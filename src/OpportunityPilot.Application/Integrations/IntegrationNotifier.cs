using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpportunityPilot.Application.Abstractions;
using OpportunityPilot.Application.Mail;
using OpportunityPilot.Domain.Drafts;
using OpportunityPilot.Domain.Integrations;
using OpportunityPilot.Domain.Opportunities;
using OpportunityPilot.Domain.Outreach;

namespace OpportunityPilot.Application.Integrations;

/// <summary>
/// Tells the person's Slack, Teams or webhook what happened. A notification never blocks or fails the action that caused it:
/// failures are recorded on the connection and shown in Settings.
/// </summary>
public sealed class IntegrationNotifier(
    IAppDbContext db, TimeProvider clock, TokenProtector protector, IConnectorHttp http, IOptions<IntegrationOptions> options,
    IOptions<MailOptions> mail, ILogger<IntegrationNotifier> logger)
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;
    private string? AppLink => string.IsNullOrWhiteSpace(mail.Value.WebBase) ? null : mail.Value.WebBase!.TrimEnd('/');

    public async Task NotifyAsync(Guid ownerId, string evt, string title, IReadOnlyList<string> lines, string? path, CancellationToken ct)
    {
        try
        {
            var rows = await db.IntegrationConnections.Where(c => c.OwnerId == ownerId && (c.Kind == IntegrationKind.Slack || c.Kind == IntegrationKind.Teams || c.Kind == IntegrationKind.Webhook)).ToListAsync(ct);
            rows = rows.Where(r => IntegrationEvents.Parse(r.Events).Contains(evt)).ToList();
            if (rows.Count == 0) return;
            var message = new ChannelMessage(evt, title, lines, AppLink is null ? null : AppLink + (path ?? ""));
            foreach (var row in rows) await PostAsync(row, message, ct);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Notification {Event} for an owner failed", evt);
        }
    }

    private async Task PostAsync(IntegrationConnection row, ChannelMessage message, CancellationToken ct)
    {
        var url = protector.Unprotect(row.EncryptedSecret);
        if (url is null) { row.MarkFailed("The stored address can no longer be read on this server. Connect again."); return; }
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await WebhookPoster.PostAsync(http, row.Kind, url, message, Now, timeout.Token);
            row.MarkUsed(Now);
        }
        catch (ConnectorProblem p) { row.MarkFailed(p.Message); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { row.MarkFailed($"{IntegrationKinds.Name(row.Kind)} took too long to answer."); }
    }

    /// <summary>Once a day after the digest hour: what is due, what is waiting to be sent and what is new. Nothing is posted on an empty day.</summary>
    public async Task<int> SendDigestsAsync(CancellationToken ct)
    {
        var now = Now;
        if (now.Hour < options.Value.DigestHourUtc) return 0;
        var today = DateOnly.FromDateTime(now);
        var due = await db.IntegrationConnections
            .Where(c => (c.Kind == IntegrationKind.Slack || c.Kind == IntegrationKind.Teams || c.Kind == IntegrationKind.Webhook) && (c.LastDigestOn == null || c.LastDigestOn < today))
            .OrderBy(c => c.OwnerId).Take(200).ToListAsync(ct);
        var sent = 0;
        foreach (var owner in due.Where(r => IntegrationEvents.Parse(r.Events).Contains(IntegrationEvents.Digest)).GroupBy(r => r.OwnerId))
        {
            var lines = await DigestLinesAsync(owner.Key, now, ct);
            foreach (var row in owner)
            {
                if (lines.Count > 0) { await PostAsync(row, new ChannelMessage(IntegrationEvents.Digest, "Your OpportunityPilot day", lines, AppLink), ct); sent++; }
                row.MarkDigestSent(today);
            }
        }
        foreach (var row in due.Where(r => !IntegrationEvents.Parse(r.Events).Contains(IntegrationEvents.Digest))) row.MarkDigestSent(today);
        await db.SaveChangesAsync(ct);
        return sent;
    }

    public async Task<List<string>> DigestLinesAsync(Guid owner, DateTime now, CancellationToken ct)
    {
        var endOfDay = now.Date.AddDays(1);
        var followUps = await db.NextActions.CountAsync(a => a.OwnerId == owner && a.State == NextActionState.Open && a.DueAt < endOfDay, ct);
        var overdue = await db.NextActions.CountAsync(a => a.OwnerId == owner && a.State == NextActionState.Open && a.DueAt < now.Date, ct);
        var approved = await db.OutreachDrafts.CountAsync(d => d.OwnerId == owner && d.State == DraftState.Approved, ct);
        var newLeads = await db.Opportunities.CountAsync(o => o.OwnerId == owner && o.CreatedAt >= now.AddDays(-1) && o.Outcome != FilterOutcome.Excluded, ct);
        var shortlisted = await db.Opportunities.CountAsync(o => o.OwnerId == owner && o.Status == OpportunityStatus.Shortlisted && o.UpdatedAt >= now.AddDays(-1), ct);
        var lines = new List<string>();
        if (followUps > 0) lines.Add($"{followUps} follow-up{S(followUps)} due today" + (overdue > 0 ? $" ({overdue} overdue)" : ""));
        if (approved > 0) lines.Add($"{approved} approved message{S(approved)} waiting to be sent");
        if (newLeads > 0) lines.Add($"{newLeads} new lead{S(newLeads)} found in the last day");
        if (shortlisted > 0) lines.Add($"{shortlisted} lead{S(shortlisted)} shortlisted in the last day");
        return lines;
    }

    /// <summary>A private calendar of open follow-ups (and the person's scheduled interviews) that Google or Outlook can subscribe to.</summary>
    public async Task<string?> CalendarAsync(Guid connectionId, string key, CancellationToken ct)
    {
        var row = await db.IntegrationConnections.AsNoTracking().FirstOrDefaultAsync(c => c.Id == connectionId && c.Kind == IntegrationKind.CalendarFeed, ct);
        var stored = row is null ? null : protector.Unprotect(row.EncryptedSecret);
        if (stored is null || !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(stored), Encoding.UTF8.GetBytes(key ?? ""))) return null;

        var owner = row!.OwnerId;
        var since = Now.AddDays(-30);
        var actions = await (from a in db.NextActions.AsNoTracking()
                             join o in db.Opportunities.AsNoTracking() on a.OpportunityId equals o.Id
                             where a.OwnerId == owner && a.State == NextActionState.Open && a.DueAt >= since
                             orderby a.DueAt
                             select new { a.Id, a.DueAt, a.Kind, a.Note, o.Title, o.Organization }).Take(500).ToListAsync(ct);
        var interviews = await db.StaffingInterviews.AsNoTracking()
            .Where(i => i.OwnerId == owner && i.ScheduledAt != null && i.ScheduledAt >= since)
            .OrderBy(i => i.ScheduledAt).Select(i => new { i.Id, i.ScheduledAt, i.DurationMinutes, i.Round, i.Location }).Take(200).ToListAsync(ct);

        var ics = new StringBuilder();
        ics.Append("BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//OpportunityPilot//Follow-ups//EN\r\nCALSCALE:GREGORIAN\r\nMETHOD:PUBLISH\r\n");
        ics.Append("X-WR-CALNAME:OpportunityPilot\r\nREFRESH-INTERVAL;VALUE=DURATION:PT1H\r\nX-PUBLISHED-TTL:PT1H\r\n");
        var stamp = Stamp(Now);
        foreach (var a in actions)
        {
            var what = a.Kind switch { NextActionKind.FollowUp => "Follow up", NextActionKind.Call => "Call", NextActionKind.CheckStatus => "Check status", _ => "To do" };
            Event(ics, $"action-{a.Id:N}", stamp, a.DueAt, a.DueAt.AddMinutes(15), $"{what}: {(string.IsNullOrWhiteSpace(a.Organization) ? a.Title : a.Organization)}", $"{a.Title}\n{a.Note}", null);
        }
        foreach (var i in interviews)
            Event(ics, $"interview-{i.Id:N}", stamp, i.ScheduledAt!.Value, i.ScheduledAt.Value.AddMinutes(i.DurationMinutes ?? 60), $"Interview, round {i.Round}", "", i.Location);
        ics.Append("END:VCALENDAR\r\n");
        return ics.ToString();
    }

    private static void Event(StringBuilder ics, string uid, string stamp, DateTime start, DateTime end, string summary, string description, string? location)
    {
        ics.Append("BEGIN:VEVENT\r\n");
        Line(ics, "UID:" + uid + "@opportunitypilot");
        Line(ics, "DTSTAMP:" + stamp);
        Line(ics, "DTSTART:" + Stamp(start));
        Line(ics, "DTEND:" + Stamp(end));
        Line(ics, "SUMMARY:" + Escape(summary));
        if (!string.IsNullOrWhiteSpace(description)) Line(ics, "DESCRIPTION:" + Escape(description.Trim()));
        if (!string.IsNullOrWhiteSpace(location)) Line(ics, "LOCATION:" + Escape(location));
        ics.Append("END:VEVENT\r\n");
    }

    /// <summary>RFC 5545 lines are folded at 75 octets.</summary>
    private static void Line(StringBuilder ics, string line)
    {
        var bytes = 0;
        var start = 0;
        for (var i = 0; i < line.Length; i++)
        {
            var size = Encoding.UTF8.GetByteCount(line.AsSpan(i, char.IsHighSurrogate(line[i]) && i + 1 < line.Length ? 2 : 1));
            if (bytes + size > (start == 0 ? 75 : 74))
            {
                ics.Append(line, start, i - start).Append("\r\n ");
                start = i;
                bytes = 0;
            }
            bytes += size;
            if (char.IsHighSurrogate(line[i])) i++;
        }
        ics.Append(line, start, line.Length - start).Append("\r\n");
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace("\r\n", "\\n").Replace("\n", "\\n");
    private static string Stamp(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
    private static string S(int n) => n == 1 ? "" : "s";
}
