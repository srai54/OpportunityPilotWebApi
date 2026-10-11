using System.Globalization;
using System.Text.Json;

namespace OpportunityPilot.Application.Trends;

/// <summary>Reads a JSearch response (v5 <c>data.jobs</c> or the older <c>data</c> array) into trend postings from every site.</summary>
public static class JsearchTrendParser
{
    public static (IReadOnlyList<TrendPosting> Postings, string? NextCursor) Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data)) return ([], null);
        string? cursor = null;
        if (data.ValueKind == JsonValueKind.Object)
        {
            if (data.TryGetProperty("cursor", out var c) && c.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(c.GetString())) cursor = c.GetString();
            if (!data.TryGetProperty("jobs", out data)) return ([], cursor);
        }
        if (data.ValueKind != JsonValueKind.Array) return ([], cursor);
        var list = new List<TrendPosting>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var job in data.EnumerateArray())
        {
            var id = Str(job, "job_id");
            var title = Str(job, "job_title");
            var company = Str(job, "employer_name");
            if (id is null || title is null || company is null || !seen.Add(id)) continue;
            var text = title + "\n" + Str(job, "job_description");
            list.Add(new(id, title, company, Str(job, "job_publisher"), Str(job, "job_city"),
                job.TryGetProperty("job_is_remote", out var r) && r.ValueKind == JsonValueKind.True, Str(job, "job_employment_type"),
                Dec(job, "job_min_salary"), Dec(job, "job_max_salary"), Str(job, "job_salary_period"), Date(job, "job_posted_at_datetime_utc"),
                SkillCatalog.Find(text)));
        }
        return (list, cursor);
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString()!.Trim() : null;

    private static decimal? Dec(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d) ? d : null;

    private static DateTime? Date(JsonElement e, string name) =>
        DateTime.TryParse(Str(e, name), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d : null;
}
