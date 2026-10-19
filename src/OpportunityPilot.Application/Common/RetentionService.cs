using Microsoft.EntityFrameworkCore;
using OpportunityPilot.Application.Abstractions;

namespace OpportunityPilot.Application.Common;

public sealed record RetentionResult(int ImportPreviews, int ResearchEvents, int AiUsage, int SecurityEvents, int GuestSessions)
{
    public int Total => ImportPreviews + ResearchEvents + AiUsage + SecurityEvents + GuestSessions;
}

/// <summary>
/// Deletes data whose purpose has passed, across all owners, in bounded batches (docs/operations.md). Business records —
/// profiles, campaigns, opportunities, evidence, drafts, applications, staffing records — are never removed here; only
/// the owner's own "Delete all data" removes those.
/// </summary>
public sealed class RetentionService(IAppDbContext db, TimeProvider clock)
{
    public const int BatchSize = 500;
    public static readonly TimeSpan ResearchEventAge = TimeSpan.FromDays(90);
    public static readonly TimeSpan AiUsageAge = TimeSpan.FromDays(90);
    public static readonly TimeSpan SecurityEventAge = TimeSpan.FromDays(365);

    public async Task<RetentionResult> RunOnceAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;

        var imports = await db.ImportBatches.Where(b => !b.Committed && b.ExpiresAt < now).OrderBy(b => b.ExpiresAt).Take(BatchSize).ToListAsync(ct);
        db.ImportBatches.RemoveRange(imports);

        var eventCutoff = now - ResearchEventAge;
        var events = await db.ResearchEvents.Where(e => e.At < eventCutoff).OrderBy(e => e.At).Take(BatchSize).ToListAsync(ct);
        db.ResearchEvents.RemoveRange(events);

        var aiCutoff = now - AiUsageAge;
        var usage = await db.AiUsages.Where(u => u.OccurredAt < aiCutoff).OrderBy(u => u.OccurredAt).Take(BatchSize).ToListAsync(ct);
        db.AiUsages.RemoveRange(usage);

        var securityCutoff = now - SecurityEventAge;
        var security = await db.SecurityEvents.Where(e => e.OccurredAt < securityCutoff).OrderBy(e => e.OccurredAt).Take(BatchSize).ToListAsync(ct);
        db.SecurityEvents.RemoveRange(security);

        var sessions = await db.GuestSessions.Where(s => s.ExpiresAt < now).OrderBy(s => s.ExpiresAt).Take(BatchSize).ToListAsync(ct);
        db.GuestSessions.RemoveRange(sessions);

        await db.SaveChangesAsync(ct);
        return new(imports.Count, events.Count, usage.Count, security.Count, sessions.Count);
    }
}
