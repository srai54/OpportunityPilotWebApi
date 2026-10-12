using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpportunityPilot.Application.JobBoards;

namespace OpportunityPilot.Infrastructure.Research;

/// <summary>
/// Reads the regional boards the owner configured (RapidAPI listings). Only hosts ending in <c>.p.rapidapi.com</c> are ever called,
/// the key travels only in a request header and is never logged, and identical searches are served from memory for a few hours.
/// </summary>
public sealed class RapidApiBoardClient(HttpClient http, IOptions<RapidApiBoardsOptions> options, TimeProvider clock, ILogger<RapidApiBoardClient> logger) : IRapidApiBoardSearch
{
    private static readonly ConcurrentDictionary<string, (DateTime At, RapidApiBoardResult Result)> Cache = new();

    public IReadOnlyList<(string Id, string Name)> Boards => options.Value.Active.Select(b => (b.Key.ToLowerInvariant(), b.Value.Name)).ToList();

    public async Task<RapidApiBoardResult> SearchAsync(string boardId, string query, string? location, CancellationToken ct)
    {
        var o = options.Value;
        var entry = o.Active.FirstOrDefault(b => string.Equals(b.Key, boardId, StringComparison.OrdinalIgnoreCase));
        if (entry.Value is null) return new(boardId, boardId, "NotConfigured", [], "This board is not set up on the server.", false);
        var c = entry.Value;
        var id = entry.Key.ToLowerInvariant();
        var qs = new List<string> { $"{c.QueryParam}={Uri.EscapeDataString(query)}" };
        if (!string.IsNullOrWhiteSpace(location) && !string.IsNullOrWhiteSpace(c.LocationParam)) qs.Add($"{c.LocationParam}={Uri.EscapeDataString(location)}");
        foreach (var (k, v) in c.Extra) qs.Add($"{Uri.EscapeDataString(k)}={Uri.EscapeDataString(v)}");
        var url = $"https://{c.Host}/{c.Path.Trim('/')}?{string.Join("&", qs)}";
        var now = clock.GetUtcNow().UtcDateTime;
        if (Cache.TryGetValue(url, out var hit) && now - hit.At < TimeSpan.FromMinutes(o.CacheMinutes)) return hit.Result with { FromCache = true };
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Get, url);
            message.Headers.Add("X-RapidAPI-Key", o.Key);
            message.Headers.Add("X-RapidAPI-Host", c.Host);
            using var response = await http.SendAsync(message, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("RapidAPI board {Board} answered HTTP {Status}", id, (int)response.StatusCode);
                var why = response.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized ? "The key is not subscribed to this API."
                    : response.StatusCode == System.Net.HttpStatusCode.TooManyRequests ? "The monthly quota for this API is used up."
                    : $"{c.Name} answered HTTP {(int)response.StatusCode}.";
                return new(id, c.Name, "Failed", [], why, false);
            }
            var json = await response.Content.ReadAsStringAsync(ct);
            var jobs = RapidApiMapping.Map(json, id, c);
            if (jobs is null) return new(id, c.Name, "Failed", [], $"{c.Name} sent a response that does not match the configured field map.", false);
            var result = new RapidApiBoardResult(id, c.Name, "Ready", jobs, null, false);
            Cache[url] = (now, result);
            return result;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return new(id, c.Name, "Failed", [], $"{c.Name} did not answer.", false);
        }
    }
}
