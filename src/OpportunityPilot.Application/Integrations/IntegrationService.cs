using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using OpportunityPilot.Application.Abstractions;
using OpportunityPilot.Application.Common;
using OpportunityPilot.Application.Mail;
using OpportunityPilot.Domain.Common;
using OpportunityPilot.Domain.Drafts;
using OpportunityPilot.Domain.Integrations;
using OpportunityPilot.Domain.Opportunities;

namespace OpportunityPilot.Application.Integrations;

/// <summary>
/// Connect, check and disconnect services, and send leads to a CRM or database. Every connect proves the secret works against the
/// real service before anything is stored; the secret is stored only encrypted and never returned.
/// </summary>
public sealed class IntegrationService(
    IAppDbContext db, ICurrentUser user, TimeProvider clock, TokenProtector protector, IConnectorHttp http, IEnumerable<ILeadTarget> targets,
    IEnumerable<IPeopleSource> people, ConnectorOauthService oauth)
{
    public const int MaxPush = 25;
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task<IReadOnlyList<IntegrationDto>> ListAsync(string feedBase, CancellationToken ct)
    {
        var rows = await db.IntegrationConnections.AsNoTracking().Where(c => c.OwnerId == user.OwnerId).ToListAsync(ct);
        return Enum.GetValues<IntegrationKind>().Select(k =>
        {
            var row = rows.FirstOrDefault(r => r.Kind == k);
            return row is null ? new IntegrationDto(k, false, null, [], null, null, null, null, oauth.Configured(k)) : Dto(row, feedBase);
        }).ToList();
    }

    public async Task<IntegrationDto> ConnectAsync(IntegrationKind kind, ConnectIntegrationRequest r, string feedBase, CancellationToken ct)
    {
        if (!Enum.IsDefined(kind)) throw new NotFoundException("Unknown service.");
        string label, secret, settings = "{}", events = "";
        if (IntegrationKinds.IsWebhook(kind))
        {
            if (WebhookPoster.CheckUrl(kind, r.Url) is { } problem) throw Invalid("url", problem);
            secret = r.Url!.Trim();
            events = IntegrationEvents.Normalise(r.Events);
            try
            {
                await WebhookPoster.PostAsync(http, kind, secret, new ChannelMessage("test", "OpportunityPilot is connected",
                    ["Messages about your leads will appear here.", "You choose which ones in Settings → Connectors."]), Now, ct);
            }
            catch (ConnectorProblem p) { throw Invalid("url", p.Message); }
            label = kind == IntegrationKind.Webhook && Uri.TryCreate(secret, UriKind.Absolute, out var u) ? $"Webhook to {u.Host}" : $"{IntegrationKinds.Name(kind)} channel";
        }
        else if (IntegrationKinds.IsLeadTarget(kind))
        {
            var token = r.Token?.Trim();
            if (string.IsNullOrEmpty(token) || token.Length > 500 || token.Any(char.IsWhiteSpace)) throw Invalid("token", $"Paste the {IntegrationKinds.Name(kind)} token.");
            try { (label, settings) = await Target(kind).VerifyAsync(token, r.Target, r.Table, ct); }
            catch (ConnectorProblem p) { throw Invalid(kind == IntegrationKind.HubSpot ? "token" : "target", p.Message); }
            secret = token;
        }
        else if (IntegrationKinds.IsPeopleSource(kind))
        {
            var key = r.Token?.Trim();
            if (string.IsNullOrEmpty(key) || key.Length > 200 || key.Any(char.IsWhiteSpace)) throw Invalid("token", $"Paste the {IntegrationKinds.Name(kind)} API key.");
            try { label = await Source(kind).VerifyAsync(key, ct); }
            catch (ConnectorProblem p) { throw Invalid("token", p.Message); }
            secret = key;
        }
        else
        {
            secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
            label = "Calendar feed";
        }

        var row = await db.IntegrationConnections.FirstOrDefaultAsync(c => c.OwnerId == user.OwnerId && c.Kind == kind, ct);
        var encrypted = protector.Protect(secret);
        if (row is null) db.IntegrationConnections.Add(row = new IntegrationConnection(user.OwnerId, kind, label, encrypted, settings, events, Now));
        else row.Reconnect(label, encrypted, settings, events, Now);
        if (IntegrationKinds.IsWebhook(kind)) row.MarkUsed(Now);
        await db.SaveChangesAsync(ct);
        return Dto(row, feedBase);
    }

    public async Task<IntegrationTestResult> TestAsync(IntegrationKind kind, CancellationToken ct)
    {
        var row = await RowAsync(kind, ct);
        if (ConnectorOauthService.Flags(row.SettingsJson).NeedsTarget) return new(false, NeedsTargetMessage(kind));
        var secret = IntegrationKinds.IsLeadTarget(kind) ? await oauth.AccessTokenAsync(row, ct)
            : protector.Unprotect(row.EncryptedSecret) ?? throw new ConflictException("The stored login can no longer be read on this server. Connect again.");
        try
        {
            if (IntegrationKinds.IsWebhook(kind))
                await WebhookPoster.PostAsync(http, kind, secret, new ChannelMessage("test", "Test from OpportunityPilot", ["If you can read this, the connection works."]), Now, ct);
            else if (IntegrationKinds.IsLeadTarget(kind))
            {
                var s = System.Text.Json.JsonDocument.Parse(row.SettingsJson).RootElement;
                var target = kind == IntegrationKind.Notion ? s.Str("databaseId") : s.Str("baseId");
                await Target(kind).VerifyAsync(secret, target, s.Str("tableId"), ct);
            }
            else if (IntegrationKinds.IsPeopleSource(kind))
                await Source(kind).VerifyAsync(secret, ct);
            row.MarkUsed(Now);
            await db.SaveChangesAsync(ct);
            return new(true, IntegrationKinds.IsWebhook(kind) ? "Sent. Check the channel."
                : IntegrationKinds.IsPeopleSource(kind) ? $"{IntegrationKinds.Name(kind)} accepted the API key." : $"{IntegrationKinds.Name(kind)} accepted the token.");
        }
        catch (ConnectorProblem p)
        {
            row.MarkFailed(p.Message);
            await db.SaveChangesAsync(ct);
            return new(false, p.Message);
        }
    }

    public async Task<IntegrationDto> SetEventsAsync(IntegrationKind kind, UpdateIntegrationEventsRequest r, string feedBase, CancellationToken ct)
    {
        var row = await RowAsync(kind, ct);
        if (!IntegrationKinds.IsWebhook(kind)) throw Invalid("events", "Only Slack, Teams and webhooks post messages.");
        row.SetEvents(IntegrationEvents.Normalise(r?.Events ?? []));
        await db.SaveChangesAsync(ct);
        return Dto(row, feedBase);
    }

    public async Task DisconnectAsync(IntegrationKind kind, CancellationToken ct)
    {
        var row = await db.IntegrationConnections.FirstOrDefaultAsync(c => c.OwnerId == user.OwnerId && c.Kind == kind, ct);
        if (row is null) return;
        db.IntegrationConnections.Remove(row);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Sends leads one at a time; a lead already in the other service is reported, not duplicated.</summary>
    public async Task<IReadOnlyList<PushLeadResult>> PushAsync(IntegrationKind kind, PushLeadsRequest r, CancellationToken ct)
    {
        if (!IntegrationKinds.IsLeadTarget(kind)) throw Invalid("kind", "Leads can be sent to HubSpot, Notion or Airtable.");
        var ids = r?.OpportunityIds?.Distinct().ToList() ?? [];
        if (ids.Count == 0) throw Invalid("opportunityIds", "Choose at least one lead.");
        if (ids.Count > MaxPush) throw Invalid("opportunityIds", $"At most {MaxPush} leads at once.");
        var row = await RowAsync(kind, ct);
        if (ConnectorOauthService.Flags(row.SettingsJson).NeedsTarget) throw new ConflictException(NeedsTargetMessage(kind));
        var token = await oauth.AccessTokenAsync(row, ct);

        var owner = user.OwnerId;
        var opps = await db.Opportunities.AsNoTracking().Where(o => o.OwnerId == owner && ids.Contains(o.Id)).ToListAsync(ct);
        var emails = await db.OutreachDrafts.AsNoTracking()
            .Where(d => d.OwnerId == owner && ids.Contains(d.OpportunityId) && d.Channel == DraftChannel.Email && d.Recipient != null)
            .OrderByDescending(d => d.UpdatedAt).Select(d => new { d.OpportunityId, d.Recipient }).ToListAsync(ct);

        var results = new List<PushLeadResult>();
        foreach (var id in ids)
        {
            var o = opps.FirstOrDefault(x => x.Id == id);
            if (o is null) { results.Add(new(id, "Failed", "Lead not found.", null)); continue; }
            try { results.Add(await Target(kind).PushAsync(token, row.SettingsJson, Lead(o, emails.FirstOrDefault(e => e.OpportunityId == id)?.Recipient), ct)); }
            catch (ConnectorProblem p)
            {
                results.Add(new(id, "Failed", p.Message, null));
                row.MarkFailed(p.Message);
                // A refused token fails every lead the same way: stop instead of repeating it.
                if (p.Message.Contains("refused the token", StringComparison.Ordinal)) break;
            }
        }
        if (results.Any(x => x.Outcome != "Failed")) row.MarkUsed(Now);
        await db.SaveChangesAsync(ct);
        return results;
    }

    /// <summary>Notion databases or Airtable tables the connection can see, so the person can choose where leads go.</summary>
    public async Task<IReadOnlyList<IntegrationTargetDto>> TargetsAsync(IntegrationKind kind, CancellationToken ct)
    {
        if (kind is not (IntegrationKind.Notion or IntegrationKind.Airtable)) throw Invalid("kind", "Only Notion and Airtable have a database or table to choose.");
        var row = await RowAsync(kind, ct);
        var token = await oauth.AccessTokenAsync(row, ct);
        try { return await Target(kind).ListTargetsAsync(token, ct); }
        catch (ConnectorProblem p) { throw new ConflictException(p.Message); }
    }

    /// <summary>Chooses where leads go; the choice is checked with the service and its columns are matched, as when a link is pasted.</summary>
    public async Task<IntegrationDto> SetTargetAsync(IntegrationKind kind, SelectIntegrationTargetRequest r, string feedBase, CancellationToken ct)
    {
        if (kind is not (IntegrationKind.Notion or IntegrationKind.Airtable)) throw Invalid("kind", "Only Notion and Airtable have a database or table to choose.");
        if (string.IsNullOrWhiteSpace(r?.Target) || r.Target.Length > 200 || r.Table?.Length > 200)
            throw Invalid("target", kind == IntegrationKind.Notion ? "Choose a database." : "Choose a table.");
        var row = await RowAsync(kind, ct);
        var token = await oauth.AccessTokenAsync(row, ct);
        string label, settings;
        try { (label, settings) = await Target(kind).VerifyAsync(token, r.Target, r.Table, ct); }
        catch (ConnectorProblem p) { throw Invalid("target", p.Message); }
        row.ChangeTarget(label, ConnectorOauthService.KeepOauth(settings, row.SettingsJson));
        await db.SaveChangesAsync(ct);
        return Dto(row, feedBase);
    }

    private static string NeedsTargetMessage(IntegrationKind kind) =>
        kind == IntegrationKind.Notion ? "Choose which Notion database leads go to first." : "Choose which Airtable table leads go to first.";

    public static LeadRecord Lead(Opportunity o, string? email)
    {
        var website = o.Mode == OpportunityMode.Job ? null : o.Url;
        var domain = Uri.TryCreate(website, UriKind.Absolute, out var u) ? u.Host.ToLowerInvariant().Replace("www.", "") : null;
        var name = string.IsNullOrWhiteSpace(o.Organization) ? o.Title : o.Organization;
        var about = string.IsNullOrWhiteSpace(o.Description) ? "" : HubSpotTarget.Cut(o.Description.Trim(), 1500);
        var notes = $"{o.Title}. Match score {o.Score}. Status {o.Status}." + (o.Url is null ? "" : $" Source: {o.Url}.") + (about.Length > 0 ? "\n\n" + about : "") +
                    "\n\nAdded from OpportunityPilot.";
        return new(o.Id, name, o.Title, website, domain, o.Location, o.Score, o.Status.ToString(), o.Url, email, notes);
    }

    private ILeadTarget Target(IntegrationKind kind) => targets.First(t => t.Kind == kind);
    private IPeopleSource Source(IntegrationKind kind) => people.First(t => t.Kind == kind);

    private async Task<IntegrationConnection> RowAsync(IntegrationKind kind, CancellationToken ct) =>
        await db.IntegrationConnections.FirstOrDefaultAsync(c => c.OwnerId == user.OwnerId && c.Kind == kind, ct)
        ?? throw new NotFoundException($"{IntegrationKinds.Name(kind)} is not connected.");

    private IntegrationDto Dto(IntegrationConnection row, string feedBase)
    {
        var (signedIn, needsTarget) = ConnectorOauthService.Flags(row.SettingsJson);
        return new(row.Kind, true, row.Label, IntegrationEvents.Parse(row.Events), row.ConnectedAt, row.LastUsedAt, row.LastError,
            row.Kind == IntegrationKind.CalendarFeed && protector.Unprotect(row.EncryptedSecret) is { } key ? $"{feedBase.TrimEnd('/')}/api/v1/calendar/{row.Id:N}/{key}.ics" : null,
            oauth.Configured(row.Kind), signedIn, needsTarget);
    }

    private static RequestValidationException Invalid(string field, string message) => new(new Dictionary<string, string[]> { [field] = [message] });
}
