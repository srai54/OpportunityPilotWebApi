using Microsoft.EntityFrameworkCore;
using OpportunityPilot.Application.Common;
using OpportunityPilot.Domain.Opportunities;

namespace OpportunityPilot.Application.Analytics;

/// <summary>One thing the signed-in person did (or that was recorded for them), newest first. It may belong to a job or lead, or to nothing in particular (a search).</summary>
public sealed record ActivityFeedItemDto(DateTime At, string Kind, string Detail, Guid? OpportunityId, string Title, string Organization);

public sealed partial class AnalyticsService
{
    public const int MaxFeed = 100;

    /// <summary>Every kind that can appear in the feed: the per-opportunity activities and the person's own events.</summary>
    public static readonly IReadOnlyList<string> FeedKinds =
    [
        ActivityKinds.Researched, ActivityKinds.Suggested, ActivityKinds.Approved, ActivityKinds.Rejected, ActivityKinds.Applied, ActivityKinds.StatusChanged, ActivityKinds.Replied,
        .. UserEventKinds.All,
    ];

    /// <summary>
    /// "My activity": the caller's own recorded actions. Per-opportunity lines are limited to the chosen workspace; lines that belong to no
    /// opportunity (searches, runs, profiles) show in both. <paramref name="before"/> is the paging cursor: pass the oldest <c>at</c> already shown.
    /// Read only; every row comes from a stored record.
    /// </summary>
    public async Task<IReadOnlyList<ActivityFeedItemDto>> ActivityFeedAsync(string? workspace, string? kinds, DateTime? from, DateTime? to, DateTime? before, int take, CancellationToken ct)
    {
        var ws = Validate(workspace, DefaultDays);
        var wanted = ParseKinds(kinds);
        take = Math.Clamp(take, 1, MaxFeed);
        var modes = ws == AnalyticsWorkspace.Candidate ? new[] { Domain.Common.OpportunityMode.Job } : SalesModes;
        var owner = user.OwnerId;
        var start = from?.ToUniversalTime();
        var end = to?.ToUniversalTime();
        var cursor = before?.ToUniversalTime();

        var activities = db.Activities.AsNoTracking().Where(a => a.OwnerId == owner);
        if (wanted is not null) activities = activities.Where(a => wanted.Contains(a.Kind));
        if (start is { } s1) activities = activities.Where(a => a.OccurredAt >= s1);
        if (end is { } e1) activities = activities.Where(a => a.OccurredAt <= e1);
        if (cursor is { } c1) activities = activities.Where(a => a.OccurredAt < c1);
        var fromActivities = await (from a in activities
                                    join o in db.Opportunities.AsNoTracking() on a.OpportunityId equals o.Id
                                    where o.OwnerId == owner && modes.Contains(o.Mode)
                                    orderby a.OccurredAt descending
                                    select new ActivityFeedItemDto(a.OccurredAt, a.Kind, a.Detail ?? string.Empty, a.OpportunityId, o.Title, o.Organization))
            .Take(take).ToListAsync(ct);

        var events = db.UserEvents.AsNoTracking().Where(x => x.OwnerId == owner);
        if (wanted is not null) events = events.Where(x => wanted.Contains(x.Kind));
        if (start is { } s2) events = events.Where(x => x.OccurredAt >= s2);
        if (end is { } e2) events = events.Where(x => x.OccurredAt <= e2);
        if (cursor is { } c2) events = events.Where(x => x.OccurredAt < c2);
        var fromEvents = await (from x in events
                                join o in db.Opportunities.AsNoTracking() on x.OpportunityId equals (Guid?)o.Id into joined
                                from o in joined.DefaultIfEmpty()
                                where x.OpportunityId == null || (o != null && o.OwnerId == owner && modes.Contains(o.Mode))
                                orderby x.OccurredAt descending
                                select new ActivityFeedItemDto(x.OccurredAt, x.Kind, x.Detail, x.OpportunityId, o != null ? o.Title : (x.Title ?? string.Empty), o != null ? o.Organization : string.Empty))
            .Take(take).ToListAsync(ct);

        return fromActivities.Concat(fromEvents).OrderByDescending(x => x.At).Take(take).ToList();
    }

    private static HashSet<string>? ParseKinds(string? kinds)
    {
        if (string.IsNullOrWhiteSpace(kinds)) return null;
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var k in kinds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!FeedKinds.Contains(k))
                throw new RequestValidationException(new Dictionary<string, string[]> { ["kinds"] = [$"Unknown kind '{k}'. Use {string.Join(", ", FeedKinds)}."] });
            set.Add(k);
        }
        return set.Count == 0 ? null : set;
    }
}
