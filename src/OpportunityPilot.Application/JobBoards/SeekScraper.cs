using System.Globalization;
using System.Text.Json;

namespace OpportunityPilot.Application.JobBoards;

/// <summary>
/// SEEK read directly through the "Seek Jobs Scraper API" RapidAPI listing (ToolzerHub): GET <c>/v1/seek/search?query=&amp;location=&amp;page=</c>,
/// one credit a call, about 32 postings a page from seek.com.au itself. Uses the server's RapidAPI key (<c>Jsearch__Key</c>); when that
/// key is not subscribed to the listing, the SEEK tab falls back to JSearch as before.
/// </summary>
public sealed class SeekOptions
{
    public const string Section = "Seek";
    public bool Enabled { get; set; } = true;
    public string Host { get; set; } = "seek-jobs-scraper-api.p.rapidapi.com";
    public string Path { get; set; } = "/v1/seek/search";
    /// <summary>Same search served from memory for this long, so a repeat costs nothing.</summary>
    public int CacheMinutes { get; set; } = 360;
}

public static class SeekScraper
{
    /// <summary>The search URL path and query for one page.</summary>
    public static string PathAndQuery(SeekOptions o, string query, string? location, int page)
    {
        var parts = new List<string> { $"query={Uri.EscapeDataString(query.Trim())}" };
        if (!string.IsNullOrWhiteSpace(location)) parts.Add($"location={Uri.EscapeDataString(location.Trim())}");
        if (page > 1) parts.Add($"page={page.ToString(CultureInfo.InvariantCulture)}");
        return $"{o.Path}?{string.Join("&", parts)}";
    }

    /// <summary>SEEK's work types in the app's employment codes (Contract/Temp → CONTRACTOR, so "Contract only" and the tag work).</summary>
    public static string? EmploymentCode(string? workType) => workType?.Trim().ToLowerInvariant() switch
    {
        null or "" => null,
        var w when w.Contains("contract") || w.Contains("temp") => "CONTRACTOR",
        var w when w.Contains("full") => "FULLTIME",
        var w when w.Contains("part") || w.Contains("casual") => "PARTTIME",
        var w when w.Contains("intern") => "INTERN",
        _ => workType.Trim(),
    };

    /// <summary>Reads <c>data.results[]</c>. Null when the answer is not that shape; postings without an id or title are skipped.</summary>
    public static IReadOnlyList<JobBoardJobDto>? Parse(string json)
    {
        JsonElement root;
        try { root = JsonDocument.Parse(json).RootElement; } catch (JsonException) { return null; }
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) return null;
        var jobs = new List<JobBoardJobDto>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in results.EnumerateArray())
        {
            var id = Str(r, "job_id");
            var title = Str(r, "title");
            // SEEK repeats sponsored listings within and across pages (seen live 2026-10-19): one row per job id.
            if (id is null || title is null || !id.All(char.IsAsciiLetterOrDigit) || !seen.Add(id)) continue;
            var company = r.TryGetProperty("advertiser", out var adv) ? Str(adv, "name") : null;
            string? location = null;
            if (r.TryGetProperty("locations", out var locs) && locs.ValueKind == JsonValueKind.Array && locs.GetArrayLength() > 0) location = Str(locs[0], "label");
            var workType = r.TryGetProperty("work_types", out var wt) && wt.ValueKind == JsonValueKind.Array && wt.GetArrayLength() > 0 && wt[0].ValueKind == JsonValueKind.String ? wt[0].GetString() : null;
            var arrangement = Str(r, "work_arrangement");
            var remote = arrangement is not null && (arrangement.Contains("remote", StringComparison.OrdinalIgnoreCase) || arrangement.Contains("home", StringComparison.OrdinalIgnoreCase));
            DateTime? posted = DateTime.TryParse(Str(r, "listed_at"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var p) ? p : null;
            var snippet = string.Join(" · ", new[] { Str(r, "salary_label"), arrangement, Str(r, "teaser") }.Where(s => s is not null));
            var bullets = r.TryGetProperty("bullet_points", out var bp) && bp.ValueKind == JsonValueKind.Array
                ? string.Join("\n", bp.EnumerateArray().Where(b => b.ValueKind == JsonValueKind.String).Select(b => b.GetString())) : null;
            jobs.Add(new(id, title, company ?? "", Https(Str(r, "logo_url")), $"https://www.seek.com.au/job/{id}", location, remote,
                EmploymentCode(workType), null, null, null, null, posted, snippet.Length > 0 ? snippet : null,
                string.IsNullOrWhiteSpace(bullets) ? null : bullets));
        }
        return jobs;
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString()!.Trim() : null;

    private static string? Https(string? url) => url is not null && url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? url : null;
}
