using Microsoft.EntityFrameworkCore;
using OpportunityPilot.Application.Common;
using OpportunityPilot.Domain.Opportunities;

namespace OpportunityPilot.Application.Analytics;

/// <summary>One UTC day. Reached = Applied (Candidate) or Contacted (Sales); Sent = drafts recorded as sent that day.</summary>
public sealed record TrendPointDto(DateOnly Date, int Found, int Qualified, int Approved, int Reached, int Replied, int Sent);

public sealed record TrendTotalsDto(int Found, int Qualified, int Approved, int Reached, int Replied, int Sent);

/// <summary>
/// Activity per day for the last <c>Days</c> days, with the totals of that window and of the window just before it, so the screen can say
/// "up 3 on the week before". Everything is counted from the caller's stored records; "Approved" is the first time an opportunity
/// reached Shortlisted, and "Replied" the first time it reached Responded.
/// </summary>
public sealed record AnalyticsTrendDto(AnalyticsWorkspace Workspace, int Days, DateTime GeneratedAt,
    IReadOnlyList<TrendPointDto> Points, TrendTotalsDto Current, TrendTotalsDto Previous);

/// <summary>The dates of each kind of event for the two windows; <see cref="TrendBuilder"/> turns them into points and totals.</summary>
public sealed record TrendEvents(
    IReadOnlyList<(DateTime At, bool Qualified)> Found,
    IReadOnlyList<DateTime> Approved,
    IReadOnlyList<DateTime> Reached,
    IReadOnlyList<DateTime> Replied,
    IReadOnlyList<DateTime> Sent);

public static class TrendBuilder
{
    /// <summary>Pure: buckets events by UTC day for [today-days+1, today], and totals the same-length window before it.</summary>
    public static (IReadOnlyList<TrendPointDto> Points, TrendTotalsDto Current, TrendTotalsDto Previous) Build(TrendEvents e, DateOnly today, int days)
    {
        var first = today.AddDays(-(days - 1));
        var prevFirst = first.AddDays(-days);
        static DateOnly D(DateTime t) => DateOnly.FromDateTime(t);
        int Count(IEnumerable<DateTime> xs, DateOnly from, DateOnly to) => xs.Count(t => D(t) >= from && D(t) <= to);

        var points = Enumerable.Range(0, days).Select(i =>
        {
            var d = first.AddDays(i);
            return new TrendPointDto(d,
                e.Found.Count(x => D(x.At) == d), e.Found.Count(x => x.Qualified && D(x.At) == d),
                Count(e.Approved, d, d), Count(e.Reached, d, d), Count(e.Replied, d, d), Count(e.Sent, d, d));
        }).ToList();

        TrendTotalsDto Totals(DateOnly from, DateOnly to) => new(
            e.Found.Count(x => D(x.At) >= from && D(x.At) <= to), e.Found.Count(x => x.Qualified && D(x.At) >= from && D(x.At) <= to),
            Count(e.Approved, from, to), Count(e.Reached, from, to), Count(e.Replied, from, to), Count(e.Sent, from, to));

        return (points, Totals(first, today), Totals(prevFirst, first.AddDays(-1)));
    }
}

public sealed partial class AnalyticsService
{
    public const int MinTrendDays = 7;
    public const int MaxTrendDays = 90;

    public async Task<AnalyticsTrendDto> TrendAsync(string? workspace, int days, CancellationToken ct)
    {
        var ws = Validate(workspace, Math.Clamp(days, MinDays, MaxDays));
        if (days is < MinTrendDays or > MaxTrendDays)
            throw new RequestValidationException(new Dictionary<string, string[]> { ["days"] = [$"Days must be between {MinTrendDays} and {MaxTrendDays} for the trend."] });
        var candidate = ws == AnalyticsWorkspace.Candidate;
        var modes = candidate ? new[] { Domain.Common.OpportunityMode.Job } : SalesModes;
        var owner = user.OwnerId;
        var now = clock.GetUtcNow().UtcDateTime;
        var today = DateOnly.FromDateTime(now);
        var rangeStart = today.AddDays(-(2 * days - 1)).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

        var found = await db.Opportunities
            .Where(o => o.OwnerId == owner && modes.Contains(o.Mode) && o.CreatedAt >= rangeStart)
            .Select(o => new { o.CreatedAt, o.Outcome })
            .ToListAsync(ct);

        var byOpp = (await (from a in db.Activities
                    join o in db.Opportunities on a.OpportunityId equals o.Id
                    where a.OwnerId == owner && o.OwnerId == owner && modes.Contains(o.Mode) && a.OccurredAt >= rangeStart && StatusKinds.Contains(a.Kind)
                    select new { a.OpportunityId, a.Kind, a.Detail, a.OccurredAt }).ToListAsync(ct))
            .GroupBy(a => a.OpportunityId)
            .ToDictionary(g => g.Key, g => g.Select(a => new AnalyticsActivity(a.Kind, a.Detail, a.OccurredAt)).OrderBy(a => a.OccurredAt).ToList());

        var reachedStage = candidate ? OpportunityStatus.Applied : OpportunityStatus.Contacted;
        var approved = FirstReaches(byOpp, OpportunityStatus.Shortlisted);
        var reached = FirstReaches(byOpp, reachedStage);
        var replied = FirstReaches(byOpp, OpportunityStatus.Responded);

        // An opportunity that got there before the range did not get there in it.
        var ids = approved.Keys.Concat(reached.Keys).Concat(replied.Keys).Distinct().ToList();
        if (ids.Count > 0)
        {
            var earlier = (await db.Activities
                    .Where(a => a.OwnerId == owner && ids.Contains(a.OpportunityId) && a.OccurredAt < rangeStart && StatusKinds.Contains(a.Kind))
                    .Select(a => new { a.OpportunityId, a.Kind, a.Detail, a.OccurredAt }).ToListAsync(ct))
                .GroupBy(a => a.OpportunityId)
                .ToDictionary(g => g.Key, g => HighestRank(null, g.Select(a => new AnalyticsActivity(a.Kind, a.Detail, a.OccurredAt))));
            RemoveReachedEarlier(approved, earlier, OpportunityStatus.Shortlisted);
            RemoveReachedEarlier(reached, earlier, reachedStage);
            RemoveReachedEarlier(replied, earlier, OpportunityStatus.Responded);
        }

        var sent = await (from d in db.OutreachDrafts
                          join o in db.Opportunities on d.OpportunityId equals o.Id
                          where d.OwnerId == owner && o.OwnerId == owner && modes.Contains(o.Mode) && d.SentAt != null && d.SentAt >= rangeStart
                          select d.SentAt!.Value).ToListAsync(ct);

        var events = new TrendEvents(
            found.Select(f => (f.CreatedAt, f.Outcome == FilterOutcome.Qualified)).ToList(),
            approved.Values.Select(a => a.OccurredAt).ToList(), reached.Values.Select(a => a.OccurredAt).ToList(),
            replied.Values.Select(a => a.OccurredAt).ToList(), sent);
        var (points, current, previous) = TrendBuilder.Build(events, today, days);
        return new AnalyticsTrendDto(ws, days, now, points, current, previous);
    }
}
