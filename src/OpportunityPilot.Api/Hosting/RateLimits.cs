using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace OpportunityPilot.Api.Hosting;

/// <summary>
/// Per-endpoint quotas on top of the global 120 requests/minute: actions that create identities, import data, start
/// work or spend a paid provider quota get their own, smaller budgets. Partitioned by signed-in owner, else by the visitor's
/// address (<see cref="ClientKey"/>: Cloudflare's CF-Connecting-IP, never X-Forwarded-For).
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
    public const string VoiceTranscribe = "voice-transcribe";
    public const string VoiceSpeak = "voice-speak";
    public const string Assistant = "assistant";
    public const string AiWrites = "ai-writes";
    public const string LeadSearch = "lead-search";

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
        // Each voice call spends Groq quota: 30 an hour per person each (S6, 2026-10-16). Over it, 429 and the app says "try later".
        (VoiceTranscribe, 30, TimeSpan.FromHours(1)),
        (VoiceSpeak, 30, TimeSpan.FromHours(1)),
        (Assistant, 30, TimeSpan.FromHours(1)),
        // Writing, rewriting and improving with AI spend the shared daily Groq budget: 60 an hour per person (2026-10-19).
        (AiWrites, 60, TimeSpan.FromHours(1)),
        // Apollo lead search is paid per result; the daily budget caps the total, this caps one person (2026-10-19).
        (LeadSearch, 10, TimeSpan.FromHours(1)),
    ];

    public static void Add(RateLimiterOptions options)
    {
        foreach (var (name, permits, window) in Policies)
            options.AddPolicy(name, ctx => RateLimitPartition.GetFixedWindowLimiter(
                $"{name}:{ClientKey.For(ctx)}",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = permits, Window = window }));
    }
}
