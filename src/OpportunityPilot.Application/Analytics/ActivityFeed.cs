using Microsoft.EntityFrameworkCore;

namespace OpportunityPilot.Application.Analytics;

/// <summary>One thing the signed-in person did (or that was recorded for them), newest first, with the job or lead it belongs to.</summary>
public sealed record ActivityFeedItemDto(DateTime At, string Kind, string Detail, Guid OpportunityId, string Title, string Organization);

public sealed partial class AnalyticsService
{
    public const int MaxFeed = 100;

    /// <summary>"My activity": the caller's own recorded actions for one workspace. Read only; every row comes from a stored activity.</summary>
    public async Task<IReadOnlyList<ActivityFeedItemDto>> ActivityFeedAsync(string? workspace, int take, CancellationToken ct)
    {
        var ws = Validate(workspace, DefaultDays);
        var modes = ws == AnalyticsWorkspace.Candidate ? new[] { Domain.Common.OpportunityMode.Job } : SalesModes;
        var owner = user.OwnerId;
        return await (from a in db.Activities.AsNoTracking()
                      join o in db.Opportunities.AsNoTracking() on a.OpportunityId equals o.Id
                      where a.OwnerId == owner && o.OwnerId == owner && modes.Contains(o.Mode)
                      orderby a.OccurredAt descending
                      select new ActivityFeedItemDto(a.OccurredAt, a.Kind, a.Detail ?? string.Empty, o.Id, o.Title, o.Organization))
            .Take(Math.Clamp(take, 1, MaxFeed)).ToListAsync(ct);
    }
}
