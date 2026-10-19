using System.Text.Json;
using System.Text.RegularExpressions;
using OpportunityPilot.Domain.Integrations;

namespace OpportunityPilot.Application.Integrations;

/// <summary>A CRM or database that receives leads. Verify proves the token works before anything is stored.</summary>
public interface ILeadTarget
{
    IntegrationKind Kind { get; }
    Task<(string Label, string SettingsJson)> VerifyAsync(string token, string? target, string? table, CancellationToken ct);
    Task<PushLeadResult> PushAsync(string token, string settingsJson, LeadRecord lead, CancellationToken ct);
    /// <summary>Where leads could go with this token (Notion databases, Airtable tables), so a person who signed in can pick one. Empty for HubSpot.</summary>
    Task<IReadOnlyList<IntegrationTargetDto>> ListTargetsAsync(string token, CancellationToken ct);
}

internal static class Reply
{
    public static ConnectorProblem Problem(string service, ConnectorResponse r, string scopes) => new(r.Status switch
    {
        0 => $"{service} could not be reached. Try again in a minute.",
        401 => $"{service} refused the token. Copy it again from {service} (it may have been deleted or rotated).",
        403 => $"The token works but is not allowed to do this. It needs: {scopes}.",
        404 => $"{service} could not find that. Check the link, and that the token has access to it.",
        429 => $"{service} is limiting requests right now. Try again in a minute.",
        _ => $"{service} answered {r.Status}{Detail(r)}."
    });

    private static string Detail(ConnectorResponse r)
    {
        var j = r.Json();
        var text = j.ValueKind == JsonValueKind.Object && j.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString()
            : j.ValueKind == JsonValueKind.Object && j.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object && e.TryGetProperty("message", out var em) ? em.GetString()
            : null;
        return string.IsNullOrWhiteSpace(text) ? "" : ": " + (text.Length > 200 ? text[..200] : text);
    }

    public static string? Str(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null
        } : null;
}

/// <summary>HubSpot with a private-app token: the company (matched by its web domain, else its name), the contact when an address is known, and a note.</summary>
public sealed class HubSpotTarget(IConnectorHttp http, TimeProvider? clock = null) : ILeadTarget
{
    private const string Api = "https://api.hubapi.com";
    private const string Scopes = "crm.objects.companies.read, crm.objects.companies.write, crm.objects.contacts.read, crm.objects.contacts.write";
    public IntegrationKind Kind => IntegrationKind.HubSpot;

    public async Task<(string Label, string SettingsJson)> VerifyAsync(string token, string? target, string? table, CancellationToken ct)
    {
        var r = await http.SendAsync(new(HttpMethod.Get, $"{Api}/account-info/v3/details", Bearer: token), ct);
        if (!r.Ok) throw Reply.Problem("HubSpot", r, Scopes);
        var portal = r.Json().Str("portalId") ?? throw new ConnectorProblem("HubSpot did not say which account this token belongs to.");
        var companies = await http.SendAsync(new(HttpMethod.Get, $"{Api}/crm/v3/objects/companies?limit=1", Bearer: token), ct);
        if (!companies.Ok) throw Reply.Problem("HubSpot", companies, Scopes);
        var domain = r.Json().Str("uiDomain") ?? "app.hubspot.com";
        return ($"HubSpot account {portal}", ConnectorJson.Write(new { portalId = portal, uiDomain = domain }));
    }

    public async Task<PushLeadResult> PushAsync(string token, string settingsJson, LeadRecord lead, CancellationToken ct)
    {
        var settings = JsonDocument.Parse(settingsJson).RootElement;
        var link = (string id) => $"https://{settings.Str("uiDomain") ?? "app.hubspot.com"}/contacts/{settings.Str("portalId")}/record/0-2/{id}";

        var (byProp, value) = lead.Domain is { } d ? ("domain", d) : ("name", lead.Name);
        var found = await SearchAsync(token, "companies", byProp, value, ct);
        if (found is not null) return new(lead.Id, "AlreadyThere", $"A company with this {byProp} is already in HubSpot.", link(found));

        var created = await http.SendAsync(new(HttpMethod.Post, $"{Api}/crm/v3/objects/companies", ConnectorJson.Write(new
        {
            properties = new Dictionary<string, string?> { ["name"] = lead.Name, ["domain"] = lead.Domain, ["city"] = lead.Location, ["description"] = Cut(lead.Notes, 1000) }
                .Where(p => !string.IsNullOrWhiteSpace(p.Value)).ToDictionary()
        }), token), ct);
        if (!created.Ok) throw Reply.Problem("HubSpot", created, Scopes);
        var companyId = created.Json().Str("id")!;

        if (lead.Email is { } email)
        {
            var contactId = await SearchAsync(token, "contacts", "email", email, ct);
            if (contactId is null)
            {
                var c = await http.SendAsync(new(HttpMethod.Post, $"{Api}/crm/v3/objects/contacts", ConnectorJson.Write(new { properties = new { email, company = lead.Name } }), token), ct);
                contactId = c.Ok ? c.Json().Str("id") : null;
            }
            if (contactId is not null)
                await http.SendAsync(new(HttpMethod.Put, $"{Api}/crm/v4/objects/contacts/{contactId}/associations/default/companies/{companyId}", null, token), ct);
        }

        // A note on the company says where the lead came from; HubSpot's own type 190 links a note to a company.
        await http.SendAsync(new(HttpMethod.Post, $"{Api}/crm/v3/objects/notes", ConnectorJson.Write(new
        {
            properties = new { hs_timestamp = (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime.ToString("O"), hs_note_body = Cut($"From OpportunityPilot: {lead.Title}. Score {lead.Score}. {lead.Link}", 1000) },
            associations = new[] { new { to = new { id = companyId }, types = new[] { new { associationCategory = "HUBSPOT_DEFINED", associationTypeId = 190 } } } }
        }), token), ct);
        return new(lead.Id, "Added", null, link(companyId));
    }

    private async Task<string?> SearchAsync(string token, string objectType, string property, string value, CancellationToken ct)
    {
        var r = await http.SendAsync(new(HttpMethod.Post, $"{Api}/crm/v3/objects/{objectType}/search", ConnectorJson.Write(new
        {
            filterGroups = new[] { new { filters = new[] { new { propertyName = property, @operator = "EQ", value } } } },
            limit = 1
        }), token), ct);
        if (!r.Ok) throw Reply.Problem("HubSpot", r, Scopes);
        return r.Json().TryGetProperty("results", out var rs) && rs.ValueKind == JsonValueKind.Array && rs.GetArrayLength() > 0 ? rs[0].Str("id") : null;
    }

    public Task<IReadOnlyList<IntegrationTargetDto>> ListTargetsAsync(string token, CancellationToken ct) => Task.FromResult<IReadOnlyList<IntegrationTargetDto>>([]);

    internal static string Cut(string s, int max) => s.Length > max ? s[..max] : s;
}

/// <summary>Notion with an internal-integration secret: one page per lead in a database the person shared with the integration.</summary>
public sealed partial class NotionTarget(IConnectorHttp http) : ILeadTarget
{
    private const string Api = "https://api.notion.com/v1";
    private static readonly Dictionary<string, string> Version = new() { ["Notion-Version"] = "2022-06-28" };
    public IntegrationKind Kind => IntegrationKind.Notion;

    [GeneratedRegex("([0-9a-fA-F]{32}|[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})")]
    private static partial Regex IdPattern();

    public static string? DatabaseId(string? target)
    {
        if (string.IsNullOrWhiteSpace(target)) return null;
        var path = Uri.TryCreate(target.Trim(), UriKind.Absolute, out var u) ? u.AbsolutePath : target.Trim();
        var m = IdPattern().Matches(path).LastOrDefault();
        return m is null ? null : m.Value.Replace("-", "").ToLowerInvariant();
    }

    public async Task<(string Label, string SettingsJson)> VerifyAsync(string token, string? target, string? table, CancellationToken ct)
    {
        var id = DatabaseId(target) ?? throw new ConnectorProblem("Paste the link of the Notion database (open it as a full page, then Share → Copy link).");
        var r = await http.SendAsync(new(HttpMethod.Get, $"{Api}/databases/{id}", Bearer: token, Headers: Version), ct);
        if (r.Status == 404) throw new ConnectorProblem("Notion cannot see that database. In Notion open the database → ••• → Connections → add your integration, then try again.");
        if (!r.Ok) throw Reply.Problem("Notion", r, "Read content and Insert content capabilities");
        var db = r.Json();
        string? Prop(Func<string, string, bool> match)
        {
            if (!db.TryGetProperty("properties", out var props)) return null;
            foreach (var p in props.EnumerateObject())
                if (match(p.Name, p.Value.Str("type") ?? "")) return p.Name;
            return null;
        }
        bool Named(string name, params string[] words) => words.Any(w => name.Contains(w, StringComparison.OrdinalIgnoreCase));
        var title = Prop((_, t) => t == "title") ?? throw new ConnectorProblem("That Notion database has no title column.");
        var settings = new
        {
            databaseId = id,
            title,
            url = Prop((n, t) => t == "url"),
            email = Prop((_, t) => t == "email"),
            score = Prop((n, t) => t == "number" && Named(n, "score", "match")),
            role = Prop((n, t) => t == "rich_text" && Named(n, "role", "title", "position")),
            location = Prop((n, t) => t == "rich_text" && Named(n, "location", "city", "place")),
            status = Prop((n, t) => t is "select" or "status" && Named(n, "status", "stage"))
        };
        var name = db.TryGetProperty("title", out var tt) && tt.ValueKind == JsonValueKind.Array && tt.GetArrayLength() > 0 ? tt[0].Str("plain_text") : null;
        return ($"Notion: {(string.IsNullOrWhiteSpace(name) ? "database" : name)}", ConnectorJson.Write(settings));
    }

    public async Task<PushLeadResult> PushAsync(string token, string settingsJson, LeadRecord lead, CancellationToken ct)
    {
        var s = JsonDocument.Parse(settingsJson).RootElement;
        var id = s.Str("databaseId")!;
        var title = s.Str("title")!;
        var existing = await http.SendAsync(new(HttpMethod.Post, $"{Api}/databases/{id}/query",
            ConnectorJson.Write(new { filter = new { property = title, title = new { equals = lead.Name } }, page_size = 1 }), token, Version), ct);
        if (!existing.Ok) throw Reply.Problem("Notion", existing, "Read content and Insert content capabilities");
        if (existing.Json().TryGetProperty("results", out var rs) && rs.GetArrayLength() > 0)
            return new(lead.Id, "AlreadyThere", "A page with this name is already in the database.", rs[0].Str("url"));

        var props = new Dictionary<string, object> { [title] = new { title = new[] { Text(lead.Name) } } };
        if (s.Str("url") is { } urlProp && (lead.Website ?? lead.Link) is { } link) props[urlProp] = new { url = link };
        if (s.Str("email") is { } emailProp && lead.Email is { } email) props[emailProp] = new { email };
        if (s.Str("score") is { } scoreProp) props[scoreProp] = new { number = lead.Score };
        if (s.Str("role") is { } roleProp) props[roleProp] = new { rich_text = new[] { Text(lead.Title) } };
        if (s.Str("location") is { } locProp && lead.Location is { } loc) props[locProp] = new { rich_text = new[] { Text(loc) } };

        var r = await http.SendAsync(new(HttpMethod.Post, $"{Api}/pages", ConnectorJson.Write(new
        {
            parent = new { database_id = id },
            properties = props,
            children = new[] { new { @object = "block", type = "paragraph", paragraph = new { rich_text = new[] { Text(lead.Notes) } } } }
        }), token, Version), ct);
        if (!r.Ok) throw Reply.Problem("Notion", r, "Insert content capability");
        return new(lead.Id, "Added", null, r.Json().Str("url"));
    }

    /// <summary>The databases the person shared while signing in (Notion's search, databases only, newest edits first).</summary>
    public async Task<IReadOnlyList<IntegrationTargetDto>> ListTargetsAsync(string token, CancellationToken ct)
    {
        var r = await http.SendAsync(new(HttpMethod.Post, $"{Api}/search", ConnectorJson.Write(new
        {
            filter = new { property = "object", value = "database" },
            sort = new { timestamp = "last_edited_time", direction = "descending" },
            page_size = 100
        }), token, Version), ct);
        if (!r.Ok) throw Reply.Problem("Notion", r, "Read content capability");
        if (!r.Json().TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) return [];
        return results.EnumerateArray()
            .Where(d => d.Str("object") == "database" && DatabaseId(d.Str("id")) is not null)
            .Select(d => new IntegrationTargetDto(DatabaseId(d.Str("id"))!, null,
                d.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.Array && t.GetArrayLength() > 0 && t[0].Str("plain_text") is { Length: > 0 } name ? name : "Untitled database", null))
            .ToList();
    }

    private static object Text(string s) => new { type = "text", text = new { content = HubSpotTarget.Cut(s, 1900) } };
}

/// <summary>Airtable with a personal access token: one record per lead, filling only the columns the table already has.</summary>
public sealed partial class AirtableTarget(IConnectorHttp http) : ILeadTarget
{
    private const string Api = "https://api.airtable.com/v0";
    private const string Scopes = "data.records:read, data.records:write and schema.bases:read, with access to this base";
    public IntegrationKind Kind => IntegrationKind.Airtable;

    [GeneratedRegex(@"\b(app[A-Za-z0-9]{14})\b")] private static partial Regex BasePattern();
    [GeneratedRegex(@"\b(tbl[A-Za-z0-9]{14})\b")] private static partial Regex TablePattern();

    public static (string? Base, string? Table) Ids(string? target) =>
        (target is null ? null : BasePattern().Match(target) is { Success: true } b ? b.Value : null,
         target is null ? null : TablePattern().Match(target) is { Success: true } t ? t.Value : null);

    public async Task<(string Label, string SettingsJson)> VerifyAsync(string token, string? target, string? table, CancellationToken ct)
    {
        var (baseId, tableFromLink) = Ids(target);
        if (baseId is null) throw new ConnectorProblem("Paste the link of the Airtable base or table (it contains an id starting with app).");
        var r = await http.SendAsync(new(HttpMethod.Get, $"{Api}/meta/bases/{baseId}/tables", Bearer: token), ct);
        if (!r.Ok) throw Reply.Problem("Airtable", r, Scopes);
        var tables = r.Json().TryGetProperty("tables", out var ts) ? ts.EnumerateArray().ToList() : [];
        var want = string.IsNullOrWhiteSpace(table) ? tableFromLink : table.Trim();
        var t = want is null ? tables.FirstOrDefault()
            : tables.FirstOrDefault(x => x.Str("id") == want || string.Equals(x.Str("name"), want, StringComparison.OrdinalIgnoreCase));
        if (t.ValueKind != JsonValueKind.Object) throw new ConnectorProblem(want is null ? "That base has no tables." : $"There is no table called \"{want}\" in that base.");

        var primary = t.Str("primaryFieldId");
        var fields = t.TryGetProperty("fields", out var fs) ? fs.EnumerateArray().Select(f => (Id: f.Str("id"), Name: f.Str("name") ?? "", Type: f.Str("type") ?? "")).ToList() : [];
        string? Find(Func<(string? Id, string Name, string Type), bool> match) => fields.FirstOrDefault(f => f.Id != primary && match(f)).Name is { Length: > 0 } n ? n : null;
        bool Named(string name, params string[] words) => words.Any(w => name.Equals(w, StringComparison.OrdinalIgnoreCase));
        var settings = new
        {
            baseId,
            tableId = t.Str("id"),
            name = fields.FirstOrDefault(f => f.Id == primary).Name,
            role = Find(f => Named(f.Name, "Role", "Title", "Position", "Job")),
            website = Find(f => f.Type == "url" || Named(f.Name, "Website", "URL", "Link")),
            email = Find(f => f.Type == "email" || Named(f.Name, "Email", "Contact")),
            location = Find(f => Named(f.Name, "Location", "City", "Place")),
            score = Find(f => f.Type == "number" && Named(f.Name, "Score", "Match")),
            notes = Find(f => f.Type is "multilineText" or "richText" || Named(f.Name, "Notes", "Description"))
        };
        if (string.IsNullOrEmpty(settings.name)) throw new ConnectorProblem("Airtable did not say which column is the table's first column.");
        return ($"Airtable: {t.Str("name")}", ConnectorJson.Write(settings));
    }

    public async Task<PushLeadResult> PushAsync(string token, string settingsJson, LeadRecord lead, CancellationToken ct)
    {
        var s = JsonDocument.Parse(settingsJson).RootElement;
        var table = $"{Api}/{s.Str("baseId")}/{s.Str("tableId")}";
        var nameField = s.Str("name")!;
        var formula = Uri.EscapeDataString($"{{{nameField.Replace("}", "")}}}='{lead.Name.Replace("\\", "\\\\").Replace("'", "\\'")}'");
        var existing = await http.SendAsync(new(HttpMethod.Get, $"{table}?maxRecords=1&filterByFormula={formula}", Bearer: token), ct);
        if (!existing.Ok) throw Reply.Problem("Airtable", existing, Scopes);
        if (existing.Json().TryGetProperty("records", out var rs) && rs.GetArrayLength() > 0)
            return new(lead.Id, "AlreadyThere", "A record with this name is already in the table.", $"https://airtable.com/{s.Str("baseId")}/{s.Str("tableId")}/{rs[0].Str("id")}");

        var fields = new Dictionary<string, object> { [nameField] = lead.Name };
        void Put(string key, object? value) { if (s.Str(key) is { } f && value is not null) fields[f] = value; }
        Put("role", lead.Title);
        Put("website", lead.Website ?? lead.Link);
        Put("email", lead.Email);
        Put("location", lead.Location);
        Put("score", lead.Score);
        Put("notes", HubSpotTarget.Cut(lead.Notes, 5000));
        var r = await http.SendAsync(new(HttpMethod.Post, table, ConnectorJson.Write(new { records = new[] { new { fields } }, typecast = true }), token), ct);
        if (!r.Ok) throw Reply.Problem("Airtable", r, Scopes);
        var id = r.Json().TryGetProperty("records", out var made) && made.GetArrayLength() > 0 ? made[0].Str("id") : null;
        return new(lead.Id, "Added", null, id is null ? null : $"https://airtable.com/{s.Str("baseId")}/{s.Str("tableId")}/{id}");
    }

    public const int MaxBases = 10;

    /// <summary>Tables in the bases the person can edit (at most <see cref="MaxBases"/> bases), each with its base name.</summary>
    public async Task<IReadOnlyList<IntegrationTargetDto>> ListTargetsAsync(string token, CancellationToken ct)
    {
        var r = await http.SendAsync(new(HttpMethod.Get, $"{Api}/meta/bases", Bearer: token), ct);
        if (!r.Ok) throw Reply.Problem("Airtable", r, Scopes);
        var bases = r.Json().TryGetProperty("bases", out var bs) && bs.ValueKind == JsonValueKind.Array
            ? bs.EnumerateArray().Where(b => b.Str("permissionLevel") is "edit" or "create" && b.Str("id") is { Length: > 0 }).Take(MaxBases).ToList() : [];
        var list = new List<IntegrationTargetDto>();
        foreach (var b in bases)
        {
            var t = await http.SendAsync(new(HttpMethod.Get, $"{Api}/meta/bases/{b.Str("id")}/tables", Bearer: token), ct);
            if (!t.Ok) throw Reply.Problem("Airtable", t, Scopes);
            if (!t.Json().TryGetProperty("tables", out var ts) || ts.ValueKind != JsonValueKind.Array) continue;
            list.AddRange(ts.EnumerateArray().Where(x => x.Str("id") is { Length: > 0 })
                .Select(x => new IntegrationTargetDto(b.Str("id")!, x.Str("id"), x.Str("name") ?? "Table", b.Str("name"))));
        }
        return list;
    }
}
