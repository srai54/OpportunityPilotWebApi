using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpportunityPilot.Application.Configuration;
using OpportunityPilot.Application.Trends;

namespace OpportunityPilot.Infrastructure.Research;

/// <summary>
/// One JSearch search per page for the trends report. The query is sent as typed (no board is named), so postings from every
/// site come back. Identical searches are served from memory for the configured cache time to protect the monthly quota.
/// </summary>
public sealed class JsearchTrendClient(HttpClient http, IOptions<JsearchOptions> options, TimeProvider clock,
    ILogger<JsearchTrendClient> logger, Application.Common.OperationalMetrics metrics, Application.Common.ProviderBudget? budget = null) : ITrendSource
{
    private static readonly ConcurrentDictionary<string, TrendPage> Cache = new();

    public bool Configured => options.Value.Configured;

    public async Task<TrendPage> PageAsync(TrendPageRequest request, CancellationToken ct)
    {
        var o = options.Value;
        if (!o.Configured) return new("NotConfigured", "Trends need a JSearch key on the server (Jsearch__Key).", [], null, null, false, null);

        var query = string.IsNullOrWhiteSpace(request.Location) ? request.Query : $"{request.Query} in {request.Location}";
        var parameters = new Dictionary<string, string> { ["query"] = query, ["num_pages"] = "1", ["date_posted"] = request.DatePosted };
        if (!string.IsNullOrWhiteSpace(request.Cursor)) parameters["cursor"] = request.Cursor;
        if (!string.IsNullOrWhiteSpace(request.Country)) parameters["country"] = request.Country.ToLowerInvariant();
        var qs = string.Join("&", parameters.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));

        var now = clock.GetUtcNow().UtcDateTime;
        if (Cache.TryGetValue(qs, out var cached) && cached.ObservedAt > now.AddMinutes(-o.CacheMinutes)) return cached with { FromCache = true };
        if (budget?.TryUse(Application.Common.ProviderBudget.Jsearch) == false)
            return new TrendPage("Failed", Application.Common.ProviderBudget.ExhaustedMessage, [], null, null, false, now);

        var started = clock.GetTimestamp();
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Get, $"https://{o.Host}/{o.SearchPath.Trim('/')}?{qs}");
            message.Headers.Add("X-RapidAPI-Key", o.Key);
            message.Headers.Add("X-RapidAPI-Host", o.Host);
            using var response = await http.SendAsync(message, ct);
            metrics.Record("jsearch.trends", clock.GetElapsedTime(started), response.IsSuccessStatusCode ? null : $"HTTP {(int)response.StatusCode}");
            int? remaining = response.Headers.TryGetValues("X-RateLimit-Requests-Remaining", out var values) && int.TryParse(values.FirstOrDefault(), out var left) ? left : null;
            if (!response.IsSuccessStatusCode)
            {
                var code = (int)response.StatusCode;
                logger.LogWarning("JSearch trends returned {Status}", code);
                var why = code switch
                {
                    401 or 403 => "The JSearch key was refused.",
                    429 => "The JSearch monthly or hourly quota is used up. Try again later.",
                    _ => $"JSearch answered {code}. Try again later.",
                };
                return new("Failed", why, [], null, remaining, false, now);
            }
            var (postings, next) = JsearchTrendParser.Parse(await response.Content.ReadAsStringAsync(ct));
            var page = new TrendPage("Ready", null, postings, next, remaining, false, now);
            Cache[qs] = page;
            return page;
        }
        catch (Exception e) when ((e is HttpRequestException or TaskCanceledException or JsonException) && !ct.IsCancellationRequested)
        {
            logger.LogWarning("JSearch trends request failed: {Type}", e.GetType().Name);
            metrics.Record("jsearch.trends", clock.GetElapsedTime(started), e.GetType().Name);
            return new("Failed", "JSearch could not be reached. Try again later.", [], null, null, false, now);
        }
    }
}
