using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using OpportunityPilot.Application.Common;
using OpportunityPilot.Application.Configuration;
using OpportunityPilot.Application.JobBoards;

namespace OpportunityPilot.Application.Sales;

/// <summary>
/// Sales lead search through the "Apollo.io (no cookies required)" RapidAPI listing: companies (or people at companies) by keyword,
/// country and company size. The key is the server's <c>RapidApi__Key</c>; the host, paths and parameter names are settings
/// (<c>LeadSearch__…</c>). Without <c>RapidApi__Key</c> it uses <c>Jsearch__Key</c>: one RapidAPI account key works for every listing it subscribes to so a change on the listing needs no code change. The listing bills per returned result, so a page is
/// small and calls are capped per day (<c>Budgets__ApolloPerDay</c>).
/// </summary>
public sealed class LeadSearchOptions
{
    public const string Section = "LeadSearch";
    public string Host { get; set; } = "apollo-io-no-cookies-required.p.rapidapi.com";
    public string OrganizationsPath { get; set; } = "/search_organizations";
    public string PeoplePath { get; set; } = "/search_people";
    /// <summary>Results per call (each one is a paid credit on the listing).</summary>
    public int PerPage { get; set; } = 10;
    /// <summary>GET with query parameters (the listing's playground, 2026-10-19) or POST with a JSON body.</summary>
    public string Method { get; set; } = "GET";
    /// <summary>Lists are sent as one comma-separated value (live test 2026-10-19: the listing splits the text; <c>[]</c> lists fail).
    /// Set to "[]" to send Apollo-style repeated lists instead.</summary>
    public string ArraySuffix { get; set; } = "";
    public string PageParam { get; set; } = "page";
    public string PerPageParam { get; set; } = "per_page";
    public string KeywordParam { get; set; } = "q_organization_keyword_tags";
    /// <summary>Company location (companies search, and the company of people).</summary>
    public string LocationParam { get; set; } = "organization_locations";
    /// <summary>Where the person is (people search); empty to filter people by their company's location instead.</summary>
    public string PeopleLocationParam { get; set; } = "person_locations";
    /// <summary>Company-size filter. Empty = not sent: the listing rejected <c>organization_num_employees_ranges=11,20</c> (422) on 2026-10-19
    /// and people answers carry no head count.</summary>
    public string SizeParam { get; set; } = "";
    public string TitleParam { get; set; } = "person_titles";
}

/// <summary>Company-size bands the sales team works with.</summary>
public static class CompanySizes
{
    public static readonly IReadOnlyDictionary<string, (int Min, int Max)> Bands = new Dictionary<string, (int, int)>
    {
        ["10-20"] = (10, 20), ["20-50"] = (21, 50), ["50-100"] = (51, 100),
    };

    public static string? BandOf(int? employees) =>
        employees is not { } n ? null : Bands.FirstOrDefault(b => n >= b.Value.Min && n <= b.Value.Max).Key;
}

/// <param name="Kind">"companies" or "people".</param>
/// <param name="Sizes">Any of "10-20", "20-50", "50-100".</param>
public sealed record LeadSearchRequest(string? Keywords, IReadOnlyList<string>? Countries, IReadOnlyList<string>? Sizes,
    string? Kind = "companies", string? Titles = null, int Page = 1);

public sealed record LeadDto(string Name, string? Company, string? Title, int? Employees, string? SizeBand, string? Location,
    string? Website, string? LinkedIn, string Source);

/// <param name="Status">Ready, NotConfigured, Failed or DailyLimit.</param>
public sealed record LeadSearchResultDto(string Status, IReadOnlyList<LeadDto> Leads, int Hidden, string? Message);

/// <summary>Sends one JSON request to a RapidAPI host and returns the status code and body (null body when the call failed).</summary>
public interface ILeadSearchHttp
{
    /// <param name="pathAndQuery">Path with its query string; <paramref name="json"/> is the body for POST, null for GET.</param>
    Task<(int Status, string? Body)> SendAsync(string method, string host, string pathAndQuery, string key, string? json, CancellationToken ct);
}

public sealed class LeadSearchService(ILeadSearchHttp http, IOptions<LeadSearchOptions> options, IOptions<RapidApiBoardsOptions> rapid, ProviderBudget budget, IOptions<JsearchOptions>? jsearch = null)
{
    public const int MaxText = 200;

    public async Task<LeadSearchResultDto> SearchAsync(LeadSearchRequest request, CancellationToken ct)
    {
        var errors = Validate(request);
        if (errors.Count > 0) throw new RequestValidationException(errors);
        var key = string.IsNullOrWhiteSpace(rapid.Value.Key) ? jsearch?.Value.Key : rapid.Value.Key;
        if (string.IsNullOrWhiteSpace(key))
            return new("NotConfigured", [], 0, "Apollo search is not set up on this server yet: no RapidAPI key is set (RapidApi__Key or Jsearch__Key).");
        if (!budget.TryUse(ProviderBudget.Apollo)) return new("DailyLimit", [], 0, ProviderBudget.ExhaustedMessage);

        var o = options.Value;
        var people = string.Equals(request.Kind, "people", StringComparison.OrdinalIgnoreCase);
        var path = people ? o.PeoplePath : o.OrganizationsPath;
        var get = !string.Equals(o.Method, "POST", StringComparison.OrdinalIgnoreCase);
        var (status, body) = get
            ? await http.SendAsync("GET", o.Host, $"{path}?{BuildQuery(request, o, people)}", key, null, ct)
            : await http.SendAsync("POST", o.Host, path, key, BuildBody(request, o), ct);
        if (status is < 200 or >= 300 || body is null)
            return new("Failed", [], 0, status switch
            {
                401 or 403 => "Apollo said this server is not subscribed to the listing. Subscribe on RapidAPI with the same key.",
                429 => "Apollo is busy or the listing's quota is used up. Try again later.",
                _ => "Apollo did not answer. Try again later.",
            });
        var leads = Map(body, people);
        if (leads is null) return new("Failed", [], 0, "Apollo answered in a shape this app does not read yet.");
        // Apollo's size filter is a request hint; keep only leads whose known size fits, and leads whose size is unknown.
        var wanted = string.IsNullOrWhiteSpace(o.SizeParam) ? [] : request.Sizes?.Where(CompanySizes.Bands.ContainsKey).ToHashSet() ?? [];
        var kept = wanted.Count == 0 ? leads : leads.Where(l => l.Employees is null || (l.SizeBand is { } band && wanted.Contains(band))).ToList();
        return new("Ready", kept, leads.Count - kept.Count, null);
    }

    public static Dictionary<string, string[]> Validate(LeadSearchRequest? r)
    {
        var errors = new Dictionary<string, string[]>();
        if (r is null) { errors["body"] = ["Request body is required."]; return errors; }
        if ((r.Keywords?.Length ?? 0) > MaxText) errors["keywords"] = [$"Keep it under {MaxText} characters."];
        if ((r.Titles?.Length ?? 0) > MaxText) errors["titles"] = [$"Keep it under {MaxText} characters."];
        if (string.IsNullOrWhiteSpace(r.Keywords) && string.IsNullOrWhiteSpace(r.Titles) && (r.Countries?.Count ?? 0) == 0)
            errors["keywords"] = ["Say what to look for, or pick a country."];
        if (r.Countries is { Count: > 20 }) errors["countries"] = ["Pick at most 20 countries."];
        if (r.Sizes?.Any(s => !CompanySizes.Bands.ContainsKey(s)) == true) errors["sizes"] = ["Sizes are 10-20, 20-50 and 50-100."];
        if (r.Page is < 1 or > 50) errors["page"] = ["Page is 1 to 50."];
        return errors;
    }

    /// <summary>The request body in Apollo's own filter names (configurable): sizes become "min,max" ranges.</summary>
    public static string BuildBody(LeadSearchRequest r, LeadSearchOptions o)
    {
        var body = new JsonObject { ["page"] = r.Page, ["per_page"] = Math.Clamp(o.PerPage, 1, 25) };
        static JsonArray Arr(IEnumerable<string> v) => new(v.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray());
        if (!string.IsNullOrWhiteSpace(r.Keywords)) body[o.KeywordParam] = Arr(Split(r.Keywords));
        var countries = r.Countries?.Select(c => c.Trim()).Where(c => c.Length > 0).Distinct().Take(20).ToList() ?? [];
        if (countries.Count > 0) body[o.LocationParam] = Arr(countries);
        var sizes = r.Sizes?.Where(CompanySizes.Bands.ContainsKey).Distinct().Select(s => $"{CompanySizes.Bands[s].Min},{CompanySizes.Bands[s].Max}").ToList() ?? [];
        if (sizes.Count > 0 && !string.IsNullOrWhiteSpace(o.SizeParam)) body[o.SizeParam] = Arr(sizes);
        if (!string.IsNullOrWhiteSpace(r.Titles)) body[o.TitleParam] = Arr(Split(r.Titles));
        return body.ToJsonString();
    }

    /// <summary>The same filters as a query string: lists repeat the name with <see cref="LeadSearchOptions.ArraySuffix"/>.</summary>
    public static string BuildQuery(LeadSearchRequest r, LeadSearchOptions o, bool people)
    {
        var parts = new List<string> { $"{o.PageParam}={r.Page}", $"{o.PerPageParam}={Math.Clamp(o.PerPage, 1, 25)}" };
        void Add(string name, IEnumerable<string> values)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            var list = values.ToList();
            if (list.Count == 0) return;
            if (o.ArraySuffix.Length == 0) parts.Add($"{Uri.EscapeDataString(name)}={Uri.EscapeDataString(string.Join(',', list))}");
            else foreach (var v in list) parts.Add($"{Uri.EscapeDataString(name + o.ArraySuffix)}={Uri.EscapeDataString(v)}");
        }
        if (!string.IsNullOrWhiteSpace(r.Keywords)) Add(o.KeywordParam, Split(r.Keywords));
        var countries = r.Countries?.Select(c => c.Trim()).Where(c => c.Length > 0).Distinct().Take(20).ToList() ?? [];
        Add(people && !string.IsNullOrWhiteSpace(o.PeopleLocationParam) ? o.PeopleLocationParam : o.LocationParam, countries);
        Add(o.SizeParam, r.Sizes?.Where(CompanySizes.Bands.ContainsKey).Distinct().Select(s => $"{CompanySizes.Bands[s].Min},{CompanySizes.Bands[s].Max}") ?? []);
        if (people && !string.IsNullOrWhiteSpace(r.Titles)) Add(o.TitleParam, Split(r.Titles));
        return string.Join("&", parts);
    }

    private static IEnumerable<string> Split(string text) =>
        text.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(10);

    /// <summary>Reads companies or people from the response; looks for the list under the usual Apollo names. Null when there is none.</summary>
    public static IReadOnlyList<LeadDto>? Map(string json, bool people)
    {
        JsonElement root;
        try { root = JsonDocument.Parse(json).RootElement; } catch (JsonException) { return null; }
        var list = FindList(root, people ? ["people", "contacts", "data", "results"] : ["organizations", "accounts", "companies", "data", "results"]);
        if (list is not { } items) return null;
        var leads = new List<LeadDto>();
        foreach (var x in items.EnumerateArray())
        {
            if (x.ValueKind != JsonValueKind.Object) continue;
            var org = x.TryGetProperty("organization", out var oo) && oo.ValueKind == JsonValueKind.Object ? oo : people ? default : x;
            var name = people ? (Str(x, "name") ?? string.Join(' ', new[] { Str(x, "first_name"), Str(x, "last_name") }.Where(s => s is not null))) : Str(x, "name");
            if (string.IsNullOrWhiteSpace(name)) continue;
            var employees = Int(org, "estimated_num_employees") ?? Int(x, "estimated_num_employees");
            var location = string.Join(", ", new[] { Str(people ? x : org, "city"), Str(people ? x : org, "country") }.Where(s => s is not null));
            leads.Add(new(name!, people ? Str(org, "name") : name, people ? Str(x, "title") : null, employees, CompanySizes.BandOf(employees),
                location.Length > 0 ? location : null, Https(Str(org, "website_url")), Https(Str(x, "linkedin_url")), "Apollo"));
        }
        return leads;
    }

    private static JsonElement? FindList(JsonElement e, string[] names)
    {
        if (e.ValueKind == JsonValueKind.Array) return e;
        if (e.ValueKind != JsonValueKind.Object) return null;
        foreach (var n in names)
            if (e.TryGetProperty(n, out var v))
            {
                if (v.ValueKind == JsonValueKind.Array) return v;
                if (v.ValueKind == JsonValueKind.Object && FindList(v, names) is { } inner) return inner;
            }
        return null;
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
            ? v.GetString()!.Trim() : null;

    private static int? Int(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;

    private static string? Https(string? url)
    {
        if (url is null) return null;
        var u = url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? url : "https://" + url;
        return Uri.TryCreate(u, UriKind.Absolute, out var parsed) && (parsed.Scheme == Uri.UriSchemeHttps || parsed.Scheme == Uri.UriSchemeHttp) ? parsed.ToString() : null;
    }
}
