using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpportunityPilot.Application.Abstractions;
using OpportunityPilot.Application.Common;
using OpportunityPilot.Application.Imports;
using OpportunityPilot.Application.Research.Rules;
using OpportunityPilot.Domain.Opportunities;
using OpportunityPilot.Domain.Outreach;

namespace OpportunityPilot.Application.Opportunities;

public sealed class OpportunityService(IAppDbContext db, ICurrentUser user, TimeProvider clock)
{
    public const int DefaultTake = 50;
    public const int MaxTake = 200;
    public const int MaxExportRows = 5000;

    private IQueryable<Opportunity> Owned => db.Opportunities.Where(o => o.OwnerId == user.OwnerId);

    public async Task<OpportunityPageDto> ListAsync(
        Guid campaignId, FilterOutcome? outcome, OpportunityStatus? status, string? sort, int take, int skip, CancellationToken ct)
    {
        await EnsureCampaignAsync(campaignId, ct);
        take = Math.Clamp(take, 1, MaxTake);
        skip = Math.Max(0, skip);

        var query = Owned.Where(o => o.CampaignId == campaignId);
        if (outcome is { } oc) query = query.Where(o => o.Outcome == oc);
        if (status is { } st) query = query.Where(o => o.Status == st);

        var total = await query.CountAsync(ct);
        // "found" / "oldest" order by when each job or lead was first found, so it is clear which came first.
        var ordered = string.Equals(sort, "recent", StringComparison.OrdinalIgnoreCase)
            ? query.OrderByDescending(o => o.UpdatedAt).ThenBy(o => o.Id)
            : string.Equals(sort, "found", StringComparison.OrdinalIgnoreCase)
                ? query.OrderByDescending(o => o.CreatedAt).ThenBy(o => o.Id)
            : string.Equals(sort, "oldest", StringComparison.OrdinalIgnoreCase)
                ? query.OrderBy(o => o.CreatedAt).ThenBy(o => o.Id)
            // Qualified first, then Needs verification, then Excluded — a high-scoring excluded job must not top the list.
            : query.OrderBy(o => o.Outcome == FilterOutcome.Qualified ? 0 : o.Outcome == FilterOutcome.NeedsVerification ? 1 : 2)
                .ThenByDescending(o => o.Score).ThenByDescending(o => o.Coverage).ThenByDescending(o => o.UpdatedAt).ThenBy(o => o.Id);
        var items = await ordered.Skip(skip).Take(take).ToListAsync(ct);
        return new OpportunityPageDto(total, items.Select(ToSummary).ToList());
    }

    /// <summary>Qualified opportunities nobody has decided on yet (status New): the review queue of campaigns that do not suggest for approval, such as Sales.</summary>
    public async Task<int> CountQualifiedNewAsync(CancellationToken ct) =>
        await Owned.CountAsync(o => o.Status == OpportunityStatus.New && o.Outcome == FilterOutcome.Qualified, ct);

    public async Task<int> CountShortlistedAsync(CancellationToken ct) =>
        await Owned.CountAsync(o => o.Status == OpportunityStatus.Shortlisted, ct);

    public async Task<OpportunityDetailDto> GetAsync(Guid id, CancellationToken ct) =>
        await ToDetailAsync(await FindOwnedAsync(id, ct), ct);

    /// <summary>A user-chosen pipeline move (Applied here is the user's own confirmation). Records an activity when it changes.</summary>
    public async Task<OpportunityDetailDto> UpdateStatusAsync(Guid id, UpdateOpportunityStatusRequest request, CancellationToken ct)
    {
        if (request is null || !Enum.IsDefined(request.Status))
            throw new RequestValidationException(new Dictionary<string, string[]> { ["status"] = ["Unknown status."] });
        var opportunity = await FindOwnedAsync(id, ct);
        Activity? activity;
        try { activity = opportunity.ChangeStatus(request.Status, clock.GetUtcNow().UtcDateTime); }
        catch (InvalidOperationException ex)
        {
            throw new RequestValidationException(new Dictionary<string, string[]> { ["status"] = [ex.Message] });
        }
        if (activity is not null)
        {
            db.Activities.Add(activity);
            // Applying is only the start: remind the person to check back, unless a follow-up is already open for it.
            if (request.Status == OpportunityStatus.Applied
                && !await db.NextActions.AnyAsync(x => x.OwnerId == user.OwnerId && x.OpportunityId == opportunity.Id
                    && x.State == NextActionState.Open && x.Kind == NextActionKind.FollowUp, ct))
            {
                var now = clock.GetUtcNow().UtcDateTime;
                db.NextActions.Add(new NextAction(user.OwnerId, opportunity.Id, NextActionKind.FollowUp,
                    "Check on this application (scheduled when you marked it applied).", now.AddDays(7), "UTC", now));
            }
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                throw new ConflictException("The opportunity was updated at the same time (for example by research). Reload and try again.");
            }
        }
        return await ToDetailAsync(opportunity, ct);
    }

    /// <summary>
    /// CSV of the campaign's opportunities with evidence links and gaps. Every cell is quoted and formula-like values
    /// are neutralised, because the file is meant to be opened in a spreadsheet.
    /// </summary>
    public async Task<CsvExport> ExportAsync(Guid campaignId, CancellationToken ct)
    {
        var campaign = await db.Campaigns.FirstOrDefaultAsync(c => c.Id == campaignId && c.OwnerId == user.OwnerId, ct)
            ?? throw new NotFoundException("Campaign not found.");
        var rows = await Owned.Where(o => o.CampaignId == campaignId)
            .OrderBy(o => o.Outcome == FilterOutcome.Qualified ? 0 : o.Outcome == FilterOutcome.NeedsVerification ? 1 : 2).ThenByDescending(o => o.Score).ThenBy(o => o.Id)
            .Take(MaxExportRows)
            .ToListAsync(ct);
        var ids = rows.Select(o => o.Id).ToList();
        var evidenceUrls = (await (from l in db.OpportunityEvidence
                    join e in db.Evidence on l.EvidenceId equals e.Id
                    where ids.Contains(l.OpportunityId) && e.OwnerId == user.OwnerId
                    select new { l.OpportunityId, e.Url })
                .ToListAsync(ct))
            .GroupBy(x => x.OpportunityId)
            .ToDictionary(g => g.Key, g => string.Join(" ", g.Select(x => x.Url).Where(u => u is not null).Distinct()));

        var csv = new StringBuilder();
        csv.Append(Csv.Row(["Title", "Organization", "Location", "Url", "ApplyUrl", "Platform", "ExternalId", "Score", "Coverage",
            "Outcome", "OutcomeReason", "Status", "Gaps", "EvidenceUrls", "UpdatedAt"]));
        foreach (var o in rows)
        {
            csv.Append(Csv.Row([
                o.Title, o.Organization, o.Location, o.Url, o.ApplyUrl, o.Platform?.ToString(), o.ExternalId,
                o.Score.ToString(), o.Coverage.ToString(), o.Outcome.ToString(), o.OutcomeReason, o.Status.ToString(),
                string.Join("; ", Deserialize<List<string>>(o.GapsJson) ?? []),
                evidenceUrls.GetValueOrDefault(o.Id), o.UpdatedAt.ToString("O")
            ]));
        }
        return new CsvExport($"{FileSafe(campaign.Name)}-opportunities.csv", csv.ToString());
    }

    private async Task<Opportunity> FindOwnedAsync(Guid id, CancellationToken ct) =>
        await Owned.FirstOrDefaultAsync(o => o.Id == id, ct)
        ?? throw new NotFoundException("Opportunity not found.");

    private async Task EnsureCampaignAsync(Guid campaignId, CancellationToken ct)
    {
        if (!await db.Campaigns.AnyAsync(c => c.Id == campaignId && c.OwnerId == user.OwnerId, ct))
            throw new NotFoundException("Campaign not found.");
    }

    private async Task<OpportunityDetailDto> ToDetailAsync(Opportunity o, CancellationToken ct)
    {
        var evidence = await (from l in db.OpportunityEvidence
                join e in db.Evidence on l.EvidenceId equals e.Id
                where l.OpportunityId == o.Id && e.OwnerId == user.OwnerId
                orderby e.RetrievedAt descending
                select e)
            .ToListAsync(ct);
        var sourceIds = evidence.Select(e => e.SourceId).Distinct().ToList();
        var labels = await db.Sources.Where(s => sourceIds.Contains(s.Id) && s.OwnerId == user.OwnerId)
            .ToDictionaryAsync(s => s.Id, s => s.Label, ct);
        var activities = await db.Activities.Where(a => a.OpportunityId == o.Id && a.OwnerId == user.OwnerId)
            .OrderByDescending(a => a.OccurredAt).ThenBy(a => a.Id)
            .Take(100)
            .Select(a => new ActivityDto(a.Kind, a.OccurredAt, a.Detail))
            .ToListAsync(ct);

        return new OpportunityDetailDto(o.Id, o.CampaignId, o.Mode, o.Title, o.Organization, o.Location, o.Url, o.ApplyUrl, o.Platform,
            o.Score, o.Coverage, o.Outcome, o.OutcomeReason, o.Status, o.GapsCount, o.UpdatedAt, o.Description, o.Version,
            Deserialize<List<BreakdownRow>>(o.BreakdownJson) ?? [],
            Deserialize<List<FactRow>>(o.FactsJson) ?? [],
            Deserialize<List<string>>(o.GapsJson) ?? [],
            evidence.Select(e => new EvidenceDto(e.Id, e.SourceId, labels.GetValueOrDefault(e.SourceId), e.Url, e.RetrievedAt, e.Excerpt,
                e.ExtractionMethod)).ToList(),
            activities,
            o.LastResearchJobId is { } jobId
                ? await db.ResearchJobs.Where(j => j.Id == jobId && j.OwnerId == user.OwnerId)
                    .Select(j => new ScoredByDto(j.Id, j.CampaignVersion, j.ProfileVersion, j.StartedAt ?? j.CreatedAt)).FirstOrDefaultAsync(ct)
                : null);
    }

    public static OpportunitySummaryDto ToSummary(Opportunity o) => new(
        o.Id, o.CampaignId, o.Mode, o.Title, o.Organization, o.Location, o.Url, o.ApplyUrl, o.Platform, o.Score, o.Coverage,
        o.Outcome, o.OutcomeReason, o.Status, o.GapsCount, o.UpdatedAt, o.CreatedAt);

    private static T? Deserialize<T>(string json) where T : class
    {
        try { return JsonSerializer.Deserialize<T>(json, JsonSerializerOptions.Web); }
        catch (JsonException) { return null; }
    }

    private static string FileSafe(string name)
    {
        var chars = name.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-').ToArray();
        var slug = string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
        return slug.Length == 0 ? "campaign" : slug.Length > 60 ? slug[..60] : slug;
    }
}
