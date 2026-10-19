using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpportunityPilot.Application.Configuration;
using OpportunityPilot.Application.JobBoards;

namespace OpportunityPilot.Infrastructure.Research;

/// <summary>
/// Live job-board postings (Indeed, LinkedIn, SEEK) through JSearch. The key travels only in a request header and is never
/// logged. Identical searches are served from memory for <see cref="JsearchOptions.CacheMinutes"/> so the free quota lasts.
/// </summary>
public sealed class JsearchBoardClient(HttpClient http, IOptions<JsearchOptions> options, TimeProvider clock,
    ILogger<JsearchBoardClient> logger, Application.Common.OperationalMetrics metrics, IOptions<ApifyOptions>? apify = null,
    Application.Common.ProviderBudget? budget = null) : IJobBoardSearch
{
    private static readonly ConcurrentDictionary<string, JobBoardSearchResult> Cache = new();
    private static int? LastRemaining;

    public bool Configured => options.Value.Configured;

    public async Task<JobBoardSearchResult> SearchAsync(JobBoardSearchRequest request, CancellationToken ct)
    {
        var o = options.Value;
        var name = JsearchBoardParser.Name(request.Board);
        var source = $"JSearch (Google for Jobs), {name} postings only";
        if (!o.Configured)
            return new(request.Board, "NotConfigured", [], 0, $"Live {name} listings need a JSearch key on the server (Jsearch__Key).", null, false, source);

        if (request.Board == JobBoard.Naukri && apify?.Value is { Configured: true } a) return await NaukriAsync(request, a, ct);
        // Naukri and Instahyre do not publish to Google for Jobs (live check 2026-10-14: 19 postings, none from Naukri), so
        // JSearch cannot return them; say so instead of spending the monthly quota on empty searches.
        if (request.Board is JobBoard.Naukri or JobBoard.Instahyre)
            return new(request.Board, "NotConfigured", [], 0,
                $"{name} does not list its jobs on Google for Jobs. Find {name} jobs with your desktop agent (collect {name.ToLowerInvariant()}), or switch on the {name} RapidAPI board on the server.",
                null, false, source);

        var query = string.IsNullOrWhiteSpace(request.Location) ? request.Query : $"{request.Query} in {request.Location}";
        // Adding the board name steers Google for Jobs towards that board's copy of each posting.
        var parameters = new Dictionary<string, string>
        {
            ["query"] = $"{query} via {name}", ["num_pages"] = "1", ["date_posted"] = request.DatePosted,
        };
        // v5 names the remote filter work_from_home; remote_jobs_only is the earlier name. Unknown parameters are ignored.
        if (!string.IsNullOrWhiteSpace(request.Cursor)) parameters["cursor"] = request.Cursor;
        if (!string.IsNullOrWhiteSpace(request.EmploymentType)) parameters["employment_types"] = request.EmploymentType;
        if (!string.IsNullOrWhiteSpace(request.Experience)) parameters["job_requirements"] = request.Experience;
        if (request.RadiusKm is > 0) parameters["radius"] = request.RadiusKm.Value.ToString();
        if (request.RemoteOnly) { parameters["work_from_home"] = "true"; parameters["remote_jobs_only"] = "true"; }
        // SEEK only runs in Australia/New Zealand and Naukri in India: default the country so Google for Jobs returns their postings.
        var country = !string.IsNullOrWhiteSpace(request.Country) ? request.Country : request.Board switch { JobBoard.Seek => "au", JobBoard.Naukri or JobBoard.Instahyre => "in", _ => request.Country };
        if (!string.IsNullOrWhiteSpace(country)) parameters["country"] = country.ToLowerInvariant();
        var qs = string.Join("&", parameters.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));

        var now = clock.GetUtcNow().UtcDateTime;
        var cacheKey = $"{request.Board}|{qs}";
        if (Cache.TryGetValue(cacheKey, out var cached) && cached.ObservedAt > now.AddMinutes(-o.CacheMinutes))
            return cached with { FromCache = true, QuotaRemaining = LastRemaining ?? cached.QuotaRemaining };
        if (budget?.TryUse(Application.Common.ProviderBudget.Jsearch) == false)
            return new(request.Board, "Failed", [], 0, Application.Common.ProviderBudget.ExhaustedMessage, now, false, source);

        var started = clock.GetTimestamp();
        try
        {
            HttpRequestMessage Request(string path)
            {
                var message = new HttpRequestMessage(HttpMethod.Get, $"https://{o.Host}/{path.Trim('/')}?{qs}");
                message.Headers.Add("X-RapidAPI-Key", o.Key);
                message.Headers.Add("X-RapidAPI-Host", o.Host);
                return message;
            }
            using var configuredRequest = Request(o.SearchPath);
            var response = await http.SendAsync(configuredRequest, ct);
            // JSearch v5 (2026) searches at /search-v2 (seen in the RapidAPI playground); older plans used /search.
            // A stale override must not take all boards down, so a 404 retries the other known path.
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                var other = o.SearchPath.Trim('/').Equals("search-v2", StringComparison.OrdinalIgnoreCase) ? "search" : "search-v2";
                response.Dispose();
                logger.LogWarning("Configured JSearch path returned 404; retrying /{Other}", other);
                using var fallbackRequest = Request(other);
                response = await http.SendAsync(fallbackRequest, ct);
            }
            using (response)
            {
            metrics.Record("jsearch.search", clock.GetElapsedTime(started), response.IsSuccessStatusCode ? null : $"HTTP {(int)response.StatusCode}");
            int? remaining = response.Headers.TryGetValues("X-RateLimit-Requests-Remaining", out var values)
                && int.TryParse(values.FirstOrDefault(), out var left) ? left : null;
            if (remaining is not null) LastRemaining = remaining;
            if (!response.IsSuccessStatusCode)
            {
                var code = (int)response.StatusCode;
                logger.LogWarning("JSearch returned {Status}", code);
                var why = code switch
                {
                    401 or 403 => "The JSearch key was refused. Check Jsearch__Key and that the RapidAPI plan is subscribed.",
                    429 => "The JSearch monthly or hourly quota is used up. Try again later.",
                    404 => "The configured JSearch endpoint was not found. Check Jsearch__SearchPath (v5 uses /search-v2).",
                    _ => $"JSearch answered {code}. Try again later.",
                };
                return new(request.Board, "Failed", [], 0, why, now, false, source, remaining);
            }
            var body = await response.Content.ReadAsStringAsync(ct);
            var (jobs, total) = JsearchBoardParser.Parse(body, request.Board);
            var nextCursor = JsearchBoardParser.NextCursor(body);
            var result = new JobBoardSearchResult(request.Board, "Ready", jobs, total,
                jobs.Count == 0 ? $"JSearch returned {total} postings; none were published on {name}. Try a broader search." : null,
                now, false, source, remaining, nextCursor);
            Cache[cacheKey] = result;
            return result;
            }
        }
        catch (Exception e) when ((e is HttpRequestException or TaskCanceledException or JsonException) && !ct.IsCancellationRequested)
        {
            logger.LogWarning("JSearch request failed: {Type}", e.GetType().Name);
            metrics.Record("jsearch.search", clock.GetElapsedTime(started), e.GetType().Name);
            return new(request.Board, "Failed", [], 0, "JSearch could not be reached. Try again later.", now, false, source);
        }
    }

    /// <summary>Naukri through the Apify actor: one synchronous run returns the jobs on Naukri's own search page. Cached per search.</summary>
    private async Task<JobBoardSearchResult> NaukriAsync(JobBoardSearchRequest request, ApifyOptions a, CancellationToken ct)
    {
        const string source = "Naukri, read through an Apify scraper";
        var now = clock.GetUtcNow().UtcDateTime;
        var searchUrl = NaukriApify.SearchUrl(request.Query, request.Location);
        var cacheKey = "naukri|" + searchUrl;
        if (Cache.TryGetValue(cacheKey, out var cached) && cached.ObservedAt > now.AddMinutes(-a.CacheMinutes)) return cached with { FromCache = true };
        if (budget?.TryUse(Application.Common.ProviderBudget.Apify) == false)
            return new(request.Board, "Failed", [], 0, Application.Common.ProviderBudget.ExhaustedMessage, now, false, source);
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post,
                $"https://api.apify.com/v2/acts/{Uri.EscapeDataString(a.NaukriActor)}/run-sync-get-dataset-items?timeout=110&clean=true");
            message.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", a.Token);
            message.Content = new StringContent(NaukriApify.Input(searchUrl, a.MaxJobs), System.Text.Encoding.UTF8, "application/json");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(120));
            using var response = await http.SendAsync(message, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                var code = (int)response.StatusCode;
                logger.LogWarning("Apify Naukri run answered {Code}", code);
                var why = code switch
                {
                    401 or 403 => "Apify refused the token. Check Apify__Token on the server.",
                    402 => "The free Apify credit for this month is used up. Naukri search resumes next month.",
                    408 => "Naukri took too long to answer. Try again in a minute.",
                    _ => $"The Naukri reader answered {code}. Try again later.",
                };
                return new(request.Board, "Failed", [], 0, why, now, false, source);
            }
            var jobs = NaukriApify.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            var result = new JobBoardSearchResult(request.Board, "Ready", jobs, jobs.Count,
                jobs.Count == 0 ? "Naukri had no jobs for this search. Try fewer words or another city." : null, now, false, source);
            if (jobs.Count > 0) Cache[cacheKey] = result;
            return result;
        }
        catch (Exception e) when ((e is HttpRequestException or TaskCanceledException or JsonException) && !ct.IsCancellationRequested)
        {
            logger.LogWarning("Apify Naukri run failed: {Type}", e.GetType().Name);
            return new(request.Board, "Failed", [], 0, "Naukri could not be read right now. Try again in a minute.", now, false, source);
        }
    }
}
