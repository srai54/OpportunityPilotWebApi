using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using OpportunityPilot.Application.Abstractions;
using OpportunityPilot.Application.Common;
using OpportunityPilot.Domain.Staffing;

namespace OpportunityPilot.Application.Staffing;

/// <param name="Show">Any of Client, Role, Interviews, Offer, Note, Prep, Roles. The stage is always shown.</param>
/// <param name="Note">The recruiter's note (shown with Note).</param>
/// <param name="RoleIds">Deals to suggest (shown with Roles). Null keeps the current suggestions; a list replaces them, keeping answers already given.</param>
public sealed record CreateProgressLinkRequest(IReadOnlyList<string>? Show, string? Note = null, IReadOnlyList<Guid>? RoleIds = null);

public sealed record SetInterviewPrepRequest(string? Prep);

public sealed record SuggestedRoleDto(Guid Id, Guid DealId, string Title, string Answer, DateTime? AnsweredAt);
public sealed record ShareInterviewDto(Guid Id, int Round, string Title, DateTime ScheduledAt, string? Prep);

/// <param name="Token">Only set when the link was just created: it is not stored and cannot be shown again.</param>
public sealed record ProgressLinkDto(bool Active, IReadOnlyList<string> Show, DateTime? CreatedAt, string? Token,
    string? Note = null, IReadOnlyList<SuggestedRoleDto>? Roles = null, IReadOnlyList<ShareInterviewDto>? Interviews = null);

public sealed record ProgressInterviewDto(int Round, DateTime ScheduledAt, string? TimeZone, string? Mode, string? Location, int? Minutes, string? Notes, string? Prep = null);
public sealed record ProgressItemDto(string? Client, string? Role, string Stage, DateTime UpdatedAt, ProgressInterviewDto? Interview);
public sealed record ProgressRoleDto(Guid Id, string Title, string? Client, string Answer);
public sealed record ProgressViewDto(string FirstName, DateTime AsOf, IReadOnlyList<ProgressItemDto> Items, string? Note = null, IReadOnlyList<ProgressRoleDto>? Roles = null);

public sealed record RoleAnswerRequest(string? Answer);

/// <summary>The sales team's side: make, look at and revoke a candidate's private progress link.</summary>
public sealed class ProgressLinkService(IAppDbContext db, ICurrentUser user, TimeProvider clock)
{
    private const ProgressShow All = ProgressShow.Client | ProgressShow.Role | ProgressShow.Interviews | ProgressShow.Offer | ProgressShow.Note | ProgressShow.Prep | ProgressShow.Roles | ProgressShow.Resume | ProgressShow.Journey;

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
        var roles = await RolesAsync(candidateId, ct);
        var interviews = await UpcomingInterviewsAsync(candidateId, ct);
        return link is null
            ? new ProgressLinkDto(false, [], null, null, null, roles, interviews)
            : new ProgressLinkDto(true, Names(link.Show), link.CreatedAt, null, link.Note, roles, interviews);
    }

    /// <summary>Makes a new link and revokes the old one, so there is only ever one live link per candidate.</summary>
    public async Task<ProgressLinkDto> CreateAsync(Guid candidateId, CreateProgressLinkRequest request, CancellationToken ct)
    {
        await CandidateAsync(candidateId, ct);
        var (show, note, roleIds) = await ValidateAsync(request, ct);
        var now = clock.GetUtcNow().UtcDateTime;
        foreach (var old in await db.CandidateProgressLinks.Where(l => l.OwnerId == user.OwnerId && l.CandidateId == candidateId && l.RevokedAt == null).ToListAsync(ct)) old.Revoke(now);
        if (roleIds is not null)
        {
            var current = await db.ProgressRoleSuggestions.Where(r => r.OwnerId == user.OwnerId && r.CandidateId == candidateId).ToListAsync(ct);
            db.ProgressRoleSuggestions.RemoveRange(current.Where(r => !roleIds.Contains(r.DealId)));
            foreach (var id in roleIds.Where(id => current.All(r => r.DealId != id)))
                db.ProgressRoleSuggestions.Add(new ProgressRoleSuggestion(user.OwnerId, candidateId, id, now));
        }
        var token = NewToken();
        db.CandidateProgressLinks.Add(new CandidateProgressLink(user.OwnerId, candidateId, Hash(token), show, now, note));
        await db.SaveChangesAsync(ct);
        return new ProgressLinkDto(true, Names(show), now, token, note, await RolesAsync(candidateId, ct), await UpcomingInterviewsAsync(candidateId, ct));
    }

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private async Task<(ProgressShow Show, string? Note, List<Guid>? RoleIds)> ValidateAsync(CreateProgressLinkRequest? request, CancellationToken ct)
    {
        var show = ProgressShow.None;
        var errors = new Dictionary<string, string[]>();
        foreach (var name in request?.Show ?? [])
        {
            if (Enum.TryParse<ProgressShow>(name, true, out var f) && f != ProgressShow.None && Enum.IsDefined(f)) show |= f;
            else errors["show"] = [$"Unknown item '{name}'. Use {string.Join(", ", Names(All))}."];
        }
        var note = Clean(request?.Note);
        if (note is { Length: > CandidateProgressLink.MaxNoteLength }) errors["note"] = [$"Keep the note under {CandidateProgressLink.MaxNoteLength} characters."];
        var roleIds = request?.RoleIds?.Distinct().ToList();
        if (roleIds is { Count: > ProgressRoleSuggestion.MaxPerCandidate }) errors["roleIds"] = [$"Suggest at most {ProgressRoleSuggestion.MaxPerCandidate} roles."];
        else if (roleIds is { Count: > 0 } && await db.StaffingDeals.CountAsync(d => d.OwnerId == user.OwnerId && roleIds.Contains(d.Id), ct) != roleIds.Count)
            errors["roleIds"] = ["One of the roles was not found."];
        if (errors.Count > 0) throw new RequestValidationException(errors);
        return (show, note, roleIds);
    }

    public async Task RevokeAsync(Guid candidateId, CancellationToken ct)
    {
        await CandidateAsync(candidateId, ct);
        var now = clock.GetUtcNow().UtcDateTime;
        foreach (var l in await db.CandidateProgressLinks.Where(l => l.OwnerId == user.OwnerId && l.CandidateId == candidateId && l.RevokedAt == null).ToListAsync(ct)) l.Revoke(now);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Prep tips for one interview. The link keeps working; the candidate sees the new tips on the next refresh.</summary>
    public async Task<ShareInterviewDto> SetPrepAsync(Guid interviewId, SetInterviewPrepRequest request, CancellationToken ct)
    {
        var interview = await db.StaffingInterviews.FirstOrDefaultAsync(i => i.Id == interviewId && i.OwnerId == user.OwnerId, ct) ?? throw new NotFoundException("Interview not found.");
        var prep = Clean(request?.Prep);
        if (prep is { Length: > StaffingInterview.MaxNotesLength })
            throw new RequestValidationException(new Dictionary<string, string[]> { ["prep"] = [$"Keep the prep under {StaffingInterview.MaxNotesLength} characters."] });
        interview.SetCandidatePrep(prep);
        await db.SaveChangesAsync(ct);
        var title = await db.StaffingDeals.Where(d => d.Id == interview.DealId).Select(d => d.Title).FirstOrDefaultAsync(ct) ?? "";
        return new ShareInterviewDto(interview.Id, interview.Round, title, interview.ScheduledAt ?? default, interview.CandidatePrep);
    }

    private async Task<IReadOnlyList<SuggestedRoleDto>> RolesAsync(Guid candidateId, CancellationToken ct)
    {
        var rows = await db.ProgressRoleSuggestions.AsNoTracking().Where(r => r.OwnerId == user.OwnerId && r.CandidateId == candidateId).OrderBy(r => r.SuggestedAt).ToListAsync(ct);
        if (rows.Count == 0) return [];
        var ids = rows.Select(r => r.DealId).ToList();
        var titles = await db.StaffingDeals.Where(d => d.OwnerId == user.OwnerId && ids.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.Title, ct);
        return rows.Where(r => titles.ContainsKey(r.DealId)).Select(r => new SuggestedRoleDto(r.Id, r.DealId, titles[r.DealId], r.Answer.ToString(), r.AnsweredAt)).ToList();
    }

    private async Task<IReadOnlyList<ShareInterviewDto>> UpcomingInterviewsAsync(Guid candidateId, CancellationToken ct)
    {
        var subIds = await db.StaffingSubmissions.Where(s => s.OwnerId == user.OwnerId && s.CandidateId == candidateId).Select(s => s.Id).ToListAsync(ct);
        if (subIds.Count == 0) return [];
        // Only the interviews the candidate's page shows: the latest round of each submission, when it is scheduled.
        var all = await db.StaffingInterviews.AsNoTracking().Where(i => i.OwnerId == user.OwnerId && subIds.Contains(i.SubmissionId)).ToListAsync(ct);
        var list = all.GroupBy(i => i.SubmissionId).Select(g => g.OrderByDescending(i => i.Round).First())
            .Where(i => i.State == InterviewState.Scheduled && i.ScheduledAt != null).OrderBy(i => i.ScheduledAt).Take(20).ToList();
        var dealIds = list.Select(i => i.DealId).Distinct().ToList();
        var titles = await db.StaffingDeals.Where(d => dealIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.Title, ct);
        return list.Select(i => new ShareInterviewDto(i.Id, i.Round, titles.GetValueOrDefault(i.DealId) ?? "", i.ScheduledAt!.Value, i.CandidatePrep)).ToList();
    }
}

/// <summary>The candidate's side: what the link shows. No sign-in; the unguessable token is the key, and only what was chosen is returned.</summary>
public sealed class ProgressViewService(IAppDbContext db, TimeProvider clock)
{
    public async Task<(CandidateProgressLink Link, StaffingCandidate Candidate)> LinkAsync(string token, CancellationToken ct)
    {
        var hash = ProgressLinkService.Hash(token ?? string.Empty);
        var link = await db.CandidateProgressLinks.FirstOrDefaultAsync(l => l.TokenHash == hash && l.RevokedAt == null, ct) ?? throw new NotFoundException("This link is not active.");
        var candidate = await db.StaffingCandidates.FirstOrDefaultAsync(c => c.Id == link.CandidateId && c.OwnerId == link.OwnerId, ct) ?? throw new NotFoundException("This link is not active.");
        if (candidate.Consent == CandidateConsent.Withdrawn) throw new NotFoundException("This link is not active.");
        return (link, candidate);
    }

    public async Task<ProgressViewDto> ViewAsync(string token, CancellationToken ct)
    {
        var (link, candidate) = await LinkAsync(token, ct);

        var submissions = await db.StaffingSubmissions.Where(s => s.OwnerId == link.OwnerId && s.CandidateId == candidate.Id).OrderByDescending(s => s.UpdatedAt).Take(50).ToListAsync(ct);
        var ids = submissions.Select(s => s.Id).ToList();
        var suggestions = link.Show.HasFlag(ProgressShow.Roles)
            ? await db.ProgressRoleSuggestions.Where(r => r.OwnerId == link.OwnerId && r.CandidateId == candidate.Id).OrderBy(r => r.SuggestedAt).ToListAsync(ct)
            : [];
        var dealIds = submissions.Select(s => s.DealId).Concat(suggestions.Select(r => r.DealId)).Distinct().ToList();
        var deals = await db.StaffingDeals.Where(d => d.OwnerId == link.OwnerId && dealIds.Contains(d.Id)).ToListAsync(ct);
        var accountIds = deals.Select(d => d.AccountId).Distinct().ToList();
        var accounts = await db.StaffingAccounts.Where(a => a.OwnerId == link.OwnerId && accountIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.Name, ct);
        var interviews = await db.StaffingInterviews.Where(i => i.OwnerId == link.OwnerId && ids.Contains(i.SubmissionId)).ToListAsync(ct);
        var offers = await db.StaffingOffers.Where(o => o.OwnerId == link.OwnerId && ids.Contains(o.SubmissionId)).ToListAsync(ct);
        var showPrep = link.Show.HasFlag(ProgressShow.Prep);

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
                ? new ProgressInterviewDto(interview.Round, interview.ScheduledAt.Value, interview.TimeZone, interview.Mode?.ToString(), interview.Location, interview.DurationMinutes, interview.CandidateNotes,
                    showPrep ? interview.CandidatePrep : null) : null;
            string? clientName = null;
            if (link.Show.HasFlag(ProgressShow.Client) && deal is not null) accounts.TryGetValue(deal.AccountId, out clientName);
            items.Add(new ProgressItemDto(clientName, link.Show.HasFlag(ProgressShow.Role) ? deal?.Title : null, stage, at, detail));
        }

        var roles = link.Show.HasFlag(ProgressShow.Roles) ? RolesFor(link, suggestions, deals, accounts) : null;
        var first = candidate.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? candidate.Name;
        return new ProgressViewDto(first, clock.GetUtcNow().UtcDateTime, items, link.Show.HasFlag(ProgressShow.Note) ? link.Note : null, roles);
    }

    private static List<ProgressRoleDto> RolesFor(CandidateProgressLink link, List<ProgressRoleSuggestion> suggestions, List<StaffingDeal> deals, Dictionary<Guid, string> accounts)
    {
        var roles = new List<ProgressRoleDto>();
        foreach (var r in suggestions)
        {
            var deal = deals.FirstOrDefault(d => d.Id == r.DealId);
            if (deal is null) continue;
            var client = link.Show.HasFlag(ProgressShow.Client) ? accounts.GetValueOrDefault(deal.AccountId) : null;
            roles.Add(new ProgressRoleDto(r.Id, deal.Title, client, r.Answer.ToString()));
        }
        return roles;
    }

    /// <summary>The candidate answers a suggested role. Only works while the link is live and shares roles.</summary>
    public async Task<ProgressRoleDto> AnswerRoleAsync(string token, Guid suggestionId, RoleAnswerRequest request, CancellationToken ct)
    {
        var (link, candidate) = await LinkAsync(token, ct);
        if (!link.Show.HasFlag(ProgressShow.Roles)) throw new NotFoundException("Role not found.");
        if (!Enum.TryParse<RoleAnswer>(request?.Answer, true, out var answer) || answer == RoleAnswer.None || !Enum.IsDefined(answer))
            throw new RequestValidationException(new Dictionary<string, string[]> { ["answer"] = ["Answer Interested or NotForMe."] });
        var row = await db.ProgressRoleSuggestions.FirstOrDefaultAsync(r => r.Id == suggestionId && r.OwnerId == link.OwnerId && r.CandidateId == candidate.Id, ct)
            ?? throw new NotFoundException("Role not found.");
        var deal = await db.StaffingDeals.FirstOrDefaultAsync(d => d.Id == row.DealId && d.OwnerId == link.OwnerId, ct) ?? throw new NotFoundException("Role not found.");
        row.Respond(answer, clock.GetUtcNow().UtcDateTime);
        await db.SaveChangesAsync(ct);
        string? client = null;
        if (link.Show.HasFlag(ProgressShow.Client)) client = await db.StaffingAccounts.Where(a => a.Id == deal.AccountId).Select(a => a.Name).FirstOrDefaultAsync(ct);
        return new ProgressRoleDto(row.Id, deal.Title, client, row.Answer.ToString());
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
