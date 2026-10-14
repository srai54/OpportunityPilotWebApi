using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpportunityPilot.Application.Abstractions;
using OpportunityPilot.Application.Common;
using OpportunityPilot.Domain.Drafts;
using OpportunityPilot.Domain.Mail;
using OpportunityPilot.Domain.Opportunities;
using OpportunityPilot.Domain.Outreach;

namespace OpportunityPilot.Application.Mail;

public sealed record ReplyItemDto(Guid OpportunityId, string Organization, string From, string? Subject, DateTime ReceivedAt);

/// <param name="Checked">Inbox messages whose headers were read.</param>
/// <param name="Replies">New replies recorded by this check (one already recorded is not counted again).</param>
public sealed record ReplyCheckResult(int Checked, int Replies, IReadOnlyList<ReplyItemDto> Items);

/// <summary>
/// Reply detection: reads the headers of the person's own inbox (the email connected with an app password) and treats a message as a
/// reply when it comes from the address an email was sent to, after that send. A reply is recorded on the lead, moves it to Responded
/// and cancels its open follow-ups, so no follow-up goes out after someone answered. Reading never changes the mailbox. Running it
/// again records nothing twice.
/// </summary>
public sealed class ReplyCheckService(
    IAppDbContext db, ICurrentUser user, TimeProvider clock, IMailboxReader reader, TokenProtector protector,
    ILogger<ReplyCheckService> logger, Integrations.IntegrationNotifier? notifier = null)
{
    /// <summary>Only emails sent in the last 30 days are watched for replies.</summary>
    public const int WindowDays = 30;
    /// <summary>The background check visits at most this many people per pass.</summary>
    public const int OwnersPerPass = 50;

    private static readonly OpportunityStatus[] BeforeResponded =
        [OpportunityStatus.New, OpportunityStatus.Suggested, OpportunityStatus.Shortlisted, OpportunityStatus.Applied, OpportunityStatus.Contacted];

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    /// <summary>The signed-in person's check (the endpoint).</summary>
    public Task<ReplyCheckResult> CheckAsync(CancellationToken ct) => CheckOwnerAsync(user.OwnerId, ct);

    /// <summary>One person's check. Takes the owner explicitly so the background job needs no signed-in user.</summary>
    public async Task<ReplyCheckResult> CheckOwnerAsync(Guid ownerId, CancellationToken ct)
    {
        var connection = await db.MailboxConnections.FirstOrDefaultAsync(m => m.OwnerId == ownerId && m.Provider == MailProvider.Smtp, ct)
            ?? throw new ConflictException("Connect your email with an app password in Settings first; replies are read from that mailbox.");
        if (connection.Status != MailboxStatus.Connected) throw new ConflictException("Connect your email again in Settings; the saved login stopped working.");

        var windowStart = Now.AddDays(-WindowDays);
        var sends = await db.OutreachDrafts.AsNoTracking()
            .Where(d => d.OwnerId == ownerId && d.Channel == DraftChannel.Email && d.SentAt != null && d.SentAt >= windowStart && d.Recipient != null)
            .Select(d => new { d.OpportunityId, d.Recipient, SentAt = d.SentAt!.Value }).ToListAsync(ct);
        if (sends.Count == 0) return new(0, 0, []);

        var credentials = protector.Unprotect(connection.EncryptedRefreshToken);
        if (credentials is null)
        {
            connection.MarkNeedsReconnect();
            await db.SaveChangesAsync(ct);
            throw new ConflictException("The saved email login can no longer be read on this server. Connect your email again in Settings.");
        }

        var read = await reader.ReadHeadersSinceAsync(credentials, sends.Min(s => s.SentAt), ct);
        if (read.Failure is not null)
        {
            // A refusal from Microsoft usually means IMAP app passwords are switched off there, not a wrong password: sending still works, so keep it connected.
            if (read.LoginRefused && !IsMicrosoft(credentials))
            {
                connection.MarkNeedsReconnect();
                await db.SaveChangesAsync(ct);
            }
            throw new ConflictException(read.Failure);
        }

        var own = connection.EmailAddress.Trim();
        var byRecipient = sends.GroupBy(s => s.Recipient!.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(s => s.SentAt).ToList(), StringComparer.OrdinalIgnoreCase);

        // A reply belongs to the latest email sent to that address before it arrived.
        var matches = new List<(Guid OpportunityId, MailHeader Header)>();
        foreach (var header in read.Messages)
        {
            if (header.AutoReply || string.Equals(header.FromAddress, own, StringComparison.OrdinalIgnoreCase)) continue;
            if (!byRecipient.TryGetValue(header.FromAddress.Trim(), out var sent)) continue;
            var send = sent.FirstOrDefault(s => s.SentAt < header.ReceivedAt);
            if (send is not null) matches.Add((send.OpportunityId, header));
        }
        if (matches.Count == 0) return new(read.Messages.Count, 0, []);

        var ids = matches.Select(m => m.OpportunityId).Distinct().ToList();
        var opportunities = await db.Opportunities.Where(o => o.OwnerId == ownerId && ids.Contains(o.Id)).ToDictionaryAsync(o => o.Id, ct);
        var recorded = (await db.Activities.AsNoTracking()
                .Where(a => a.OwnerId == ownerId && ids.Contains(a.OpportunityId) && a.Kind == ActivityKinds.Replied)
                .Select(a => new { a.OpportunityId, a.Detail }).ToListAsync(ct))
            .Select(a => (a.OpportunityId, a.Detail)).ToList();
        var followUps = await db.NextActions
            .Where(a => a.OwnerId == ownerId && ids.Contains(a.OpportunityId) && a.State == NextActionState.Open && a.Kind == NextActionKind.FollowUp)
            .ToListAsync(ct);

        var items = new List<ReplyItemDto>();
        foreach (var (opportunityId, header) in matches.OrderBy(m => m.Header.ReceivedAt))
        {
            if (!opportunities.TryGetValue(opportunityId, out var opportunity)) continue;
            var key = DedupeKey(header);
            // Dedupe by sender and arrival time, kept at the start of the activity line.
            if (recorded.Any(r => r.OpportunityId == opportunityId && (r.Detail == key || r.Detail.StartsWith(key + ":", StringComparison.Ordinal)))) continue;
            var subject = string.IsNullOrWhiteSpace(header.Subject) ? null : header.Subject.Trim();
            var detail = subject is null ? key : $"{key}: {subject}";
            db.Activities.Add(new Activity(ownerId, opportunityId, ActivityKinds.Replied, header.ReceivedAt, detail));
            recorded.Add((opportunityId, detail));

            if (BeforeResponded.Contains(opportunity.Status) && opportunity.ChangeStatus(OpportunityStatus.Responded, header.ReceivedAt) is { } moved)
                db.Activities.Add(moved);
            // "Follow up if there is no reply": there is one now, so the reminder is no longer needed.
            foreach (var followUp in followUps.Where(f => f.OpportunityId == opportunityId && f.State == NextActionState.Open))
                followUp.Update(NextActionState.Cancelled, null, Now);

            items.Add(new(opportunityId, opportunity.Organization, header.FromAddress, subject, header.ReceivedAt));
        }
        if (items.Count == 0) return new(read.Messages.Count, 0, []);

        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw new ConflictException("A lead changed while replies were being recorded. Check for replies again."); }

        if (notifier is not null)
        {
            var lines = items.Take(10).Select(i => $"Reply from {i.From}" + (string.IsNullOrWhiteSpace(i.Organization) ? "" : $" at {i.Organization}")).ToList();
            if (items.Count > 10) lines.Add($"and {items.Count - 10} more");
            await notifier.NotifyAsync(ownerId, Integrations.IntegrationEvents.Replies, items.Count == 1 ? "Reply received" : $"{items.Count} replies received", lines, "/outreach", ct);
        }
        return new(read.Messages.Count, items.Count, items);
    }

    /// <summary>
    /// The background pass: people with a connected app-password mailbox and an email sent in the last 30 days, at most
    /// <see cref="OwnersPerPass"/>, continuing after <paramref name="afterOwner"/> so everyone gets a turn. One person's failure
    /// (a refused login, an unreachable server) is logged and the pass moves on.
    /// </summary>
    /// <returns>The last owner visited (null when the list wrapped to the start) and the replies recorded.</returns>
    public async Task<(Guid? LastOwner, int Replies)> CheckDueOwnersAsync(Guid? afterOwner, CancellationToken ct)
    {
        var windowStart = Now.AddDays(-WindowDays);
        var eligible = await db.MailboxConnections.AsNoTracking()
            .Where(m => m.Provider == MailProvider.Smtp && m.Status == MailboxStatus.Connected)
            .Where(m => db.OutreachDrafts.Any(d => d.OwnerId == m.OwnerId && d.Channel == DraftChannel.Email && d.SentAt != null && d.SentAt >= windowStart))
            .Select(m => m.OwnerId).ToListAsync(ct);
        // Ordered here, not in SQL: databases order uuids differently, and the cursor must mean the same thing on every pass.
        var owners = eligible.Distinct().Order().Where(o => afterOwner is not { } after || o.CompareTo(after) > 0).Take(OwnersPerPass).ToList();

        var replies = 0;
        foreach (var owner in owners)
        {
            try { replies += (await CheckOwnerAsync(owner, ct)).Replies; }
            catch (ConflictException ex) { logger.LogInformation("Reply check skipped for an owner: {Reason}", ex.Message); }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Reply check failed for an owner; trying the next one");
            }
            db.ChangeTracker.Clear();
        }
        return (owners.Count < OwnersPerPass ? null : owners[^1], replies);
    }

    private static string DedupeKey(MailHeader header) =>
        $"Reply received from {header.FromAddress} on {header.ReceivedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} UTC";

    private static bool IsMicrosoft(string credentialsJson)
    {
        try { return System.Text.Json.JsonSerializer.Deserialize<SmtpCredentials>(credentialsJson) is { } c && MailHosts.IsMicrosoft(c.Host); }
        catch (System.Text.Json.JsonException) { return false; }
    }
}
