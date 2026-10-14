using Microsoft.EntityFrameworkCore;
using OpportunityPilot.Application.Abstractions;
using OpportunityPilot.Domain.Common;
using OpportunityPilot.Domain.Opportunities;

namespace OpportunityPilot.Application.Agents;

/// <param name="Connected">At least one active agent key exists.</param>
/// <param name="Online">The agent called the API within <see cref="AgentStatusService.OnlineWindow"/>.</param>
/// <param name="Waiting">Approved jobs on LinkedIn, Naukri or Instahyre that the agent applies to and has not yet.</param>
public sealed record AgentStatusDto(bool Connected, bool Online, DateTime? LastSeenAt, int AppliedLast7Days, int Waiting, IReadOnlyList<AgentActivityDto> Recent);

public sealed record AgentActivityDto(string Title, string Company, string Platform, string Status, DateTime At, string? Detail);

/// <summary>What the desktop agent is doing, from what it reported: when it last called in, what it applied to, and what is waiting for it.</summary>
public sealed class AgentStatusService(IAppDbContext db, ICurrentUser user, TimeProvider clock)
{
    /// <summary>The agent polls every few minutes while it runs; quiet for longer than this means it is not running.</summary>
    public static readonly TimeSpan OnlineWindow = TimeSpan.FromMinutes(10);
    private static readonly JobPlatform[] AgentPlatforms = [JobPlatform.LinkedIn, JobPlatform.Naukri, JobPlatform.Instahyre];

    public async Task<AgentStatusDto> GetAsync(CancellationToken ct)
    {
        var owner = user.OwnerId;
        var now = clock.GetUtcNow().UtcDateTime;
        var keys = await db.AgentKeys.AsNoTracking().Where(k => k.OwnerId == owner && k.RevokedAt == null).Select(k => k.LastUsedAt).ToListAsync(ct);
        var lastSeen = keys.Max();
        var since = now.AddDays(-7);
        var applied = await db.JobApplications.CountAsync(a => a.OwnerId == owner && a.OccurredAt >= since, ct);
        var waiting = await db.Opportunities.CountAsync(o => o.OwnerId == owner && o.Mode == OpportunityMode.Job && o.Status == OpportunityStatus.Shortlisted
            && o.Platform != null && AgentPlatforms.Contains(o.Platform.Value), ct);
        var recent = await db.JobApplications.AsNoTracking().Where(a => a.OwnerId == owner).OrderByDescending(a => a.OccurredAt).Take(5)
            .Select(a => new { a.Title, a.Company, a.Platform, a.Status, a.OccurredAt, a.Detail }).ToListAsync(ct);
        return new AgentStatusDto(keys.Count > 0, lastSeen is { } seen && now - seen <= OnlineWindow, lastSeen, applied, waiting,
            recent.Select(a => new AgentActivityDto(a.Title, a.Company, a.Platform.ToString(), a.Status.ToString(), a.OccurredAt, a.Detail)).ToList());
    }
}
