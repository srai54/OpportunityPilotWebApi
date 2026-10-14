using System.Globalization;
using System.Text.Json;

namespace OpportunityPilot.Application.JobBoards;

/// <summary>
/// Job boards whose postings can be shown live through JSearch. None of them offers a public search API of its own
/// (Indeed closed it; LinkedIn and SEEK are partner-only), so each posting is kept only when it carries that board's link.
/// </summary>
/// <summary>Boards read through JSearch (Google for Jobs). Naukri blocks direct server search, but its postings are listed there.</summary>
public enum JobBoard { Indeed, LinkedIn, Seek, Naukri, Instahyre }

public sealed record JobBoardSearchRequest(JobBoard Board, string Query, string? Location, bool RemoteOnly, string DatePosted, string? Country, int Page, string? Cursor = null,
    string? EmploymentType = null, string? Experience = null, int? RadiusKm = null);

public sealed record JobBoardJobDto(
    string ProviderJobId,
    string Title,
    string CompanyName,
    string? CompanyLogoUrl,
    string BoardUrl,
    string? Location,
    bool IsRemote,
    string? EmploymentType,
    decimal? SalaryMin,
    decimal? SalaryMax,
    string? SalaryCurrency,
    string? SalaryPeriod,
    DateTime? PostedAt,
    string? Snippet,
    string? Description = null);

/// <summary>Status is Ready, NotConfigured or Failed; Jobs is empty unless Ready. ObservedAt is when the provider answered.</summary>
public sealed record JobBoardSearchResult(
    JobBoard Board,
    string Status,
    IReadOnlyList<JobBoardJobDto> Jobs,
    int ProviderResults,
    string? Message,
    DateTime? ObservedAt,
    bool FromCache,
    string Source,
    /// <summary>Requests left on the provider plan this period, from RapidAPI's X-RateLimit-Requests-Remaining header.</summary>
    int? QuotaRemaining = null,
    /// <summary>Cursor for the next page of provider results (JSearch v5), or null when there are no more.</summary>
    string? NextCursor = null);

public interface IJobBoardSearch
{
    bool Configured { get; }
    Task<JobBoardSearchResult> SearchAsync(JobBoardSearchRequest request, CancellationToken ct);
}

/// <summary>
/// Reads a JSearch /search response and keeps only postings published on the chosen board (the posting's own publisher,
/// or one of its apply options) with an https link on that board's own domain. Nothing is invented or filled in.
/// </summary>
public static class JsearchBoardParser
{
    public const int MaxDescription = 8_000;

    /// <summary>JSearch <c>employment_types</c> values; one or several joined by commas.</summary>
    public static readonly string[] EmploymentTypeValues = ["FULLTIME", "PARTTIME", "CONTRACTOR", "INTERN"];

    /// <summary>JSearch <c>job_requirements</c> values.</summary>
    public static readonly string[] ExperienceValues = ["no_experience", "under_3_years_experience", "more_than_3_years_experience", "no_degree"];

    public static readonly string[] DatePostedValues = ["all", "today", "3days", "week", "month"];

    /// <summary>SEEK only runs in Australia and New Zealand; the search defaults to Australia when no country is given.</summary>
    public static readonly string[] SeekCountries = ["au", "nz"];

    private static readonly Dictionary<JobBoard, (string Publisher, string[] Domains)> Boards = new()
    {
        [JobBoard.Indeed] = ("indeed", ["indeed.com"]),
        [JobBoard.LinkedIn] = ("linkedin", ["linkedin.com"]),
        [JobBoard.Seek] = ("seek", ["seek.com.au", "seek.co.nz"]),
        [JobBoard.Naukri] = ("naukri", ["naukri.com"]),
        [JobBoard.Instahyre] = ("instahyre", ["instahyre.com"]),
    };

    public static string Name(JobBoard board) => board switch { JobBoard.LinkedIn => "LinkedIn", JobBoard.Seek => "SEEK", JobBoard.Naukri => "Naukri", JobBoard.Instahyre => "Instahyre", _ => "Indeed" };

    /// <summary>The v5 pagination cursor (<c>data.cursor</c>), or null when the provider sent none.</summary>
    public static string? NextCursor(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object
            && d.TryGetProperty("cursor", out var c) && c.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(c.GetString())
            ? c.GetString() : null;
    }

    public static (IReadOnlyList<JobBoardJobDto> Jobs, int ProviderResults) Parse(string json, JobBoard board)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data)) return ([], 0);
        // API v5 (2026) wraps the list: {"data":{"jobs":[...],"cursor":...}}; earlier versions return {"data":[...]}.
        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("jobs", out var wrapped)) data = wrapped;
        if (data.ValueKind != JsonValueKind.Array) return ([], 0);
        var jobs = new List<JobBoardJobDto>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var job in data.EnumerateArray())
        {
            var url = BoardLink(job, board);
            var id = Str(job, "job_id");
            var title = Str(job, "job_title");
            var company = Str(job, "employer_name");
            if (url is null || id is null || title is null || company is null || !seen.Add(id)) continue;
            var location = string.Join(", ", new[] { Str(job, "job_city"), Str(job, "job_state"), Str(job, "job_country") }
                .Where(x => !string.IsNullOrWhiteSpace(x)));
            var description = Str(job, "job_description");
            if (description is { Length: > MaxDescription }) description = description[..MaxDescription];
            var snippet = description is { Length: > 400 } ? description[..400].TrimEnd() + "…" : description;
            jobs.Add(new(id, title, company, Str(job, "employer_logo"), url,
                location.Length == 0 ? Str(job, "job_location") : location,
                job.TryGetProperty("job_is_remote", out var r) && r.ValueKind == JsonValueKind.True,
                Str(job, "job_employment_type"),
                Dec(job, "job_min_salary"), Dec(job, "job_max_salary"),
                Str(job, "job_salary_currency"), Str(job, "job_salary_period"),
                Date(job, "job_posted_at_datetime_utc"), snippet, description));
        }
        return (jobs, data.GetArrayLength());
    }

    /// <summary>The board's own link for a posting, or null when that board does not publish it.</summary>
    public static string? BoardLink(JsonElement job, JobBoard board)
    {
        var (publisher, domains) = Boards[board];
        if (Matches(Str(job, "job_publisher"), publisher) && OnDomain(Str(job, "job_apply_link"), domains)) return Str(job, "job_apply_link");
        if (job.TryGetProperty("apply_options", out var options) && options.ValueKind == JsonValueKind.Array)
            foreach (var option in options.EnumerateArray())
            {
                var link = Str(option, "apply_link");
                if (Matches(Str(option, "publisher"), publisher) && OnDomain(link, domains)) return link;
            }
        return null;
    }

    private static bool Matches(string? publisher, string name) =>
        publisher is not null && publisher.Contains(name, StringComparison.OrdinalIgnoreCase);

    private static bool OnDomain(string? url, string[] domains) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps
        && domains.Any(d => u.Host.Equals(d, StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith("." + d, StringComparison.OrdinalIgnoreCase));

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
            ? v.GetString()!.Trim() : null;

    private static decimal? Dec(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d) ? d : null;

    private static DateTime? Date(JsonElement e, string name) =>
        DateTime.TryParse(Str(e, name), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d)
            ? d : null;
}
