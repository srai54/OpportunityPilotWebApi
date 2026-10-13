using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using OpportunityPilot.Application.Abstractions;
using OpportunityPilot.Application.Common;
using OpportunityPilot.Domain.Staffing;

namespace OpportunityPilot.Application.Staffing;

/// <param name="Show">Any of Client, Role, Interviews, Offer. The stage is always shown.</param>
public sealed record CreateProgressLinkRequest(IReadOnlyList<string>? Show);

/// <param name="Token">Only set when the link was just created: it is not stored and cannot be shown again.</param>
public sealed record ProgressLinkDto(bool Active, IReadOnlyList<string> Show, DateTime? CreatedAt, string? Token);

public sealed record ProgressInterviewDto(int Round, DateTime ScheduledAt, string? TimeZone, string? Mode, string? Location, int? Minutes, string? Notes);
public sealed record ProgressItemDto(string? Client, string? Role, string Stage, DateTime UpdatedAt, ProgressInterviewDto? Interview);
public sealed record ProgressViewDto(string FirstName, DateTime AsOf, IReadOnlyList<ProgressItemDto> Items);

/// <summary>The sales team's side: make, look at and revoke a candidate's private progress link.</summary>
public sealed class ProgressLinkService(IAppDbContext db, ICurrentUser user, TimeProvider clock)
{
    public static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    public static IReadOnlyList<string> Names(ProgressShow s) => Enum.GetValues<ProgressShow>().Where(f => f != ProgressShow.None && s.HasFlag(f)).Select(f => f.ToString()).ToList();

    private async Task<StaffingCandidate> CandidateAsync(Guid id, CancellationToken ct) =>
        await db.StaffingCandidates.FirstOrDefaultAsync(c => c.Id == id && c.OwnerId == user.OwnerId, ct) ?? throw new NotFoundException("Candidate not found.");

    public async Task<ProgressLinkDto> GetAsync(Guid candidateId, CancellationToken ct)
    {
        await CandidateAsync(candidateId, ct);
        var link = await db.CandidateProgressLinks.Where(l => l.OwnerId == user.OwnerId && l.CandidateId == candidateId && l.RevokedAt == null)
            .OrderByDescending(l => l.CreatedAt).FirstOrDefaultAsync(ct);
        return link is null ? new ProgressLinkDto(false, [], null, null) : new ProgressLinkDto(true, Names(link.Show), link.CreatedAt, null);
    }

    /// <summary>Makes a new link and revokes the old one, so there is only ever one live link per candidate.</summary>
    public async Task<ProgressLinkDto> CreateAsync(Guid candidateId, CreateProgressLinkRequest request, CancellationToken ct)
    {
        await CandidateAsync(candidateId, ct);
        var show = ProgressShow.None;
        var errors = new Dictionary<string, string[]>();
        foreach (var name in request?.Show ?? [])
        {
            if (Enum.TryParse<ProgressShow>(name, true, out var f) && f != ProgressShow.None && Enum.IsDefined(f)) show |= f;
            else errors["show"] = [$"Unknown item '{name}'. Use {string.Join(", ", Names((ProgressShow)15))}."];
        }
        if (errors.Count > 0) throw new RequestValidationException(errors);
        var now = clock.GetUtcNow().UtcDateTime;
        foreach (var old in await db.CandidateProgressLinks.Where(l => l.OwnerId == user.OwnerId && l.CandidateId == candidateId && l.RevokedAt == null).ToListAsync(ct)) old.Revoke(now);
        var token = NewToken();
        db.CandidateProgressLinks.Add(new CandidateProgressLink(user.OwnerId, candidateId, Hash(token), show, now));
        await db.SaveChangesAsync(ct);
        return new ProgressLinkDto(true, Names(show), now, token);
    }

    public async Task RevokeAsync(Guid candidateId, CancellationToken ct)
    {
        await CandidateAsync(candidateId, ct);
        var now = clock.GetUtcNow().UtcDateTime;
        foreach (var l in await db.CandidateProgressLinks.Where(l => l.OwnerId == user.OwnerId && l.CandidateId == candidateId && l.RevokedAt == null).ToListAsync(ct)) l.Revoke(now);
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>The candidate's side: what the link shows. No sign-in; the unguessable token is the key, and only what was chosen is returned.</summary>
public sealed class ProgressViewService(IAppDbContext db, TimeProvider clock)
{
    public async Task<ProgressViewDto> ViewAsync(string token, CancellationToken ct)
    {
        var hash = ProgressLinkService.Hash(token ?? string.Empty);
        var link = await db.CandidateProgressLinks.FirstOrDefaultAsync(l => l.TokenHash == hash && l.RevokedAt == null, ct) ?? throw new NotFoundException("This link is not active.");
        var candidate = await db.StaffingCandidates.FirstOrDefaultAsync(c => c.Id == link.CandidateId && c.OwnerId == link.OwnerId, ct) ?? throw new NotFoundException("This link is not active.");
        if (candidate.Consent == CandidateConsent.Withdrawn) throw new NotFoundException("This link is not active.");

        var submissions = await db.StaffingSubmissions.Where(s => s.OwnerId == link.OwnerId && s.CandidateId == candidate.Id).OrderByDescending(s => s.UpdatedAt).Take(50).ToListAsync(ct);
        var ids = submissions.Select(s => s.Id).ToList();
        var dealIds = submissions.Select(s => s.DealId).Distinct().ToList();
        var deals = await db.StaffingDeals.Where(d => d.OwnerId == link.OwnerId && dealIds.Contains(d.Id)).ToListAsync(ct);
        var accountIds = deals.Select(d => d.AccountId).Distinct().ToList();
        var accounts = await db.StaffingAccounts.Where(a => a.OwnerId == link.OwnerId && accountIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.Name, ct);
        var interviews = await db.StaffingInterviews.Where(i => i.OwnerId == link.OwnerId && ids.Contains(i.SubmissionId)).ToListAsync(ct);
        var offers = await db.StaffingOffers.Where(o => o.OwnerId == link.OwnerId && ids.Contains(o.SubmissionId)).ToListAsync(ct);

        var items = new List<ProgressItemDto>();
        foreach (var s in submissions)
        {
            var deal = deals.FirstOrDefault(d => d.Id == s.DealId);
            var offer = link.Show.HasFlag(ProgressShow.Offer) ? offers.Where(o => o.SubmissionId == s.Id && o.State != OfferState.Draft).OrderByDescending(o => o.UpdatedAt).FirstOrDefault() : null;
            var interview = link.Show.HasFlag(ProgressShow.Interviews) ? interviews.Where(i => i.SubmissionId == s.Id).OrderByDescending(i => i.Round).FirstOrDefault() : null;
            var (stage, at) = offer is not null ? (OfferStage(offer.State), offer.UpdatedAt)
                : interview is not null ? (InterviewStage(interview.State), interview.UpdatedAt)
                : (SubmissionStage(s.State), s.UpdatedAt);
            var detail = interview is { State: InterviewState.Scheduled, ScheduledAt: not null } && offer is null
                ? new ProgressInterviewDto(interview.Round, interview.ScheduledAt.Value, interview.TimeZone, interview.Mode?.ToString(), interview.Location, interview.DurationMinutes, interview.CandidateNotes) : null;
            string? clientName = null;
            if (link.Show.HasFlag(ProgressShow.Client) && deal is not null) accounts.TryGetValue(deal.AccountId, out clientName);
            items.Add(new ProgressItemDto(clientName, link.Show.HasFlag(ProgressShow.Role) ? deal?.Title : null, stage, at, detail));
        }
        var first = candidate.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? candidate.Name;
        return new ProgressViewDto(first, clock.GetUtcNow().UtcDateTime, items);
    }

    private static string SubmissionStage(SubmissionState s) => s switch
    {
        SubmissionState.Sent => "Your profile was sent to the client",
        SubmissionState.Withdrawn => "Withdrawn",
        _ => "We are preparing your profile"
    };

    private static string InterviewStage(InterviewState s) => s switch
    {
        InterviewState.Requested => "The client asked for an interview",
        InterviewState.Scheduled => "Interview scheduled",
        InterviewState.Completed => "Interview done, waiting for the client's answer",
        InterviewState.Cancelled => "Interview cancelled",
        _ => "Interview closed"
    };

    private static string OfferStage(OfferState s) => s switch
    {
        OfferState.Extended => "An offer was made",
        OfferState.Accepted => "Offer accepted",
        OfferState.Declined => "Offer declined",
        _ => "Offer withdrawn"
    };
}
