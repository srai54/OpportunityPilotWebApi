using System.Text.Json;

namespace OpportunityPilot.Application.JobBoards;

/// <summary>
/// One regional job board read through a RapidAPI listing (for example Bayt, Naukri Gulf, JobStreet, StepStone). Nothing about
/// a listing's response is assumed: the owner names the endpoint and which response fields hold the title, company and so on,
/// in configuration (<c>RapidApi__Boards__bayt__Host</c> and so on). The key (<c>RapidApi__Key</c>) lives only in server settings.
/// </summary>
public sealed class RapidApiBoardConfig
{
    public string Name { get; set; } = "";
    /// <summary>The listing host, e.g. <c>bayt-jobs.p.rapidapi.com</c>. Must end in <c>.p.rapidapi.com</c>.</summary>
    public string Host { get; set; } = "";
    public string Path { get; set; } = "";
    public string QueryParam { get; set; } = "query";
    public string? LocationParam { get; set; }
    /// <summary>Fixed extra query values, e.g. <c>country=ae</c>.</summary>
    public Dictionary<string, string> Extra { get; set; } = [];
    /// <summary>Dotted path to the array of jobs in the response ("data", "result.jobs"); empty when the response is the array.</summary>
    public string ListPath { get; set; } = "";
    public string Id { get; set; } = "id";
    public string Title { get; set; } = "title";
    public string Company { get; set; } = "company";
    public string? Location { get; set; } = "location";
    public string Url { get; set; } = "url";
    public string? Description { get; set; }
    public string? Posted { get; set; }
    public string? Remote { get; set; }

    public bool Valid => Name.Length > 0 && Host.EndsWith(".p.rapidapi.com", StringComparison.OrdinalIgnoreCase) && !Host.Contains('/')
        && Path.Length > 0 && Title.Length > 0 && Url.Length > 0;
}

public sealed class RapidApiBoardsOptions
{
    public const string Section = "RapidApi";
    public string? Key { get; set; }
    public int CacheMinutes { get; set; } = 360;
    public Dictionary<string, RapidApiBoardConfig> Boards { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public bool Configured => !string.IsNullOrWhiteSpace(Key) && Boards.Values.Any(b => b.Valid);
    public IEnumerable<KeyValuePair<string, RapidApiBoardConfig>> Active => string.IsNullOrWhiteSpace(Key) ? [] : Boards.Where(b => b.Value.Valid);
}

public sealed record RapidApiBoardResult(string BoardId, string Name, string Status, IReadOnlyList<JobBoardJobDto> Jobs, string? Message, bool FromCache);

public interface IRapidApiBoardSearch
{
    IReadOnlyList<(string Id, string Name)> Boards { get; }
    Task<RapidApiBoardResult> SearchAsync(string boardId, string query, string? location, CancellationToken ct);
}

/// <summary>Turns a RapidAPI response into postings using the owner's field map. Missing fields stay missing; a job needs a title and an https link.</summary>
public static class RapidApiMapping
{
    public static IReadOnlyList<JobBoardJobDto>? Map(string? json, string boardId, RapidApiBoardConfig c)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        JsonDocument document;
        try { document = JsonDocument.Parse(json); } catch (JsonException) { return null; }
        using (document)
        {
            var list = Walk(document.RootElement, c.ListPath);
            if (list is not { ValueKind: JsonValueKind.Array } rows) return null;
            var jobs = new List<JobBoardJobDto>();
            foreach (var row in rows.EnumerateArray())
            {
                var title = Text(row, c.Title);
                var url = Text(row, c.Url);
                if (title is null || url is null || !Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps) continue;
                var id = Text(row, c.Id) ?? url;
                var description = c.Description is null ? null : Text(row, c.Description);
                var snippet = description is null ? null : description.Length <= 240 ? description : description[..240].TrimEnd() + "…";
                var location = c.Location is null ? null : Text(row, c.Location);
                var remote = (c.Remote is not null && Walk(row, c.Remote) is { } r && (r.ValueKind == JsonValueKind.True ||
                    (r.ValueKind == JsonValueKind.String && r.GetString()!.Contains("remote", StringComparison.OrdinalIgnoreCase))))
                    || (location ?? string.Empty).Contains("remote", StringComparison.OrdinalIgnoreCase);
                DateTime? posted = c.Posted is not null && Text(row, c.Posted) is { } p && DateTimeOffset.TryParse(p, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal, out var at) ? at.UtcDateTime : null;
                jobs.Add(new JobBoardJobDto($"{boardId}:{id}", title, Text(row, c.Company) ?? string.Empty, null, url, location, remote, null, null, null, null, null, posted, snippet, description));
            }
            return jobs;
        }
    }

    /// <summary>Follows a dotted path ("data.jobs"); an empty path is the element itself.</summary>
    public static JsonElement? Walk(JsonElement element, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return element;
        var current = element;
        foreach (var part in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(part, out var next)) return null;
            current = next;
        }
        return current;
    }

    private static string? Text(JsonElement row, string path)
    {
        if (Walk(row, path) is not { } v) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => string.IsNullOrWhiteSpace(v.GetString()) ? null : v.GetString()!.Trim(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null
        };
    }
}
