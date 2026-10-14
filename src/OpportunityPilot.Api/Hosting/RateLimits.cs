using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace OpportunityPilot.Api.Hosting;

/// <summary>
/// Per-endpoint quotas on top of the global 120 requests/minute: actions that create identities, import data, start
/// work or spend a paid provider quota get their own, smaller budgets. Partitioned by signed-in owner, else by the peer
/// address (forwarded headers are not trusted for this, see Program.cs).
/// </summary>
public static class RateLimits
{
    public const string GuestSignIn = "guest-sign-in";
    public const string Imports = "imports";
    public const string ResearchQueue = "research-queue";
    public const string JobBoardSearch = "job-board-search";
    public const string PublicProgress = "public-progress";
    public const string Connectors = "connectors";
    public const string ReplyCheck = "reply-check";

    /// <summary>(policy, permits, window). Kept in one place so tests and the runbook can name them.</summary>
    public static readonly (string Name, int Permits, TimeSpan Window)[] Policies =
    [
        (GuestSignIn, 10, TimeSpan.FromHours(1)),
        (Imports, 30, TimeSpan.FromHours(1)),
        (ResearchQueue, 60, TimeSpan.FromHours(1)),
        (JobBoardSearch, 60, TimeSpan.FromHours(1)),
        (PublicProgress, 120, TimeSpan.FromHours(1)),
        (Connectors, 60, TimeSpan.FromHours(1)),
        (ReplyCheck, 20, TimeSpan.FromHours(1)),
    ];

    public static void Add(RateLimiterOptions options)
    {
        foreach (var (name, permits, window) in Policies)
            options.AddPolicy(name, ctx => RateLimitPartition.GetFixedWindowLimiter(
                $"{name}:{ctx.User.FindFirst("sub")?.Value ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "anonymous"}",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = permits, Window = window }));
    }
}
