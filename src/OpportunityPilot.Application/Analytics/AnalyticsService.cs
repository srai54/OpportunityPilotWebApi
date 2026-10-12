using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpportunityPilot.Application.Abstractions;
using OpportunityPilot.Application.Common;
using OpportunityPilot.Application.Research;
using OpportunityPilot.Application.Research.Rules;
using OpportunityPilot.Domain.Applications;
using OpportunityPilot.Domain.Common;
using OpportunityPilot.Domain.Opportunities;
using OpportunityPilot.Domain.Research;

namespace OpportunityPilot.Application.Analytics;

/// <summary>
/// Overview metrics for the Candidate (Job) and Sales (Customer) workspaces, per docs/ANALYTICS_CONTRACT.md.
/// Read-only, owner-scoped, EF LINQ only (InMemory demo mode and Postgres alike), every query bounded by the owner and,
/// where the contract says so, the window. Nothing is estimated: what stored data cannot answer is null.
/// </summary>
public sealed partial class AnalyticsService(IAppDbContext db, ICurrentUser user, TimeProvider clock)
{
    public const int DefaultDays = 30;
    public const int MinDays = 1;
    public const int MaxDays = 365;
    public const int HistogramBands = 10;
    public const int TopUnknownCriteria = 5;
    public const int MaxSources = 20;
    public const int ApplicationsPerDayDays = 14;
    public const int TopIndustries = 6;
    public const string OtherIndustry = "Other";

    // The activities that prove a status was reached. Researched lines never move a status, so they are not read.
    private static readonly string[] StatusKinds =
        [ActivityKinds.StatusChanged, ActivityKinds.Suggested, ActivityKinds.Approved, ActivityKinds.Rejected, ActivityKinds.Applied];

    private const string Arrow = " → ";

    public static readonly OpportunityMode[] SalesModes = [OpportunityMode.Customer, OpportunityMode.Partner, OpportunityMode.Investor, OpportunityMode.Freelance];

    /// <param name="salesMode">Sales only: one of Customer, Partner, Investor, Freelance; null = all four.</param>
    public async Task<AnalyticsOverviewDto> OverviewAsync(string? workspace, int days, CancellationToken ct, OpportunityMode? salesMode = null)
    {
        var ws = Validate(workspace, days);
        var candidate = ws == AnalyticsWorkspace.Candidate;
        if (salesMode is { } requested && (candidate || !SalesModes.Contains(requested)))
            throw new RequestValidationException(new Dictionary<string, string[]> { ["mode"] = ["Filter by mode only in the Sales workspace: Customer, Partner, Investor or Freelance."] });
        var modes = candidate ? [OpportunityMode.Job] : salesMode is { } one ? [one] : SalesModes;
        var owner = user.OwnerId;
        var now = clock.GetUtcNow().UtcDateTime;
        var since = now.AddDays(-days);
        var today = DateOnly.FromDateTime(now);
        var firstChartDay = today.AddDays(-(ApplicationsPerDayDays - 1));
        // Candidate charts the last 14 days even when the window is shorter, so activities are read from the earlier of the two.
        var rangeStart = candidate ? Min(since, firstChartDay.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)) : since;

        var campaigns = await db.Campaigns.Where(c => c.OwnerId == owner && modes.Contains(c.Mode))
            .Select(c => new { c.Id, c.Name, c.AutoSuggestMinScore })
            .ToListAsync(ct);

        // Opportunities found in the window (the funnel cohort). Breakdown and facts only where they are read.
        var found = await db.Opportunities
            .Where(o => o.OwnerId == owner && modes.Contains(o.Mode) && o.CreatedAt >= since)
            .Select(o => new
            {
                o.Id, o.Score, o.Outcome, o.Status,
                BreakdownJson = o.Outcome != FilterOutcome.Excluded ? o.BreakdownJson : null,
                FactsJson = !candidate ? o.FactsJson : null
            })
            .ToListAsync(ct);

        var statusCounts = await db.Opportunities
            .Where(o => o.OwnerId == owner && modes.Contains(o.Mode) &&
                        (o.Status == OpportunityStatus.Suggested || o.Status == OpportunityStatus.Shortlisted))
            .GroupBy(o => o.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Status, x => x.Count, ct);

        // Status-proving activities in the range, for the funnel cohort and for "first reached X in the window".
        var rangeActivities = (await (from a in db.Activities
                    join o in db.Opportunities on a.OpportunityId equals o.Id
                    where a.OwnerId == owner && o.OwnerId == owner && modes.Contains(o.Mode) && a.OccurredAt >= rangeStart &&
                          StatusKinds.Contains(a.Kind)
                    select new { a.OpportunityId, a.Kind, a.Detail, a.OccurredAt })
                .ToListAsync(ct))
            .GroupBy(a => a.OpportunityId)
            .ToDictionary(g => g.Key, g => g.Select(a => new AnalyticsActivity(a.Kind, a.Detail, a.OccurredAt)).OrderBy(a => a.OccurredAt).ToList());

        // KPIs from the cohort.
        var qualified = found.Where(o => o.Outcome == FilterOutcome.Qualified).ToList();
        var awaiting = candidate ? statusCounts.GetValueOrDefault(OpportunityStatus.Suggested) : 0;
        var shortlisted = statusCounts.GetValueOrDefault(OpportunityStatus.Shortlisted);
        var shortlistedNotApplied = candidate ? await ShortlistedNotAppliedAsync(owner, modes, ct) : 0;

        // First reach of Applied (Candidate) or Contacted (Sales), and Responded, from stored status changes.
        Dictionary<Guid, AnalyticsActivity> appliedFirst = [], respondedFirst = [], contactedFirst = [];
        {
            if (candidate) appliedFirst = FirstReaches(rangeActivities, OpportunityStatus.Applied);
            else contactedFirst = FirstReaches(rangeActivities, OpportunityStatus.Contacted);
            respondedFirst = FirstReaches(rangeActivities, OpportunityStatus.Responded);
            var reachedIds = appliedFirst.Keys.Concat(contactedFirst.Keys).Concat(respondedFirst.Keys).Distinct().ToList();
            if (reachedIds.Count > 0)
            {
                // An opportunity that already got there before the range was not reached in it.
                var earlier = (await db.Activities
                        .Where(a => a.OwnerId == owner && reachedIds.Contains(a.OpportunityId) && a.OccurredAt < rangeStart &&
                                    StatusKinds.Contains(a.Kind))
                        .Select(a => new { a.OpportunityId, a.Kind, a.Detail, a.OccurredAt })
                        .ToListAsync(ct))
                    .GroupBy(a => a.OpportunityId)
                    .ToDictionary(g => g.Key, g => HighestRank(null, g.Select(a => new AnalyticsActivity(a.Kind, a.Detail, a.OccurredAt))));
                RemoveReachedEarlier(appliedFirst, earlier, OpportunityStatus.Applied);
                RemoveReachedEarlier(respondedFirst, earlier, OpportunityStatus.Responded);
                RemoveReachedEarlier(contactedFirst, earlier, OpportunityStatus.Contacted);
            }
        }

        var appliedInWindow = appliedFirst.Values.Where(a => a.OccurredAt >= since).ToList();
        int? applied = candidate ? appliedInWindow.Count : null;
        int? appliedByAgent = candidate ? appliedInWindow.Count(a => a.Kind == ActivityKinds.Applied) : null;
        int? appliedByYou = candidate ? appliedInWindow.Count(a => a.Kind != ActivityKinds.Applied) : null;
        int? responded = respondedFirst.Values.Count(a => a.OccurredAt >= since);
        int? contacted = candidate ? null : contactedFirst.Values.Count(a => a.OccurredAt >= since);

        var agentNeedsYou = candidate
            ? await db.JobApplications.CountAsync(a => a.OwnerId == owner && a.Status == ApplicationStatus.NeedsManual && a.OccurredAt >= since, ct)
            : 0;

        var kpis = new AnalyticsKpisDto(found.Count, qualified.Count, Rate(qualified.Count, found.Count), awaiting, shortlisted,
            shortlistedNotApplied, applied, appliedByAgent, appliedByYou, contacted, responded, Rate(responded, candidate ? applied : contacted), agentNeedsYou);

        // Funnel.
        var read = await ReadAsync(owner, modes, since, ct);
        int CountReached(OpportunityStatus stage) => found.Count(o =>
            Reached(o.Status, rangeActivities.GetValueOrDefault(o.Id) ?? [], stage));
        var funnel = new List<FunnelStageDto>
        {
            new("read", "Read", read, "Candidates read by the latest completed research run of each campaign started in the window."),
            new("found", "Found", found.Count, "Opportunities created in the window."),
            new("qualified", "Qualified", qualified.Count, "Of those, passing every hard filter (outcome Qualified).")
        };
        if (candidate)
        {
            funnel.Add(new("suggested", "Suggested", CountReached(OpportunityStatus.Suggested), "Of those found, reached Suggested or a later stage."));
            funnel.Add(new("shortlisted", "Shortlisted", CountReached(OpportunityStatus.Shortlisted), "Of those found, reached Shortlisted or a later stage."));
            funnel.Add(new("applied", "Applied", CountReached(OpportunityStatus.Applied), "Of those found, reached Applied or a later stage."));
            funnel.Add(new("responded", "Responded", CountReached(OpportunityStatus.Responded), "Of those found, reached Responded or a later stage."));
        }
        else
        {
            funnel.Add(new("shortlisted", "Shortlisted", CountReached(OpportunityStatus.Shortlisted), "Of those found, reached Shortlisted or a later stage."));
            funnel.Add(new("contacted", "Contacted", CountReached(OpportunityStatus.Contacted), "Of those found, marked Contacted (after you sent the approved message) or a later stage."));
            funnel.Add(new("responded", "Responded", CountReached(OpportunityStatus.Responded), "Of those found, reached Responded or a later stage."));
        }

        // Fit histogram and unknown criteria.
        var threshold = CommonThreshold(campaigns.Select(c => c.AutoSuggestMinScore));
        var histogram = new FitHistogramDto(Histogram(qualified.Select(o => o.Score)), threshold,
            threshold is { } t ? qualified.Count(o => o.Score >= t) : null);
        var unknown = TopUnknown(found.Where(o => o.Outcome != FilterOutcome.Excluded)
            .Select(o => Deserialize<List<BreakdownRow>>(o.BreakdownJson) ?? []));

        // Sources.
        var (sources, failingSources) = await SourcesAsync(owner, modes, ct);

        // Applications per day (Candidate).
        IReadOnlyList<ApplicationsDayDto>? perDay = null;
        if (candidate)
        {
            var appliedByDay = appliedFirst.Values.GroupBy(a => DateOnly.FromDateTime(a.OccurredAt)).ToDictionary(g => g.Key, g => g.Count());
            var repliesByDay = respondedFirst.Values.GroupBy(a => DateOnly.FromDateTime(a.OccurredAt)).ToDictionary(g => g.Key, g => g.Count());
            perDay = Enumerable.Range(0, ApplicationsPerDayDays).Select(i => firstChartDay.AddDays(i))
                .Select(d => new ApplicationsDayDto(d, appliedByDay.GetValueOrDefault(d), repliesByDay.GetValueOrDefault(d)))
                .ToList();
        }

        // Attention.
        var attention = new List<AttentionItemDto>();
        if (awaiting > 0)
            attention.Add(new(AttentionKind.Approvals, awaiting, $"{awaiting} suggested opportunit{(awaiting == 1 ? "y is" : "ies are")} waiting for your approval."));
        if (shortlistedNotApplied > 0)
            attention.Add(new(AttentionKind.ShortlistedNotApplied, shortlistedNotApplied,
                $"{shortlistedNotApplied} shortlisted job{Plural(shortlistedNotApplied)} not applied to yet."));
        if (agentNeedsYou > 0)
            attention.Add(new(AttentionKind.AgentNeedsYou, agentNeedsYou,
                $"{agentNeedsYou} application{Plural(agentNeedsYou)} the desktop agent could not finish without you in the last {days} day{Plural(days)}."));
        if (failingSources > 0)
            attention.Add(new(AttentionKind.SourceFailing, failingSources,
                $"{failingSources} source{Plural(failingSources)} could not be read on the last research run."));

        // Active research: the newest queued or running job of the workspace's campaigns (not windowed: it is "now").
        var active = await (from j in db.ResearchJobs
                join c in db.Campaigns on j.CampaignId equals c.Id
                where j.OwnerId == owner && c.OwnerId == owner && modes.Contains(c.Mode) &&
                      (j.State == ResearchJobState.Queued || j.State == ResearchJobState.Running)
                orderby j.CreatedAt descending, j.Id
                select new { j.Id, j.CampaignId, c.Name, j.State, j.Stage, j.CountsJson })
            .FirstOrDefaultAsync(ct);
        ActiveResearchDto? activeResearch = null;
        if (active is not null)
        {
            var counts = ResearchCounts.FromJson(active.CountsJson);
            activeResearch = new ActiveResearchDto(active.Id, active.CampaignId, active.Name, active.State, active.Stage,
                new ActiveResearchCountsDto(counts.Candidates, counts.Sources, counts.SourcesDone));
        }

        // Sales only.
        IReadOnlyList<IndustryCountDto>? byIndustry = null;
        IReadOnlyList<SignalCountDto>? signals = null;
        if (!candidate)
        {
            byIndustry = ByIndustry(qualified.Select(o => Deserialize<List<FactRow>>(o.FactsJson) ?? []));
            signals = Signals(found.Select(o => Deserialize<List<FactRow>>(o.FactsJson) ?? []));
        }

        SalesOutreachDto? outreach = null;
        if (!candidate)
        {
            var drafts = await (from d in db.OutreachDrafts
                    join o in db.Opportunities on d.OpportunityId equals o.Id
                    where d.OwnerId == owner && o.OwnerId == owner && modes.Contains(o.Mode)
                    select d.State).ToListAsync(ct);
            var bids = await db.SalesBids.Where(b => b.OwnerId == owner).Select(b => b.State).ToListAsync(ct);
            var openFollowUps = await (from n in db.NextActions
                    join o in db.Opportunities on n.OpportunityId equals o.Id
                    where n.OwnerId == owner && o.OwnerId == owner && modes.Contains(o.Mode) && n.State == Domain.Outreach.NextActionState.Open
                    select n.DueAt).ToListAsync(ct);
            outreach = new SalesOutreachDto(
                drafts.Count(s => s == Domain.Drafts.DraftState.Draft), drafts.Count(s => s == Domain.Drafts.DraftState.Approved),
                bids.Count(s => s == Domain.Sales.SalesBidState.Placed), bids.Count(s => s == Domain.Sales.SalesBidState.Failed),
                openFollowUps.Count(d => d >= now && d <= now.AddDays(7)), openFollowUps.Count(d => d < now),
                Rate(responded, contacted));
            if (outreach.FollowUpsOverdue > 0)
                attention.Add(new(AttentionKind.FollowUpsOverdue, outreach.FollowUpsOverdue,
                    $"{outreach.FollowUpsOverdue} follow-up{Plural(outreach.FollowUpsOverdue)} overdue."));
        }

        return new AnalyticsOverviewDto(ws, days, now, campaigns.Count, kpis, funnel, histogram, unknown, sources, perDay, attention,
            activeResearch, byIndustry, signals, outreach);
    }

    /// <summary>Shortlisted now and never applied: no activity shows the opportunity at Applied or later.</summary>
    private async Task<int> ShortlistedNotAppliedAsync(Guid owner, OpportunityMode[] modes, CancellationToken ct)
    {
        var ids = await db.Opportunities
            .Where(o => o.OwnerId == owner && modes.Contains(o.Mode) && o.Status == OpportunityStatus.Shortlisted)
            .Select(o => o.Id)
            .ToListAsync(ct);
        if (ids.Count == 0) return 0;
        var history = (await db.Activities
                .Where(a => a.OwnerId == owner && ids.Contains(a.OpportunityId) && StatusKinds.Contains(a.Kind))
                .Select(a => new { a.OpportunityId, a.Kind, a.Detail, a.OccurredAt })
                .ToListAsync(ct))
            .GroupBy(a => a.OpportunityId)
            .ToDictionary(g => g.Key, g => HighestRank(null, g.Select(a => new AnalyticsActivity(a.Kind, a.Detail, a.OccurredAt))));
        return ids.Count(id => history.GetValueOrDefault(id, -1) < RankOr(OpportunityStatus.Applied));
    }

    /// <summary>Sum of counts.candidates over the window's completed jobs, latest job per campaign only (re-runs would double count).</summary>
    private async Task<int> ReadAsync(Guid owner, OpportunityMode[] modes, DateTime since, CancellationToken ct)
    {
        var jobs = await (from j in db.ResearchJobs
                join c in db.Campaigns on j.CampaignId equals c.Id
                where j.OwnerId == owner && c.OwnerId == owner && modes.Contains(c.Mode) && j.CreatedAt >= since &&
                      (j.State == ResearchJobState.Completed || j.State == ResearchJobState.CompletedWithGaps)
                select new { j.Id, j.CampaignId, j.CreatedAt, j.CountsJson })
            .ToListAsync(ct);
        return jobs.GroupBy(j => j.CampaignId)
            .Select(g => g.OrderByDescending(j => j.CreatedAt).ThenBy(j => j.Id).First())
            .Sum(j => ResearchCounts.FromJson(j.CountsJson).Candidates);
    }

    private async Task<(IReadOnlyList<SourceYieldDto> Top, int Failing)> SourcesAsync(Guid owner, OpportunityMode[] modes, CancellationToken ct)
    {
        var sources = await (from s in db.Sources
                join c in db.Campaigns on s.CampaignId equals c.Id
                where s.OwnerId == owner && c.OwnerId == owner && modes.Contains(c.Mode)
                select new { s.Id, s.CampaignId, s.Label, s.Kind, s.Platform, s.ItemCount, s.LastFetchedAt, s.Status })
            .ToListAsync(ct);
        if (sources.Count == 0) return ([], 0);

        // One grouped query for every source: Qualified opportunities linked to evidence from it.
        var qualifiedBySource = await (from l in db.OpportunityEvidence
                join e in db.Evidence on l.EvidenceId equals e.Id
                join o in db.Opportunities on l.OpportunityId equals o.Id
                where e.OwnerId == owner && o.OwnerId == owner && modes.Contains(o.Mode) && o.Outcome == FilterOutcome.Qualified
                select new { e.SourceId, l.OpportunityId })
            .Distinct()
            .GroupBy(x => x.SourceId)
            .Select(g => new { SourceId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.SourceId, x => x.Count, ct);

        var top = sources
            .OrderByDescending(s => s.ItemCount).ThenBy(s => s.Label, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Id)
            .Take(MaxSources)
            .Select(s =>
            {
                var q = qualifiedBySource.GetValueOrDefault(s.Id);
                return new SourceYieldDto(s.Id, s.CampaignId, s.Label, s.Kind, s.Platform, s.ItemCount, q, Rate(q, s.ItemCount),
                    s.LastFetchedAt, s.Status == SourceStatus.Failed);
            })
            .ToList();
        return (top, sources.Count(s => s.Status == SourceStatus.Failed));
    }

    // ---- Pure rules (unit-tested) ----

    /// <summary>Both values are validated together; workspace is matched by name, case-insensitively (numbers are rejected).</summary>
    public static AnalyticsWorkspace Validate(string? workspace, int days)
    {
        var errors = new Dictionary<string, string[]>();
        var names = Enum.GetNames<AnalyticsWorkspace>();
        var name = names.FirstOrDefault(n => string.Equals(n, workspace?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (name is null) errors["workspace"] = ["Workspace must be Candidate or Sales."];
        if (days is < MinDays or > MaxDays) errors["days"] = [$"Days must be between {MinDays} and {MaxDays}."];
        if (errors.Count > 0) throw new RequestValidationException(errors);
        return Enum.Parse<AnalyticsWorkspace>(name!);
    }

    /// <summary>
    /// Position on the pipeline New &lt; Suggested &lt; Shortlisted &lt; Applied &lt; Contacted &lt; Responded &lt; Interested.
    /// Dismissed and Closed are off the pipeline (null): they say nothing about how far an opportunity got.
    /// </summary>
    public static int? Rank(OpportunityStatus status) => status switch
    {
        OpportunityStatus.New => 0,
        OpportunityStatus.Suggested => 1,
        OpportunityStatus.Shortlisted => 2,
        OpportunityStatus.Applied => 3,
        OpportunityStatus.Contacted => 4,
        OpportunityStatus.Responded => 5,
        OpportunityStatus.Interested => 6,
        _ => null
    };

    private static int RankOr(OpportunityStatus status, int fallback = -1) => Rank(status) ?? fallback;

    /// <summary>
    /// The statuses an activity proves: StatusChanged "From → To"; Suggested (New → Suggested); Approved (Suggested → Shortlisted);
    /// Rejected (Suggested → Dismissed); Applied (→ Applied, the agent report). Anything else proves nothing.
    /// </summary>
    public static (OpportunityStatus? From, OpportunityStatus? To) StepOf(string kind, string? detail)
    {
        switch (kind)
        {
            case ActivityKinds.StatusChanged:
            {
                var parts = (detail ?? string.Empty).Split(Arrow);
                return parts.Length == 2 ? (Parse(parts[0]), Parse(parts[1])) : (null, null);
            }
            case ActivityKinds.Suggested: return (OpportunityStatus.New, OpportunityStatus.Suggested);
            case ActivityKinds.Approved: return (OpportunityStatus.Suggested, OpportunityStatus.Shortlisted);
            case ActivityKinds.Rejected: return (OpportunityStatus.Suggested, OpportunityStatus.Dismissed);
            case ActivityKinds.Applied: return (null, OpportunityStatus.Applied);
            default: return (null, null);
        }

        static OpportunityStatus? Parse(string value) =>
            Enum.GetNames<OpportunityStatus>().Contains(value.Trim()) ? Enum.Parse<OpportunityStatus>(value.Trim()) : null;
    }

    /// <summary>
    /// How far along the pipeline the opportunity has provably been: its current status, or any status an activity shows it
    /// left or entered (so a Dismissed or Closed opportunity keeps the stage it reached first). -1 when nothing is known.
    /// </summary>
    public static int HighestRank(OpportunityStatus? current, IEnumerable<AnalyticsActivity> activities)
    {
        var best = current is { } c ? RankOr(c) : -1;
        foreach (var a in activities)
        {
            var (from, to) = StepOf(a.Kind, a.Detail);
            if (from is { } f) best = Math.Max(best, RankOr(f));
            if (to is { } t) best = Math.Max(best, RankOr(t));
        }
        return best;
    }

    /// <summary>"Reached X": current status at or beyond X, or an activity proving it got there before being Dismissed/Closed.</summary>
    public static bool Reached(OpportunityStatus current, IEnumerable<AnalyticsActivity> activities, OpportunityStatus stage) =>
        Rank(stage) is { } target && HighestRank(current, activities) >= target;

    /// <summary>The earliest activity that moved the opportunity to <paramref name="stage"/> or beyond; null when none did.</summary>
    public static AnalyticsActivity? FirstReach(IEnumerable<AnalyticsActivity> activities, OpportunityStatus stage)
    {
        if (Rank(stage) is not { } target) return null;
        return activities.OrderBy(a => a.OccurredAt)
            .FirstOrDefault(a => StepOf(a.Kind, a.Detail).To is { } to && RankOr(to) >= target);
    }

    private static Dictionary<Guid, AnalyticsActivity> FirstReaches(Dictionary<Guid, List<AnalyticsActivity>> byOpportunity, OpportunityStatus stage)
    {
        var result = new Dictionary<Guid, AnalyticsActivity>();
        foreach (var (id, activities) in byOpportunity)
            if (FirstReach(activities, stage) is { } first) result[id] = first;
        return result;
    }

    private static void RemoveReachedEarlier(Dictionary<Guid, AnalyticsActivity> first, Dictionary<Guid, int> earlierRank, OpportunityStatus stage)
    {
        foreach (var id in first.Keys.ToList())
            if (earlierRank.GetValueOrDefault(id, -1) >= RankOr(stage, int.MaxValue)) first.Remove(id);
    }

    /// <summary>Band index 0–9: 0–9, 10–19, … 80–89, and 90–100 (100 joins the top band). Scores outside 0–100 are clamped.</summary>
    public static int Band(int score) => Math.Min(Math.Max(score, 0) / 10, HistogramBands - 1);

    public static IReadOnlyList<HistogramBandDto> Histogram(IEnumerable<int> scores)
    {
        var counts = new int[HistogramBands];
        foreach (var s in scores) counts[Band(s)]++;
        return Enumerable.Range(0, HistogramBands)
            .Select(i => new HistogramBandDto(i * 10, i == HistogramBands - 1 ? 100 : i * 10 + 9, counts[i]))
            .ToList();
    }

    /// <summary>numerator / denominator rounded to 4 decimals; null when either is null or the denominator is 0.</summary>
    public static double? Rate(int? numerator, int? denominator) =>
        numerator is { } n && denominator is > 0 and { } d ? Math.Round(n / (double)d, 4) : null;

    /// <summary>The most common non-null value; a tie goes to the higher threshold. Null when no campaign sets one.</summary>
    public static int? CommonThreshold(IEnumerable<int?> thresholds) =>
        thresholds.Where(t => t is not null).GroupBy(t => t!.Value)
            .OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key)
            .Select(g => (int?)g.Key).FirstOrDefault();

    /// <summary>
    /// Criteria the sources most often left unknown (breakdown value null), counted once per opportunity; most first, then by
    /// criterion key. The label is the one first seen for that key.
    /// </summary>
    public static IReadOnlyList<UnknownCriterionDto> TopUnknown(IEnumerable<IReadOnlyList<BreakdownRow>> breakdowns, int take = TopUnknownCriteria)
    {
        var counts = new Dictionary<string, (string Label, int Count)>(StringComparer.Ordinal);
        foreach (var rows in breakdowns)
        {
            foreach (var row in rows.Where(r => r.Value is null).DistinctBy(r => r.Criterion))
                counts[row.Criterion] = counts.TryGetValue(row.Criterion, out var c) ? (c.Label, c.Count + 1) : (row.Label, 1);
        }
        return counts.OrderByDescending(kv => kv.Value.Count).ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Take(take)
            .Select(kv => new UnknownCriterionDto(kv.Key, kv.Value.Label, kv.Value.Count))
            .ToList();
    }

    /// <summary>
    /// Each opportunity counts once, under the first industry term it matched (fact "industriesMatched", criteria order).
    /// Opportunities that matched none are not counted. Top 6 by count, the rest summed as "Other".
    /// </summary>
    public static IReadOnlyList<IndustryCountDto> ByIndustry(IEnumerable<IReadOnlyList<FactRow>> facts)
    {
        var counts = facts
            .Select(f => Terms(f, "industriesMatched").FirstOrDefault())
            .Where(t => t is not null)
            .GroupBy(t => t!, StringComparer.OrdinalIgnoreCase)
            .Select(g => new IndustryCountDto(g.First()!, g.Count()))
            .OrderByDescending(x => x.Count).ThenBy(x => x.Industry, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (counts.Count <= TopIndustries) return counts;
        return [.. counts.Take(TopIndustries), new IndustryCountDto(OtherIndustry, counts.Skip(TopIndustries).Sum(x => x.Count))];
    }

    /// <summary>Opportunities per signal term they mentioned (fact "signals"); most first, then by name.</summary>
    public static IReadOnlyList<SignalCountDto> Signals(IEnumerable<IReadOnlyList<FactRow>> facts) =>
        facts.SelectMany(f => Terms(f, "signals").Distinct(StringComparer.OrdinalIgnoreCase))
            .GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
            .Select(g => new SignalCountDto(g.First(), g.Count()))
            .OrderByDescending(x => x.Count).ThenBy(x => x.Signal, StringComparer.OrdinalIgnoreCase)
            .ToList();

    // The rules write matched terms joined with ", " (see CustomerRules).
    private static IEnumerable<string> Terms(IReadOnlyList<FactRow> facts, string key) =>
        facts.Where(f => f.Key == key)
            .SelectMany(f => f.Value.Split(", ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static T? Deserialize<T>(string? json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<T>(json, JsonSerializerOptions.Web); }
        catch (JsonException) { return null; }
    }

    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;

    private static string Plural(int n) => n == 1 ? "" : "s";
}
