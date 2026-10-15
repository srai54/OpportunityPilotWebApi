using OpportunityPilot.Application.Abstractions;
using OpportunityPilot.Application.Common;
using OpportunityPilot.Application.Opportunities;
using OpportunityPilot.Domain.Drafts;
using OpportunityPilot.Domain.Opportunities;

namespace OpportunityPilot.Application.Drafts;

/// <param name="Outcome">Written (a draft now exists, still to be approved), Skipped (nothing was written, see Reason).</param>
public sealed record PreparedDraftDto(Guid OpportunityId, string Title, string Outcome, string? Reason, Guid? DraftId, string? Recipient);

public sealed record PrepareDraftsResult(int Written, int Skipped, int Remaining, IReadOnlyList<PreparedDraftDto> Items);

/// <summary>Writes email drafts for a campaign's shortlisted leads (Sales hands-off, step one).</summary>
public interface IDraftPreparer
{
    Task<PrepareDraftsResult> PrepareAsync(Guid campaignId, CancellationToken ct);
}

/// <summary>
/// Sales hands-off, step one: for the shortlisted leads of a campaign that have no email yet, find the contact address the company itself
/// published (never guessed) and write the email. Nothing is approved or sent here; the person still reviews and approves, then sends.
/// </summary>
public sealed class DraftPreparer(IDraftRepository repository, ICurrentUser user, ContactFinder contacts, IDraftService drafts) : IDraftPreparer
{
    /// <summary>One run looks at this many leads, because finding a contact can read the company's own pages.</summary>
    public const int MaxPerRun = 15;

    public async Task<PrepareDraftsResult> PrepareAsync(Guid campaignId, CancellationToken ct)
    {
        var owner = user.OwnerId;
        if (!await repository.CampaignExistsAsync(owner, campaignId, ct)) throw new NotFoundException("Campaign not found.");

        var pending = await repository.ShortlistedWithoutEmailDraftAsync(owner, campaignId, ct);

        var items = new List<PreparedDraftDto>();
        foreach (var o in pending.Take(MaxPerRun))
        {
            var email = await BestEmailAsync(o.Id, ct);
            if (email is null)
            {
                items.Add(new(o.Id, o.Title, "Skipped", "No email address is published for this company. Add one by hand, or use its contact form.", null, null));
                continue;
            }
            try
            {
                var created = await drafts.CreateAsync(o.Id, new CreateDraftRequest(DraftChannel.Email, email), ct);
                var subject = string.IsNullOrWhiteSpace(o.Organization) ? "Introduction" : $"Introduction for {o.Organization}";
                var saved = await drafts.UpdateAsync(created.Id, new UpdateDraftRequest(email, subject, created.Body, created.Version), ct);
                items.Add(new(o.Id, o.Title, "Written", null, saved.Id, email));
            }
            catch (Exception ex) when (ex is ConflictException or RequestValidationException or NotFoundException)
            {
                repository.ClearTracked();
                var reason = ex is RequestValidationException v ? string.Join(" ", v.Errors.SelectMany(e => e.Value)) : ex.Message;
                items.Add(new(o.Id, o.Title, "Skipped", reason, null, email));
            }
        }
        return new PrepareDraftsResult(items.Count(i => i.Outcome == "Written"), items.Count(i => i.Outcome == "Skipped"), Math.Max(0, pending.Count - MaxPerRun), items);
    }

    private async Task<string?> BestEmailAsync(Guid opportunityId, CancellationToken ct)
    {
        try
        {
            var found = await contacts.FindAsync(opportunityId, null, ct);
            return found.Candidates.Where(c => c.Kind == "Email").OrderByDescending(c => c.Rank).Select(c => c.Value).FirstOrDefault();
        }
        catch (Exception ex) when (ex is RequestValidationException or HttpRequestException) { return null; }
    }
}
