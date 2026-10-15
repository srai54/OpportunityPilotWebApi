using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpportunityPilot.Application.Abstractions;
using OpportunityPilot.Domain.Drafts;
using OpportunityPilot.Domain.Outreach;

namespace OpportunityPilot.Application.Outreach;

/// <summary>
/// Automates the manual "write the follow-up" step: when a follow-up is due and nobody has replied (a reply cancels open
/// follow-ups), a follow-up draft is written for that lead from a template, addressed like the last message sent. It is only a
/// draft: it waits in Outreach for the person to read, approve and send, like every other message. Nothing is sent here.
/// One draft per sent message: a newer unsent draft for the lead means it is skipped. Suppressed recipients are skipped.
/// </summary>
public sealed class FollowUpDraftService(IAppDbContext db, TimeProvider clock, ILogger<FollowUpDraftService> logger)
{
    /// <summary>At most this many follow-up drafts are written per pass.</summary>
    public const int PerPass = 50;

    private static readonly DraftChannel[] Channels = [DraftChannel.Email, DraftChannel.LinkedInMessage];

    public async Task<int> DraftDueFollowUpsAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var due = await db.NextActions.AsNoTracking()
            .Where(a => a.Kind == NextActionKind.FollowUp && a.State == NextActionState.Open && a.DueAt <= now)
            .OrderBy(a => a.DueAt)
            .Select(a => new { a.OwnerId, a.OpportunityId })
            .Take(PerPass * 4)
            .ToListAsync(ct);

        var created = 0;
        foreach (var lead in due.DistinctBy(d => (d.OwnerId, d.OpportunityId)))
        {
            if (created >= PerPass) break;
            var drafts = await db.OutreachDrafts.AsNoTracking()
                .Where(d => d.OwnerId == lead.OwnerId && d.OpportunityId == lead.OpportunityId)
                .OrderByDescending(d => d.CreatedAt)
                .Take(20)
                .ToListAsync(ct);
            var lastSent = drafts.Where(d => d.State == DraftState.Sent && d.SentAt != null && Channels.Contains(d.Channel))
                .OrderByDescending(d => d.SentAt).FirstOrDefault();
            if (lastSent?.SentAt is not { } sentAt) continue;
            if (drafts.Any(d => d.State != DraftState.Sent && d.CreatedAt > sentAt)) continue;
            if (lastSent.Recipient is { } recipient)
            {
                var normalized = Suppression.Normalize(recipient);
                if (await db.Suppressions.AnyAsync(s => s.OwnerId == lead.OwnerId && s.NormalizedRecipient == normalized, ct)) continue;
            }

            db.OutreachDrafts.Add(new OutreachDraft(lead.OwnerId, lead.OpportunityId, lastSent.Channel, lastSent.Recipient,
                lastSent.RecipientVerified, FollowUpSubject(lastSent.Subject), FollowUpBody(sentAt), DraftSource.Template, "[]", now));
            created++;
        }

        if (created > 0)
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Wrote {Count} follow-up drafts for due follow-ups", created);
        }
        return created;
    }

    public static string? FollowUpSubject(string? subject)
    {
        if (string.IsNullOrWhiteSpace(subject)) return null;
        var s = subject.Trim();
        if (!s.StartsWith("Re:", StringComparison.OrdinalIgnoreCase)) s = $"Re: {s}";
        return s.Length > OutreachDraft.MaxSubjectLength ? s[..OutreachDraft.MaxSubjectLength] : s;
    }

    public static string FollowUpBody(DateTime sentAt) =>
        $"Hi,\n\nI wanted to follow up on my message from {sentAt.ToString("d MMMM", CultureInfo.InvariantCulture)}. " +
        "Is this still a priority for you? I am happy to share more detail or find a time to talk.\n\nBest regards";
}
