using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using OpportunityPilot.Application.Integrations;
using OpportunityPilot.Application.Research;

namespace OpportunityPilot.Application.JobBoards;

/// <summary>
/// Keys for the official job-search APIs that are free but need a key (section <c>JobFeeds</c>, Render env <c>JobFeeds__*</c>).
/// A source without its key is reported NotConfigured and never called. Keys never appear in logs, messages or cache keys.
/// </summary>
public sealed class JobFeedOptions
{
    public const string Section = "JobFeeds";
    /// <summary>jooble.org/api/about → free key.</summary>
    public string? JoobleKey { get; set; }
    /// <summary>Careerjet partner id (affid) from careerjet.com/partners.</summary>
    public string? CareerjetAffid { get; set; }
    /// <summary>The visitor address Careerjet asks for; the server sends this fixed value (no visitor data leaves the server).</summary>
    public string CareerjetUserIp { get; set; } = "1.1.1.1";
    /// <summary>findwork.dev/developers → API token.</summary>
    public string? FindworkToken { get; set; }
    /// <summary>developer.usajobs.gov → Authorization-Key, and the e-mail it was registered with (sent as User-Agent, as USAJOBS requires).</summary>
    public string? UsaJobsKey { get; set; }
    public string? UsaJobsEmail { get; set; }
    /// <summary>reed.co.uk/developers → API key (UK, many contract roles).</summary>
    public string? ReedKey { get; set; }
    /// <summary>The Bundesagentur für Arbeit Jobsuche API's public client id (documented at jobsuche.api.bund.dev); overridable.</summary>
    public string ArbeitsagenturClientId { get; set; } = "jobboerse-jobsuche";

    /// <summary>Search the Bundesagentur feed (JobFeeds__ArbeitsagenturEnabled=true). Off by default since its API refused requests on 2026-10-26.</summary>
    public bool ArbeitsagenturEnabled { get; set; }

    public bool Has(FeedSite site) => site switch
    {
        FeedSite.Jooble => !string.IsNullOrWhiteSpace(JoobleKey),
        FeedSite.Careerjet => !string.IsNullOrWhiteSpace(CareerjetAffid),
        FeedSite.Findwork => !string.IsNullOrWhiteSpace(FindworkToken),
        FeedSite.UsaJobs => !string.IsNullOrWhiteSpace(UsaJobsKey) && !string.IsNullOrWhiteSpace(UsaJobsEmail),
        FeedSite.Reed => !string.IsNullOrWhiteSpace(ReedKey),
        // Off unless switched on: its public API answered 403 on 2026-10-26, and a failing source would show on every search.
        FeedSite.Arbeitsagentur => ArbeitsagenturEnabled,
        _ => true,
    };
}

/// <summary>
/// The official search APIs added on 2026-10-19: Jooble, Careerjet, Findwork and USAJOBS (free with a key) and Freelancer.com's public
/// projects API (no key; freelance marketplace, so its items are contract projects, not jobs). Each is searched with the person's words,
/// answers are kept 30 minutes per search, and every item links to the original posting. Read-only.
/// </summary>
public sealed partial class KeyedFeeds(IWebFetcher fetcher, IConnectorHttp http, IOptions<JobFeedOptions> options, TimeProvider clock)
{
    public static readonly FeedSite[] Sites = [FeedSite.Jooble, FeedSite.Careerjet, FeedSite.Findwork, FeedSite.UsaJobs, FeedSite.Freelancer, FeedSite.Reed, FeedSite.Arbeitsagentur];
    private const int MaxCached = 200;
    private static readonly ConcurrentDictionary<string, (DateTime At, List<JobBoardJobDto> Jobs)> Cache = new(StringComparer.Ordinal);
    private JobFeedOptions O => options.Value;

    public bool Configured(FeedSite site) => O.Has(site);

    public async Task<(List<JobBoardJobDto> Jobs, string Status, string? Message, bool FromCache)> SearchAsync(FeedSite site, string query, string? location, CancellationToken ct)
    {
        if (!O.Has(site)) return ([], "NotConfigured", $"{FeedSearchService.Name(site)} is not configured on the server.", false);
        var now = clock.GetUtcNow().UtcDateTime;
        var key = $"{site}\n{query.Trim().ToLowerInvariant()}\n{location?.Trim().ToLowerInvariant()}";
        if (Cache.TryGetValue(key, out var hit) && now - hit.At < FeedSearchService.CacheFor) return (hit.Jobs, "Ready", null, true);
        string? text; string? failure;
        var q = Uri.EscapeDataString(query.Trim());
        var where = string.IsNullOrWhiteSpace(location) ? "" : Uri.EscapeDataString(location.Trim());
        switch (site)
        {
            case FeedSite.Jooble:
            {
                var r = await fetcher.PostJsonAsync($"https://jooble.org/api/{Uri.EscapeDataString(O.JoobleKey!.Trim())}", JsonSerializer.Serialize(new { keywords = query.Trim(), location = location?.Trim() ?? "" }), ct);
                (text, failure) = (r.Ok ? r.Content : null, r.Ok ? null : r.StatusCode is 401 or 403 ? "Jooble rejected the server's key." : "Jooble could not be read.");
                break;
            }
            case FeedSite.Careerjet:
            {
                var r = await fetcher.FetchJsonAsync($"https://public.api.careerjet.net/search?keywords={q}&location={where}&affid={Uri.EscapeDataString(O.CareerjetAffid!.Trim())}&user_ip={Uri.EscapeDataString(O.CareerjetUserIp)}&user_agent=OpportunityPilot&locale_code=en_GB&pagesize=50&sort=date", ct);
                (text, failure) = (r.Ok ? r.Content : null, r.Ok ? null : "Careerjet could not be read.");
                break;
            }
            case FeedSite.Findwork:
            {
                var r = await http.SendAsync(new ConnectorRequest(HttpMethod.Get, $"https://findwork.dev/api/jobs/?search={q}{(where.Length > 0 ? "&location=" + where : "")}&order_by=-date",
                    Headers: new Dictionary<string, string> { ["Authorization"] = $"Token {O.FindworkToken!.Trim()}" }), ct);
                (text, failure) = (r.Ok ? r.Body : null, r.Ok ? null : r.Status is 401 or 403 ? "Findwork rejected the server's token." : "Findwork could not be read.");
                break;
            }
            case FeedSite.UsaJobs:
            {
                var r = await http.SendAsync(new ConnectorRequest(HttpMethod.Get, $"https://data.usajobs.gov/api/search?Keyword={q}{(where.Length > 0 ? "&LocationName=" + where : "")}&ResultsPerPage=50",
                    Headers: new Dictionary<string, string> { ["Authorization-Key"] = O.UsaJobsKey!.Trim(), ["User-Agent"] = O.UsaJobsEmail!.Trim() }), ct);
                (text, failure) = (r.Ok ? r.Body : null, r.Ok ? null : r.Status is 401 ? "USAJOBS rejected the server's key." : "USAJOBS could not be read.");
                break;
            }
            case FeedSite.Reed:
            {
                var basic = Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes(O.ReedKey!.Trim() + ":"));
                var r = await http.SendAsync(new ConnectorRequest(HttpMethod.Get, $"https://www.reed.co.uk/api/1.0/search?keywords={q}{(where.Length > 0 ? "&locationName=" + where : "")}&resultsToTake=50",
                    Headers: new Dictionary<string, string> { ["Authorization"] = $"Basic {basic}" }), ct);
                (text, failure) = (r.Ok ? r.Body : null, r.Ok ? null : r.Status is 401 or 403 ? "Reed rejected the server's key." : "Reed could not be read.");
                break;
            }
            case FeedSite.Arbeitsagentur:
            {
                var r = await http.SendAsync(new ConnectorRequest(HttpMethod.Get, $"https://rest.arbeitsagentur.de/jobboerse/jobsuche-service/pc/v4/jobs?was={q}{(where.Length > 0 ? "&wo=" + where : "")}&size=50",
                    Headers: new Dictionary<string, string> { ["X-API-Key"] = O.ArbeitsagenturClientId }), ct);
                (text, failure) = (r.Ok ? r.Body : null, r.Ok ? null : "Bundesagentur für Arbeit could not be read.");
                break;
            }
            default:
            {
                var r = await fetcher.FetchJsonAsync($"https://www.freelancer.com/api/projects/0.1/projects/active/?query={q}&limit=50&full_description=true&job_details=true", ct, ResearchOptions.LargeFeedBytes);
                (text, failure) = (r.Ok ? r.Content : null, r.Ok ? null : "Freelancer.com could not be read.");
                break;
            }
        }
        if (text is null) return ([], "Failed", failure, false);
        var jobs = site switch
        {
            FeedSite.Jooble => Jooble(text),
            FeedSite.Careerjet => Careerjet(text),
            FeedSite.Findwork => Findwork(text),
            FeedSite.UsaJobs => UsaJobs(text),
            FeedSite.Reed => Reed(text),
            FeedSite.Arbeitsagentur => Arbeitsagentur(text),
            _ => Freelancer(text),
        };
        if (jobs is null) return ([], "Failed", $"{FeedSearchService.Name(site)} sent a response that could not be read.", false);
        if (Cache.Count >= MaxCached)
            foreach (var old in Cache.OrderBy(e => e.Value.At).Take(Cache.Count - MaxCached + 1).Select(e => e.Key).ToList()) Cache.TryRemove(old, out _);
        Cache[key] = (now, jobs);
        return (jobs, "Ready", null, false);
    }

    // ---------- Mapping (pure, tested) ----------

    /// <summary><c>{ jobs: [ { id, title, location, snippet, salary, type, link, company, updated } ] }</c></summary>
    public static List<JobBoardJobDto>? Jooble(string json) => Map(json, root => Arr(root, "jobs"), j =>
        Job("jooble", Str(j, "id") ?? Str(j, "link"), Str(j, "title"), Str(j, "company"), Str(j, "link"), Str(j, "location"), Str(j, "type"), Date(Str(j, "updated")), Str(j, "snippet")));

    /// <summary><c>{ type: "JOBS", jobs: [ { title, company, locations, description, url, date, salary } ] }</c></summary>
    public static List<JobBoardJobDto>? Careerjet(string json) => Map(json, root => Arr(root, "jobs"), j =>
        Job("careerjet", Str(j, "url"), Str(j, "title"), Str(j, "company"), Str(j, "url"), Str(j, "locations"), null, Date(Str(j, "date")), Str(j, "description")));

    /// <summary><c>{ results: [ { id, role, company_name, employment_type, location, remote, url, text, date_posted } ] }</c></summary>
    public static List<JobBoardJobDto>? Findwork(string json) => Map(json, root => Arr(root, "results"), j =>
        Job("findwork", Str(j, "id"), Str(j, "role"), Str(j, "company_name"), Str(j, "url"), Str(j, "location"), Str(j, "employment_type"), Date(Str(j, "date_posted")), Str(j, "text"),
            j.TryGetProperty("remote", out var r) && r.ValueKind == JsonValueKind.True));

    /// <summary><c>{ SearchResult: { SearchResultItems: [ { MatchedObjectId, MatchedObjectDescriptor: { PositionTitle, OrganizationName, PositionLocationDisplay, PositionURI, PublicationStartDate, PositionSchedule[].Name, UserArea.Details.JobSummary } } ] } }</c></summary>
    public static List<JobBoardJobDto>? UsaJobs(string json) => Map(json,
        root => root.TryGetProperty("SearchResult", out var sr) ? Arr(sr, "SearchResultItems") : null,
        item =>
        {
            if (!item.TryGetProperty("MatchedObjectDescriptor", out var d)) return null;
            var schedule = d.TryGetProperty("PositionSchedule", out var ps) && ps.ValueKind == JsonValueKind.Array && ps.GetArrayLength() > 0 ? Str(ps[0], "Name") : null;
            var summary = d.TryGetProperty("UserArea", out var ua) && ua.TryGetProperty("Details", out var det) ? Str(det, "JobSummary") : null;
            return Job("usajobs", Str(item, "MatchedObjectId"), Str(d, "PositionTitle"), Str(d, "OrganizationName"), Str(d, "PositionURI"), Str(d, "PositionLocationDisplay"), schedule,
                Date(Str(d, "PublicationStartDate")), summary);
        });

    /// <summary>
    /// <c>{ result: { projects: [ { id, title, seo_url, preview_description, type, budget: { minimum, maximum }, currency: { code }, time_submitted } ] } }</c>.
    /// Freelance projects: contract work by definition, remote by default. The API does not name the client, so the company is left empty (Sales groups by company and leaves these out; Candidate lists them as projects).
    /// </summary>
    public static List<JobBoardJobDto>? Freelancer(string json) => Map(json,
        root => root.TryGetProperty("result", out var res) ? Arr(res, "projects") : null,
        p =>
        {
            var seo = Str(p, "seo_url");
            var url = seo is null ? null : $"https://www.freelancer.com/projects/{seo}";
            decimal? min = p.TryGetProperty("budget", out var b) && b.TryGetProperty("minimum", out var mn) && mn.ValueKind == JsonValueKind.Number ? mn.GetDecimal() : null;
            decimal? max = p.TryGetProperty("budget", out var b2) && b2.TryGetProperty("maximum", out var mx) && mx.ValueKind == JsonValueKind.Number ? mx.GetDecimal() : null;
            var currency = p.TryGetProperty("currency", out var c) ? Str(c, "code") : null;
            DateTime? posted = p.TryGetProperty("time_submitted", out var t) && t.ValueKind == JsonValueKind.Number && t.TryGetInt64(out var s) && s > 0
                ? DateTimeOffset.FromUnixTimeSeconds(s).UtcDateTime : null;
            var kind = Str(p, "type") == "hourly" ? "per hour" : "fixed price";
            var job = Job("freelancer", Str(p, "id"), Str(p, "title"), "", url, "Remote", "CONTRACTOR", posted, Str(p, "preview_description") ?? Str(p, "description"), true);
            return job is null ? null : job with { SalaryMin = min, SalaryMax = max, SalaryCurrency = currency, SalaryPeriod = kind };
        });

    /// <summary><c>{ results: [ { jobId, employerName, jobTitle, locationName, minimumSalary, maximumSalary, currency, date (dd/MM/yyyy), jobDescription, jobUrl } ] }</c></summary>
    public static List<JobBoardJobDto>? Reed(string json) => Map(json, root => Arr(root, "results"), j =>
    {
        DateTime? posted = DateTime.TryParseExact(Str(j, "date"), "dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var d) ? d : null;
        var job = Job("reed", Str(j, "jobId"), Str(j, "jobTitle"), Str(j, "employerName"), Str(j, "jobUrl"), Str(j, "locationName"), null, posted, Str(j, "jobDescription"));
        decimal? Num(string n) => j.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : null;
        return job is null ? null : job with { SalaryMin = Num("minimumSalary"), SalaryMax = Num("maximumSalary"), SalaryCurrency = Str(j, "currency") };
    });

    /// <summary><c>{ stellenangebote: [ { refnr, titel, arbeitgeber, arbeitsort: { ort, land }, aktuelleVeroeffentlichungsdatum, externeUrl } ] }</c>; links to the official job page.</summary>
    public static List<JobBoardJobDto>? Arbeitsagentur(string json) => Map(json, root => Arr(root, "stellenangebote"), j =>
    {
        var refnr = Str(j, "refnr");
        if (refnr is null) return null;
        var place = j.TryGetProperty("arbeitsort", out var a) ? string.Join(", ", new[] { Str(a, "ort"), Str(a, "land") }.Where(x => x is not null)) : null;
        return Job("arbeitsagentur", refnr, Str(j, "titel") ?? Str(j, "beruf"), Str(j, "arbeitgeber"), $"https://www.arbeitsagentur.de/jobsuche/jobdetail/{Uri.EscapeDataString(refnr)}",
            string.IsNullOrEmpty(place) ? null : place, null, Date(Str(j, "aktuelleVeroeffentlichungsdatum")), Str(j, "beruf"));
    });

    private static List<JobBoardJobDto>? Map(string json, Func<JsonElement, JsonElement?> list, Func<JsonElement, JobBoardJobDto?> one)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || list(doc.RootElement) is not { ValueKind: JsonValueKind.Array } rows) return null;
            return rows.EnumerateArray().Where(r => r.ValueKind == JsonValueKind.Object).Select(one).Where(j => j is not null).Select(j => j!).ToList();
        }
        catch (JsonException) { return null; }
    }

    private static JsonElement? Arr(JsonElement e, string name) => e.TryGetProperty(name, out var v) ? v : null;

    private static string? Str(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
        ? v.ValueKind switch { JsonValueKind.String => string.IsNullOrWhiteSpace(v.GetString()) ? null : v.GetString()!.Trim(), JsonValueKind.Number => v.GetRawText(), _ => null }
        : null;

    private static DateTime? Date(string? s) =>
        DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    private static JobBoardJobDto? Job(string prefix, string? id, string? title, string? company, string? url, string? location, string? type, DateTime? posted, string? html, bool remoteFlag = false)
    {
        if (title is null || url is null || !Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp)) return null;
        var text = html is null ? null : WebUtility.HtmlDecode(Tags().Replace(html, " ")).Trim();
        text = text is null ? null : Spaces().Replace(text, " ");
        var snippet = text is null ? null : text.Length <= 240 ? text : text[..240].TrimEnd() + "…";
        var remote = remoteFlag || (location ?? "").Contains("remote", StringComparison.OrdinalIgnoreCase) || (title).Contains("remote", StringComparison.OrdinalIgnoreCase);
        return new JobBoardJobDto($"{prefix}:{id ?? url}", WebUtility.HtmlDecode(title), WebUtility.HtmlDecode(company ?? ""), null, url, location, remote, type, null, null, null, null, posted, snippet, text);
    }

    [GeneratedRegex("<[^>]+>")] private static partial Regex Tags();
    [GeneratedRegex(@"[ \t]+")] private static partial Regex Spaces();
}
