using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpportunityPilot.Application.JobBoards;

/// <summary>
/// Naukri through an Apify scraper actor (Naukri has no public API and blocks direct server search with a CAPTCHA). On when
/// <c>Apify__Token</c> is set; the token travels only in a request header. Free Apify credit covers tens of thousands of jobs a month.
/// </summary>
public sealed class ApifyOptions
{
    public const string Section = "Apify";
    public string? Token { get; set; }
    /// <summary>The actor that reads a Naukri search page (curious_coder/naukri-job-scraper, about $0.2 per 1,000 jobs).</summary>
    public string NaukriActor { get; set; } = "curious_coder~naukri-job-scraper";
    public int MaxJobs { get; set; } = 40;
    /// <summary>Same search served from memory for this long, so repeat searches cost nothing.</summary>
    public int CacheMinutes { get; set; } = 360;
    public bool Configured => !string.IsNullOrWhiteSpace(Token);
}

public static partial class NaukriApify
{
    [GeneratedRegex("[^a-z0-9]+")] private static partial Regex NonSlug();
    [GeneratedRegex(@"(\d{10,14})(?:\D*)$")] private static partial Regex TrailingId();

    private static string Slug(string s) => NonSlug().Replace(s.Trim().ToLowerInvariant(), "-").Trim('-');

    /// <summary>Naukri's own search page for the words and place, e.g. https://www.naukri.com/data-scientist-jobs-in-bengaluru?k=data%20scientist&amp;l=bengaluru</summary>
    public static string SearchUrl(string query, string? location)
    {
        var q = Slug(query);
        var path = (q.Length == 0 ? "jobs" : $"{q}-jobs") + (string.IsNullOrWhiteSpace(location) ? "" : $"-in-{Slug(location)}");
        var qs = $"k={Uri.EscapeDataString(query.Trim())}" + (string.IsNullOrWhiteSpace(location) ? "" : $"&l={Uri.EscapeDataString(location.Trim())}");
        return $"https://www.naukri.com/{path}?{qs}";
    }

    public static string Input(string searchUrl, int maxJobs) => JsonSerializer.Serialize(new
    {
        startUrls = new[] { new { url = searchUrl } },
        maxPagesPerUrl = Math.Max(1, (maxJobs + 19) / 20),
        maxJobsPerUrl = maxJobs,
    });

    /// <summary>The actor's dataset items: title, company, location, experience, salary, skills[], description, postedLabel, url.</summary>
    public static IReadOnlyList<JobBoardJobDto> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];
        var jobs = new List<JobBoardJobDto>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in doc.RootElement.EnumerateArray())
        {
            var url = Str(row, "url") ?? Str(row, "jdURL") ?? Str(row, "job_url");
            var title = Str(row, "title");
            var company = Str(row, "company") ?? Str(row, "companyName") ?? Str(row, "company_name");
            if (url is null || title is null || company is null || !Uri.TryCreate(url, UriKind.Absolute, out var u) || !u.Host.EndsWith("naukri.com", StringComparison.OrdinalIgnoreCase)) continue;
            var id = TrailingId().Match(u.AbsolutePath) is { Success: true } m ? m.Groups[1].Value : url;
            if (!seen.Add(id)) continue;
            var location = Str(row, "location");
            var skills = row.TryGetProperty("skills", out var s) && s.ValueKind == JsonValueKind.Array
                ? string.Join(", ", s.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString())) : null;
            var lines = new[]
            {
                Str(row, "experience") is { } exp ? $"Experience: {exp}" : null,
                Str(row, "salary") is { } pay && !pay.Contains("not disclosed", StringComparison.OrdinalIgnoreCase) ? $"Salary: {pay}" : null,
                string.IsNullOrEmpty(skills) ? null : $"Skills: {skills}",
                Str(row, "description"),
            }.Where(x => !string.IsNullOrWhiteSpace(x));
            var description = string.Join("\n", lines);
            var remote = location?.Contains("remote", StringComparison.OrdinalIgnoreCase) == true || location?.Contains("work from home", StringComparison.OrdinalIgnoreCase) == true;
            jobs.Add(new JobBoardJobDto(id, title, company, null, url, location, remote, null, null, null, null, null,
                Posted(Str(row, "createdDate")), description.Length > 400 ? description[..400].TrimEnd() + "…" : description, description));
        }
        return jobs;
    }

    private static DateTime? Posted(string? value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString()!.Trim() : null;
}
