using Microsoft.EntityFrameworkCore;
using OpportunityPilot.Application.Abstractions;
using OpportunityPilot.Application.Common;
using OpportunityPilot.Domain.Staffing;

namespace OpportunityPilot.Application.Staffing;

/// <param name="Kind">Joined, Learning, Certification, Project, InterviewPrep, Note (logged) or Interview, Resume, Offer (from the pipeline).</param>
public sealed record TimelineItemDto(string Kind, string Title, string? Detail, DateTime At, string Source, Guid? EntryId);
public sealed record FitDto(string Role, int Score);
/// <param name="Status">priority, available, interviewing, mapped or training (see <see cref="JourneyService.StatusOf"/>).</param>
public sealed record JourneyDto(Guid CandidateId, string Name, string? Headline, string? Skills, DateTime JoinedAt, int Day, string? Salesperson,
    bool Priority, string Status, int? ResumeStrength, FitDto? BestFit, IReadOnlyList<string> Certifications, int LearningHours,
    IReadOnlyList<TimelineItemDto> Timeline, IReadOnlyList<TimelineItemDto> Upcoming);
public sealed record BenchRowDto(Guid CandidateId, string Name, string? Headline, int Day, string? Salesperson, bool Priority, string Status,
    int? ResumeStrength, FitDto? BestFit, IReadOnlyList<string> Certifications, TimelineItemDto? Next);
public sealed record BenchDto(IReadOnlyList<BenchRowDto> Rows, int? AverageStrength);
public sealed record JourneyProfileRequest(DateTime? JoinedAt, string? Salesperson, bool Priority);
public sealed record JourneyEntryRequest(string? Kind, string? Title, string? Detail, DateTime? At, int? Hours);

/// <summary>
/// Candidate journey and Sales resource 360 (2026-10-26): from the day a resource joins to project onboarding, one timeline built from what
/// they log (learning, certifications, projects, prep) and what the pipeline already knows (interviews, resumes, offers), plus the bench view.
/// </summary>
public sealed class JourneyService(IAppDbContext db, ICurrentUser user, ProgressViewService progress, TimeProvider clock)
{
    private sealed record Facts(StaffingCandidate Candidate, List<CandidateJourneyEntry> Entries, List<StaffingInterview> Interviews,
        List<CandidateResume> Resumes, List<StaffingOffer> Offers, Dictionary<Guid, string> DealTitles);

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    private static RequestValidationException Invalid(string field, string message) => new(new Dictionary<string, string[]> { [field] = [message] });

    private async Task<Dictionary<Guid, Facts>> FactsAsync(Guid ownerId, IReadOnlyCollection<StaffingCandidate> candidates, CancellationToken ct)
    {
        var ids = candidates.Select(c => c.Id).ToList();
        var entries = await db.CandidateJourneyEntries.AsNoTracking().Where(e => e.OwnerId == ownerId && ids.Contains(e.CandidateId)).ToListAsync(ct);
        var subs = await db.StaffingSubmissions.AsNoTracking().Where(s => s.OwnerId == ownerId && ids.Contains(s.CandidateId)).Select(s => new { s.Id, s.CandidateId, s.DealId }).ToListAsync(ct);
        var subIds = subs.Select(s => s.Id).ToList();
        var interviews = await db.StaffingInterviews.AsNoTracking().Where(i => i.OwnerId == ownerId && subIds.Contains(i.SubmissionId)).ToListAsync(ct);
        var offers = await db.StaffingOffers.AsNoTracking().Where(o => o.OwnerId == ownerId && subIds.Contains(o.SubmissionId)).ToListAsync(ct);
        var resumes = await db.CandidateResumes.AsNoTracking().Where(r => r.OwnerId == ownerId && ids.Contains(r.CandidateId)).ToListAsync(ct);
        var dealIds = subs.Select(s => s.DealId).Concat(resumes.Select(r => r.DealId)).Distinct().ToList();
        var titles = await db.StaffingDeals.AsNoTracking().Where(d => d.OwnerId == ownerId && dealIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.Title, ct);
        return candidates.ToDictionary(c => c.Id, c =>
        {
            var mine = subs.Where(s => s.CandidateId == c.Id).Select(s => s.Id).ToHashSet();
            return new Facts(c, entries.Where(e => e.CandidateId == c.Id).ToList(), interviews.Where(i => mine.Contains(i.SubmissionId)).ToList(),
                resumes.Where(r => r.CandidateId == c.Id).ToList(), offers.Where(o => mine.Contains(o.SubmissionId)).ToList(), titles);
        });
    }

    /// <summary>mapped (offer accepted) → interviewing (an interview requested or scheduled) → training (learning logged in the last 14 days,
    /// nothing submitted yet) → available. Priority is a flag the team sets on top.</summary>
    public static string StatusOf(StaffingCandidate c, IReadOnlyList<StaffingInterview> interviews, IReadOnlyList<StaffingOffer> offers,
        IReadOnlyList<CandidateJourneyEntry> entries, DateTime now)
    {
        if (offers.Any(o => o.State == OfferState.Accepted)) return "mapped";
        if (interviews.Any(i => i.State is InterviewState.Requested or InterviewState.Scheduled)) return "interviewing";
        if (interviews.Count == 0 && entries.Any(e => e.Kind is JourneyKind.Learning or JourneyKind.Certification && e.OccurredAt > now.AddDays(-14) && e.OccurredAt <= now)) return "training";
        return "available";
    }

    private static int? Strength(Facts f) =>
        f.Resumes.Where(r => r.SalesScore is not null).Select(r => r.SalesScore).Max() ?? f.Resumes.Where(r => r.MatchScore is not null).Select(r => r.MatchScore).Max();

    private static FitDto? BestFit(Facts f) => f.Resumes.Where(r => r.MatchScore is not null).OrderByDescending(r => r.MatchScore)
        .Select(r => new FitDto(f.DealTitles.GetValueOrDefault(r.DealId) ?? "Role", r.MatchScore!.Value)).FirstOrDefault();

    private static string KindName(JourneyKind k) => k == JourneyKind.InterviewPrep ? "InterviewPrep" : k.ToString();

    private List<TimelineItemDto> Items(Facts f)
    {
        var c = f.Candidate;
        var items = new List<TimelineItemDto> { new("Joined", "Joined", c.AlignedSalesperson is null ? null : $"Aligned with {c.AlignedSalesperson}.", c.JoinedAt ?? c.CreatedAt, "System", null) };
        items.AddRange(f.Entries.Select(e => new TimelineItemDto(KindName(e.Kind), e.Title, e.Hours is > 0 ? $"{e.Detail}{(e.Detail is null ? "" : " · ")}{e.Hours} h".Trim() : e.Detail,
            e.OccurredAt, e.AddedBy.ToString(), e.Id)));
        foreach (var i in f.Interviews)
        {
            var deal = f.DealTitles.GetValueOrDefault(i.DealId) ?? "a role";
            var what = i.State switch
            {
                InterviewState.Scheduled => $"Interview round {i.Round} · {deal}",
                InterviewState.Completed => $"Interview round {i.Round} done · {deal}",
                InterviewState.Requested => $"Client asked for interview round {i.Round} · {deal}",
                InterviewState.Cancelled => $"Interview round {i.Round} cancelled · {deal}",
                _ => $"Interview round {i.Round} · {deal}",
            };
            items.Add(new TimelineItemDto("Interview", what, i.Mode?.ToString(), i.ScheduledAt ?? i.CreatedAt, "Pipeline", null));
        }
        foreach (var r in f.Resumes.Where(r => r.Status != ResumeStatus.Draft))
        {
            var deal = f.DealTitles.GetValueOrDefault(r.DealId) ?? "a role";
            items.Add(r.Status == ResumeStatus.Reviewed
                ? new TimelineItemDto("Resume", $"Resume for {deal} scored {r.SalesScore}/100", r.MatchScore is null ? null : $"JD match {r.MatchScore}%", r.ReviewedAt ?? r.UpdatedAt, "Pipeline", null)
                : new TimelineItemDto("Resume", $"Resume submitted for {deal}", r.MatchScore is null ? null : $"JD match {r.MatchScore}%", r.SubmittedAt ?? r.UpdatedAt, "Pipeline", null));
        }
        foreach (var o in f.Offers.Where(o => o.State is OfferState.Extended or OfferState.Accepted))
            items.Add(new TimelineItemDto("Offer", o.State == OfferState.Accepted ? "Offer accepted: mapped to a project" : "Offer made", null, o.UpdatedAt, "Pipeline", null));
        return items;
    }

    private JourneyDto Journey(Facts f, bool forCandidate)
    {
        var c = f.Candidate;
        var now = Now;
        var joined = c.JoinedAt ?? c.CreatedAt;
        var items = Items(f);
        var past = items.Where(i => i.At <= now).OrderByDescending(i => i.At).ToList();
        var upcoming = items.Where(i => i.At > now).OrderBy(i => i.At).Take(20).ToList();
        return new JourneyDto(c.Id, c.Name, c.Headline, c.Skills, joined, Math.Max(1, (int)(now.Date - joined.Date).TotalDays + 1), c.AlignedSalesperson,
            !forCandidate && c.Priority, StatusOf(c, f.Interviews, f.Offers, f.Entries, now), Strength(f), BestFit(f),
            f.Entries.Where(e => e.Kind == JourneyKind.Certification && e.OccurredAt <= now).OrderBy(e => e.OccurredAt).Select(e => e.Title).ToList(),
            f.Entries.Where(e => e.OccurredAt <= now).Sum(e => e.Hours ?? 0), past, upcoming);
    }

    private async Task<StaffingCandidate> CandidateAsync(Guid id, CancellationToken ct) =>
        await db.StaffingCandidates.FirstOrDefaultAsync(c => c.Id == id && c.OwnerId == user.OwnerId, ct) ?? throw new NotFoundException("Candidate not found.");

    // ---------- Sales ----------

    public async Task<JourneyDto> GetAsync(Guid candidateId, CancellationToken ct)
    {
        var c = await CandidateAsync(candidateId, ct);
        return Journey((await FactsAsync(user.OwnerId, [c], ct))[c.Id], forCandidate: false);
    }

    public async Task<BenchDto> BenchAsync(CancellationToken ct)
    {
        var candidates = await db.StaffingCandidates.AsNoTracking().Where(c => c.OwnerId == user.OwnerId).OrderBy(c => c.Name).Take(500).ToListAsync(ct);
        var facts = await FactsAsync(user.OwnerId, candidates, ct);
        var now = Now;
        var rows = candidates.Select(c =>
        {
            var f = facts[c.Id];
            var joined = c.JoinedAt ?? c.CreatedAt;
            var next = Items(f).Where(i => i.At > now).OrderBy(i => i.At).FirstOrDefault();
            return new BenchRowDto(c.Id, c.Name, c.Headline, Math.Max(1, (int)(now.Date - joined.Date).TotalDays + 1), c.AlignedSalesperson, c.Priority,
                StatusOf(c, f.Interviews, f.Offers, f.Entries, now), Strength(f), BestFit(f),
                f.Entries.Where(e => e.Kind == JourneyKind.Certification && e.OccurredAt <= now).Select(e => e.Title).ToList(), next);
        }).ToList();
        var strengths = rows.Where(r => r.ResumeStrength is not null).Select(r => r.ResumeStrength!.Value).ToList();
        return new BenchDto(rows, strengths.Count == 0 ? null : (int)Math.Round(strengths.Average()));
    }

    public async Task<JourneyDto> SetProfileAsync(Guid candidateId, JourneyProfileRequest request, CancellationToken ct)
    {
        var c = await CandidateAsync(candidateId, ct);
        if (request is null) throw Invalid("joinedAt", "Send the journey profile.");
        if (request.JoinedAt is { } d && (d > Now.AddDays(1) || d < new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc))) throw Invalid("joinedAt", "The joining date looks wrong.");
        try { c.SetJourneyProfile(request.JoinedAt, request.Salesperson?.Trim(), request.Priority); }
        catch (ArgumentException e) { throw Invalid("salesperson", e.Message); }
        await db.SaveChangesAsync(ct);
        return await GetAsync(candidateId, ct);
    }

    private CandidateJourneyEntry NewEntry(Guid ownerId, Guid candidateId, JourneyEntryRequest? request, JourneyAuthor author)
    {
        if (request is null || !Enum.TryParse<JourneyKind>(request.Kind, true, out var kind) || !Enum.IsDefined(kind))
            throw Invalid("kind", "Choose Learning, Certification, Project, InterviewPrep or Note.");
        if (string.IsNullOrWhiteSpace(request.Title)) throw Invalid("title", "Give it a title.");
        var at = request.At ?? Now;
        if (at > Now.AddDays(366) || at < Now.AddYears(-10)) throw Invalid("at", "The date looks wrong.");
        try { return new CandidateJourneyEntry(ownerId, candidateId, kind, request.Title.Trim(), request.Detail?.Trim(), at, request.Hours, author, Now); }
        catch (ArgumentException e) { throw Invalid("title", e.Message); }
    }

    public async Task<JourneyDto> AddEntryAsync(Guid candidateId, JourneyEntryRequest request, CancellationToken ct)
    {
        await CandidateAsync(candidateId, ct);
        db.CandidateJourneyEntries.Add(NewEntry(user.OwnerId, candidateId, request, JourneyAuthor.Sales));
        await db.SaveChangesAsync(ct);
        return await GetAsync(candidateId, ct);
    }

    public async Task DeleteEntryAsync(Guid entryId, CancellationToken ct)
    {
        var e = await db.CandidateJourneyEntries.FirstOrDefaultAsync(x => x.Id == entryId && x.OwnerId == user.OwnerId, ct) ?? throw new NotFoundException("Entry not found.");
        db.CandidateJourneyEntries.Remove(e);
        await db.SaveChangesAsync(ct);
    }

    // ---------- Candidate, through the private progress link ----------

    private async Task<(CandidateProgressLink Link, StaffingCandidate Candidate)> SharedAsync(string token, CancellationToken ct)
    {
        var (link, candidate) = await progress.LinkAsync(token, ct);
        if (!link.Show.HasFlag(ProgressShow.Journey)) throw new NotFoundException("The journey is not shared on this link.");
        return (link, candidate);
    }

    public async Task<JourneyDto> PublicAsync(string token, CancellationToken ct)
    {
        var (link, candidate) = await SharedAsync(token, ct);
        return Journey((await FactsAsync(link.OwnerId, [candidate], ct))[candidate.Id], forCandidate: true);
    }

    public async Task<JourneyDto> PublicAddAsync(string token, JourneyEntryRequest request, CancellationToken ct)
    {
        var (link, candidate) = await SharedAsync(token, ct);
        var count = await db.CandidateJourneyEntries.CountAsync(e => e.OwnerId == link.OwnerId && e.CandidateId == candidate.Id, ct);
        if (count >= 500) throw Invalid("title", "Your journey is full (500 entries). Ask your recruiter to tidy it.");
        db.CandidateJourneyEntries.Add(NewEntry(link.OwnerId, candidate.Id, request, JourneyAuthor.Candidate));
        await db.SaveChangesAsync(ct);
        return Journey((await FactsAsync(link.OwnerId, [candidate], ct))[candidate.Id], forCandidate: true);
    }
}
