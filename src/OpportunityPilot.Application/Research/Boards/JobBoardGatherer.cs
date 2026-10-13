using Microsoft.Extensions.Options;
using OpportunityPilot.Application.Campaigns;
using OpportunityPilot.Application.Configuration;
using OpportunityPilot.Application.JobBoards;
using OpportunityPilot.Application.Research.Rules;
using OpportunityPilot.Domain.Common;
using OpportunityPilot.Domain.Research;

namespace OpportunityPilot.Application.Research.Boards;

/// <param name="Items">Candidates read, already bounded by the run's room.</param>
/// <param name="Requests">HTTP requests sent (retries and redirects included), charged to the run's fetch budget.</param>
public sealed record BoardGathered(SourceStatus Status, string? SafeError, IReadOnlyList<Candidate> Items, int Requests, string Message, EventLevel Level);

/// <summary>
/// Reads the documented public job-board APIs (Greenhouse, Lever, Adzuna) through <see cref="IWebFetcher.FetchJsonAsync"/>,
/// so every request gets the same address policy, size cap, timeout and redirect checks as any other research fetch.
/// No login, no scraping: these endpoints exist to publish job listings. Messages never contain a request URL (the
/// Adzuna one carries the server's key).
/// </summary>
public sealed class JobBoardGatherer(IWebFetcher fetcher, IContentParser parser, IOptions<ResearchOptions> research, IOptions<AdzunaOptions> adzuna,
    IJobBoardSearch jobBoards)
{
    public const int AdzunaMaxKeywords = 3;
    public const int AdzunaResultsPerPage = 50;
    public const int AdzunaMaxDaysOld = 14;
    public const int LeverLimit = 100;

    /// <summary>Greenhouse reads one description per request; the run is kept alive (lease, cancel check) every this many.</summary>
    public const int KeepAliveEvery = 10;

    private readonly ResearchOptions _limits = research.Value;

    /// <param name="keepGoing">Renews the job's lease and reports whether to continue (false once a cancel was requested).</param>
    public Task<BoardGathered> GatherAsync(Source source, CampaignCriteria criteria, int room, int fetchesLeft,
        Func<CancellationToken, Task<bool>> keepGoing, CancellationToken ct) => source.Kind switch
    {
        SourceKind.Greenhouse => GreenhouseAsync(source, criteria, room, fetchesLeft, keepGoing, ct),
        SourceKind.Lever => LeverAsync(source, room, fetchesLeft, ct),
        SourceKind.Adzuna => AdzunaAsync(source, criteria, room, fetchesLeft, ct),
        SourceKind.Ashby => SimpleBoardAsync(source, room, fetchesLeft, BoardIdentifiers.Ashby,
            $"{Base(_limits.AshbyApiBase, ResearchOptions.DefaultAshbyApiBase)}/posting-api/job-board/{{0}}",
            (json, slug) => BoardMapping.AshbyCandidates(json, source, slug, parser), ct),
        SourceKind.SmartRecruiters => SimpleBoardAsync(source, room, fetchesLeft, BoardIdentifiers.SmartRecruiters,
            $"{Base(_limits.SmartRecruitersApiBase, ResearchOptions.DefaultSmartRecruitersApiBase)}/v1/companies/{{0}}/postings?limit=100",
            (json, slug) => BoardMapping.SmartRecruitersCandidates(json, source, slug, parser), ct),
        SourceKind.Recruitee => SimpleBoardAsync(source, room, fetchesLeft, BoardIdentifiers.Recruitee,
            $"https://{{0}}.{Host(_limits.RecruiteeHostSuffix, ResearchOptions.DefaultRecruiteeHostSuffix)}/api/offers/",
            (json, slug) => BoardMapping.RecruiteeCandidates(json, source, slug, parser), ct),
        SourceKind.Workable => SimpleBoardAsync(source, room, fetchesLeft, BoardIdentifiers.Workable,
            $"{Base(_limits.WorkableApiBase, ResearchOptions.DefaultWorkableApiBase)}/api/v1/widget/accounts/{{0}}?details=true",
            (json, slug) => BoardMapping.WorkableCandidates(json, source, slug, parser), ct),
        SourceKind.Workday => WorkdayAsync(source, criteria, room, fetchesLeft, keepGoing, ct),
        SourceKind.Remotive => AggregateAsync(source, room, fetchesLeft, criteria,
            $"{Base(_limits.RemotiveApiBase, ResearchOptions.DefaultRemotiveApiBase)}/api/remote-jobs",
            json => BoardMapping.RemotiveCandidates(json, source, parser), ct),
        SourceKind.RemoteOk => AggregateAsync(source, room, fetchesLeft, criteria,
            $"{Base(_limits.RemoteOkApiBase, ResearchOptions.DefaultRemoteOkApiBase)}/api",
            json => BoardMapping.RemoteOkCandidates(json, source, parser), ct),
        SourceKind.Arbeitnow => ReadOneAsync(source, room, fetchesLeft,
            $"{Base(_limits.ArbeitnowApiBase, ResearchOptions.DefaultArbeitnowApiBase)}/api/job-board-api",
            json => BoardMapping.ArbeitnowCandidates(json, source, parser), ct, ResearchOptions.LargeFeedBytes, criteria: criteria),
        SourceKind.Jobicy => ReadOneAsync(source, room, fetchesLeft,
            $"{Base(_limits.JobicyApiBase, ResearchOptions.DefaultJobicyApiBase)}/api/v2/remote-jobs?count=50",
            json => BoardMapping.JobicyCandidates(json, source, parser), ct, criteria: criteria),
        SourceKind.WeWorkRemotely => ReadOneAsync(source, room, fetchesLeft,
            $"{Base(_limits.WeWorkRemotelyBase, ResearchOptions.DefaultWeWorkRemotelyBase)}/remote-jobs.rss",
            xml => BoardMapping.WeWorkRemotelyCandidates(xml, source, parser), ct, ResearchOptions.LargeFeedBytes, rss: true, criteria: criteria),
        _ => throw new ArgumentOutOfRangeException(nameof(source), "Not a job-board source.")
    };

    private Task<BoardGathered> AggregateAsync(Source source, int room, int fetchesLeft, CampaignCriteria criteria, string url,
        Func<string?, IReadOnlyList<Candidate>?> map, CancellationToken ct) =>
        ReadOneAsync(source, room, fetchesLeft, url, map, ct, criteria: criteria);

    /// <summary>
    /// The free feeds list every job on the site, not just yours: keep the ones where every word of at least one campaign keyword
    /// appears in the title, company or text. No keywords means nothing is dropped.
    /// </summary>
    public static bool MatchesKeywords(Candidate c, IReadOnlyList<string> keywords)
    {
        var terms = keywords.Select(k => k.Trim()).Where(k => k.Length > 0).ToList();
        if (terms.Count == 0) return true;
        var haystack = $"{c.Title} {c.Organization} {c.Text}";
        return terms.Any(k => k.Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries).All(w => haystack.Contains(w, StringComparison.OrdinalIgnoreCase)));
    }

    private Task<BoardGathered> SimpleBoardAsync(Source source, int room, int fetchesLeft,
        Func<string?, string?> identify, string urlTemplate, Func<string?, string, IReadOnlyList<Candidate>?> map, CancellationToken ct)
    {
        var slug = identify(source.Url);
        if (slug is null) return Task.FromResult(Failed(source, $"The {source.Kind} company identifier is not valid. Delete the source and add it again.", 0));
        return ReadOneAsync(source, room, fetchesLeft, string.Format(System.Globalization.CultureInfo.InvariantCulture, urlTemplate, Uri.EscapeDataString(slug)),
            json => map(json, slug), ct);
    }

    private async Task<BoardGathered> ReadOneAsync(Source source, int room, int fetchesLeft, string url,
        Func<string?, IReadOnlyList<Candidate>?> map, CancellationToken ct, int? maxBytes = null, bool rss = false, CampaignCriteria? criteria = null)
    {
        if (fetchesLeft <= 0) return FetchLimitReached(source);
        var response = rss ? await fetcher.FetchAsync(url, ct, maxBytes) : await fetcher.FetchJsonAsync(url, ct, maxBytes);
        if (!response.Ok) return Failed(source, response.StatusCode == 404
            ? $"No public {source.Kind} jobs were found. Check the source identifier."
            : response.FailureReason ?? $"{source.Kind} could not be read.", response.Requests);
        var all = map(response.Content);
        if (all is null) return Failed(source, $"{source.Kind} sent a response that could not be read.", response.Requests);
        if (all.Count == 0) return new(SourceStatus.Skipped, $"The {source.Kind} source has no open jobs.", [], response.Requests,
            $"{source.Label}: no open jobs.", EventLevel.Warning);
        var listed = all.Count;
        if (criteria is { Keywords.Count: > 0 })
        {
            all = all.Where(c => MatchesKeywords(c, criteria.Keywords)).ToList();
            if (all.Count == 0) return new(SourceStatus.Skipped, $"None of the {Count(listed, "job")} on {source.Label} matched your keywords.", [], response.Requests,
                $"{source.Label}: {Count(listed, "job")} listed, none matched your keywords.", EventLevel.Warning);
        }
        var items = all.Take(room).ToList();
        var message = criteria is { Keywords.Count: > 0 }
            ? $"{source.Label}: {Count(listed, "job")} listed, {all.Count} matched your keywords, {items.Count} read."
            : $"{source.Label}: {Count(all.Count, "job")} listed, {items.Count} read.";
        if (items.Count < all.Count) message += $" Stopped at the run's limit of {_limits.EffectiveCandidates} candidates.";
        return new(SourceStatus.Ok, null, items, response.Requests, message, items.Count < all.Count ? EventLevel.Warning : EventLevel.Info);
    }

    private async Task<BoardGathered> GreenhouseAsync(Source source, CampaignCriteria criteria, int room, int fetchesLeft,
        Func<CancellationToken, Task<bool>> keepGoing, CancellationToken ct)
    {
        if (BoardIdentifiers.Greenhouse(source.Url) is not { } token)
            return Failed(source, "The Greenhouse board token is not valid. Delete the source and add it again.", 0);
        if (fetchesLeft <= 0) return FetchLimitReached(source);

        var root = Base(_limits.GreenhouseApiBase, ResearchOptions.DefaultGreenhouseApiBase);
        var list = await fetcher.FetchJsonAsync($"{root}/v1/boards/{token}/jobs", ct);
        var requests = list.Requests;
        if (!list.Ok)
            return Failed(source, list.StatusCode == 404
                ? $"No public Greenhouse board was found for \"{token}\". Check the board token."
                : list.FailureReason ?? "The board could not be read.", requests);
        if (BoardMapping.GreenhouseJobs(list.Content) is not { } jobs)
            return Failed(source, "Greenhouse sent a response that could not be read.", requests);
        if (jobs.Count == 0)
            return new(SourceStatus.Skipped, "The Greenhouse board has no open jobs.", [], requests, $"{source.Label}: the board has no open jobs.",
                EventLevel.Warning);

        var terms = BoardMapping.TitleTerms(criteria);
        var matched = BoardMapping.Prefilter(jobs, terms);
        var items = new List<Candidate>();
        int unreadable = 0;
        string? stoppedBy = null;
        foreach (var job in matched)
        {
            if (items.Count >= room) { stoppedBy = $"the run's limit of {_limits.EffectiveCandidates} candidates"; break; }
            if (fetchesLeft - requests <= 0) { stoppedBy = $"the run's limit of {_limits.EffectiveFetches} fetches"; break; }
            if (items.Count > 0 && items.Count % KeepAliveEvery == 0 && !await keepGoing(ct)) { stoppedBy = "a cancel request"; break; }

            var detail = await fetcher.FetchJsonAsync($"{root}/v1/boards/{token}/jobs/{Uri.EscapeDataString(job.Id)}", ct);
            requests += detail.Requests;
            var description = detail.Ok ? BoardMapping.GreenhouseDescription(detail.Content, parser) : null;
            if (description is null) unreadable++;
            // A job whose description failed is still listed: the rules mark what they cannot see as unknown.
            items.Add(BoardMapping.GreenhouseCandidate(source, token, job, description));
        }

        var matchedText = terms.Count == 0
            ? $"{Count(matched.Count, "job")} kept (no keywords or required skills to match titles against)"
            : $"{matched.Count} matched your keywords";
        var message = $"{source.Label}: {Count(jobs.Count, "job")} listed, {matchedText}, {items.Count} read.";
        if (stoppedBy is not null) message += $" Stopped at {stoppedBy}.";
        if (unreadable > 0) message += $" {Count(unreadable, "description")} could not be loaded, so those jobs are scored on the title only.";
        return new(SourceStatus.Ok, null, items, requests, message, stoppedBy is null && unreadable == 0 ? EventLevel.Info : EventLevel.Warning);
    }

    private async Task<BoardGathered> LeverAsync(Source source, int room, int fetchesLeft, CancellationToken ct)
    {
        if (BoardIdentifiers.Lever(source.Url) is not { } slug)
            return Failed(source, "The Lever company slug is not valid. Delete the source and add it again.", 0);
        if (fetchesLeft <= 0) return FetchLimitReached(source);

        var root = Base(_limits.LeverApiBase, ResearchOptions.DefaultLeverApiBase);
        var result = await fetcher.FetchJsonAsync($"{root}/v0/postings/{slug}?mode=json&limit={LeverLimit}", ct);
        if (!result.Ok)
            return Failed(source, result.StatusCode == 404
                ? $"No public Lever postings were found for \"{slug}\". Check the company slug."
                : result.FailureReason ?? "The postings could not be read.", result.Requests);
        if (BoardMapping.LeverCandidates(result.Content, source, slug, parser) is not { } postings)
            return Failed(source, "Lever sent a response that could not be read.", result.Requests);
        if (postings.Count == 0)
            return new(SourceStatus.Skipped, "The company has no open postings on Lever.", [], result.Requests,
                $"{source.Label}: no open postings.", EventLevel.Warning);

        var items = postings.Take(room).ToList();
        var message = $"{source.Label}: {Count(postings.Count, "posting")} listed, {items.Count} read.";
        if (items.Count < postings.Count) message += $" Stopped at the run's limit of {_limits.EffectiveCandidates} candidates.";
        return new(SourceStatus.Ok, null, items, result.Requests, message, items.Count < postings.Count ? EventLevel.Warning : EventLevel.Info);
    }

    private async Task<BoardGathered> AdzunaAsync(Source source, CampaignCriteria criteria, int room, int fetchesLeft, CancellationToken ct)
    {
        var keys = adzuna.Value;
        if (!keys.Configured) return Failed(source, "Adzuna is not configured on the server.", 0);
        var keywords = criteria.Keywords.Take(AdzunaMaxKeywords).ToList();
        if (keywords.Count == 0)
            return new(SourceStatus.Skipped, "Adzuna searches by keyword: add keywords to the campaign.", [], 0,
                $"{source.Label}: skipped, the campaign has no keywords to search for.", EventLevel.Warning);
        if (fetchesLeft <= 0) return FetchLimitReached(source);

        var where = criteria.Locations.FirstOrDefault(l => !l.Trim().Equals("Remote", StringComparison.OrdinalIgnoreCase));
        var root = Base(_limits.AdzunaApiBase, ResearchOptions.DefaultAdzunaApiBase);
        var found = new Dictionary<string, Candidate>(StringComparer.Ordinal);
        var requests = 0;
        var searched = 0;
        string? failure = null;
        string? stoppedBy = null;
        foreach (var keyword in keywords)
        {
            if (fetchesLeft - requests <= 0) { stoppedBy = $"the run's limit of {_limits.EffectiveFetches} fetches"; break; }
            // Built here and passed straight to the fetcher: this URL holds the key, so it is never logged or recorded.
            var url = $"{root}/v1/api/jobs/in/search/1?app_id={Uri.EscapeDataString(keys.AppId!)}&app_key={Uri.EscapeDataString(keys.AppKey!)}" +
                      $"&what={Uri.EscapeDataString(keyword)}" + (where is null ? "" : $"&where={Uri.EscapeDataString(where)}") +
                      $"&results_per_page={AdzunaResultsPerPage}&max_days_old={AdzunaMaxDaysOld}&content-type=application/json";
            var result = await fetcher.FetchJsonAsync(url, ct);
            requests += result.Requests;
            if (!result.Ok)
            {
                failure ??= result.StatusCode is 401 or 403
                    ? "Adzuna rejected the server's keys. Check Adzuna:AppId and Adzuna:AppKey."
                    : result.FailureReason ?? "Adzuna could not be read.";
                continue;
            }
            if (BoardMapping.AdzunaCandidates(result.Content, source, parser) is not { } jobs)
            {
                failure ??= "Adzuna sent a response that could not be read.";
                continue;
            }
            searched++;
            foreach (var job in jobs) found.TryAdd(job.ExternalId!, job);
        }

        if (searched == 0) return Failed(source, failure ?? "Adzuna could not be read.", requests);
        var items = found.Values.Take(room).ToList();
        var place = where is null ? "" : $" in {where}";
        var message = $"{source.Label}: searched {Count(searched, "keyword")}{place}, {Count(found.Count, "job")} found, {items.Count} read.";
        if (items.Count < found.Count) message += $" Stopped at the run's limit of {_limits.EffectiveCandidates} candidates.";
        if (stoppedBy is not null) message += $" Stopped at {stoppedBy}.";
        if (failure is not null) message += $" Some searches failed: {failure}";
        var level = items.Count < found.Count || stoppedBy is not null || failure is not null ? EventLevel.Warning : EventLevel.Info;
        return new(SourceStatus.Ok, null, items, requests, message, level);
    }

    public const int JobSearchMaxKeywords = 3;

    /// <summary>
    /// Indeed, LinkedIn and SEEK have no public search API, so postings come from JSearch (a licensed Google-for-Jobs
    /// service, server key) and only those with an https link on the board's own domain are kept. One search per term
    /// (Job: keywords; other modes: keywords, then buying signals) in the first non-"Remote" location, past month. Job
    /// campaigns get one candidate per posting; other modes get one candidate per hiring company, with its postings as
    /// evidence. Each search not served from the API's cache costs one fetch.
    /// </summary>
    public async Task<BoardGathered> JobSearchAsync(Source source, CampaignCriteria criteria, OpportunityMode mode, int room, int fetchesLeft,
        CancellationToken ct)
    {
        var board = Enum.TryParse<JobBoard>(source.Url, ignoreCase: true, out var b) && Enum.IsDefined(b) ? b : JobBoard.Indeed;
        var name = JsearchBoardParser.Name(board);
        if (!jobBoards.Configured)
            return Failed(source, $"{name} search needs the server's JSearch key (Jsearch:Key), which is not set.", 0);
        var job = mode == OpportunityMode.Job;
        var terms = (job ? criteria.Keywords : criteria.Keywords.Concat(criteria.Signals))
            .Select(k => k.Trim()).Where(k => k.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(JobSearchMaxKeywords).ToList();
        if (terms.Count == 0)
            return new(SourceStatus.Skipped, $"{name} search needs {(job ? "keywords" : "keywords or buying signals")}: add them to the campaign.", [], 0,
                $"{source.Label}: skipped, the campaign has nothing to search for.", EventLevel.Warning);
        if (fetchesLeft <= 0) return FetchLimitReached(source);

        var where = criteria.Locations.FirstOrDefault(l => !l.Trim().Equals("Remote", StringComparison.OrdinalIgnoreCase))?.Trim();
        var remoteOnly = where is null && criteria.Locations.Count > 0;
        // SEEK only runs in Australia and New Zealand; the API client defaults SEEK to Australia.
        var postings = new Dictionary<string, JobBoardJobDto>(StringComparer.Ordinal);
        var requests = 0;
        var searched = 0;
        var cached = 0;
        string? failure = null;
        string? stoppedBy = null;
        foreach (var term in terms)
        {
            if (fetchesLeft - requests <= 0) { stoppedBy = $"the run's limit of {_limits.EffectiveFetches} fetches"; break; }
            var result = await jobBoards.SearchAsync(new JobBoardSearchRequest(board, term, where, remoteOnly, "month", null, 1), ct);
            if (result.FromCache) cached++; else requests++;
            if (result.Status != "Ready") { failure ??= result.Message ?? "JSearch could not be read."; continue; }
            searched++;
            foreach (var posting in result.Jobs) postings.TryAdd(posting.ProviderJobId, posting);
        }

        if (searched == 0) return Failed(source, failure ?? "JSearch could not be read.", requests);
        var all = job
            ? postings.Values.Select(p => BoardMapping.JobSearchCandidate(source, board, p)).ToList()
            : postings.Values.GroupBy(p => p.CompanyName.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(g => BoardMapping.HiringCompanyCandidate(source, board, g.Key, g.ToList())).ToList();
        var items = all.Take(room).ToList();
        var place = where is null ? (remoteOnly ? " (remote only)" : "") : $" in {where}";
        var what = job ? Count(all.Count, $"{name} posting") : $"{Count(all.Count, "hiring company", "hiring companies")} from {Count(postings.Count, $"{name} posting")}";
        var message = $"{source.Label}: searched {Count(searched, "term")}{place} through JSearch, {what} found, {items.Count} read.";
        if (cached > 0) message += $" {Count(cached, "search")} reused from the last few hours to save the JSearch quota.";
        if (postings.Count == 0) message += $" No matching posting was published on {name}; try broader terms.";
        if (items.Count < all.Count) message += $" Stopped at the run's limit of {_limits.EffectiveCandidates} candidates.";
        if (stoppedBy is not null) message += $" Stopped at {stoppedBy}.";
        if (failure is not null) message += $" Some searches failed: {failure}";
        var level = postings.Count == 0 || items.Count < all.Count || stoppedBy is not null || failure is not null ? EventLevel.Warning : EventLevel.Info;
        return new(SourceStatus.Ok, null, items, requests, message, level);
    }

    /// <summary>Searches per keyword (at most this many searches), then reads each matching job's detail.</summary>
    public const int WorkdayMaxSearches = 3;

    private async Task<BoardGathered> WorkdayAsync(Source source, CampaignCriteria criteria, int room, int fetchesLeft,
        Func<CancellationToken, Task<bool>> keepGoing, CancellationToken ct)
    {
        if (WorkdayBoard.Parse(source.Url) is not { } board)
            return Failed(source, "The Workday careers URL is not valid. Delete the source and add it again.", 0);
        if (fetchesLeft <= 0) return FetchLimitReached(source);

        var root = string.IsNullOrWhiteSpace(_limits.WorkdayApiBase) ? $"https://{board.Host}" : Base(_limits.WorkdayApiBase, "");
        var api = $"{root}/wday/cxs/{Uri.EscapeDataString(board.Tenant)}/{Uri.EscapeDataString(board.Site)}";
        var terms = BoardMapping.TitleTerms(criteria);
        // Workday sites can list thousands of jobs, so the site's own search narrows them first: one search per
        // keyword (an empty search when the campaign has none), each giving at most one page of 20.
        var searches = criteria.Keywords.Select(k => k.Trim()).Where(k => k.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(WorkdayMaxSearches).DefaultIfEmpty("").ToList();

        var requests = 0;
        var listed = 0;
        var found = new List<WorkdayPosting>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? failure = null;
        foreach (var search in searches)
        {
            if (fetchesLeft - requests <= 0) break;
            var page = await fetcher.PostJsonAsync($"{api}/jobs", WorkdayMapping.SearchBody(search), ct);
            requests += page.Requests;
            if (!page.Ok)
            {
                failure = page.StatusCode is 404 or 422
                    ? $"No public Workday careers site was found at {board.SiteUrl}. Check the URL."
                    : page.FailureReason ?? "The careers site could not be read.";
                continue;
            }
            if (WorkdayMapping.Postings(page.Content) is not { } result) { failure = "Workday sent a response that could not be read."; continue; }
            listed += result.Total;
            foreach (var posting in result.Postings)
                if (seen.Add(posting.ExternalPath)) found.Add(posting);
        }
        if (found.Count == 0)
            return failure is not null
                ? Failed(source, failure, requests)
                : new(SourceStatus.Skipped, "The Workday search found no open jobs.", [], requests,
                    $"{source.Label}: no open jobs matched {(searches is [""] ? "the site" : "your keywords")}.", EventLevel.Warning);

        var matched = found
            .Select((p, i) => (p, i, hits: terms.Count == 0 ? 0 : terms.Count(t => TextMatch.Contains(p.Title, t))))
            .OrderByDescending(x => x.hits).ThenBy(x => x.i).Select(x => x.p).ToList();
        var items = new List<Candidate>();
        int unreadable = 0;
        string? stoppedBy = null;
        foreach (var posting in matched)
        {
            if (items.Count >= room) { stoppedBy = $"the run's limit of {_limits.EffectiveCandidates} candidates"; break; }
            if (fetchesLeft - requests <= 0) { stoppedBy = $"the run's limit of {_limits.EffectiveFetches} fetches"; break; }
            if (items.Count > 0 && items.Count % KeepAliveEvery == 0 && !await keepGoing(ct)) { stoppedBy = "a cancel request"; break; }

            var detail = await fetcher.FetchJsonAsync($"{api}{posting.ExternalPath}", ct);
            requests += detail.Requests;
            var parsed = detail.Ok ? WorkdayMapping.Detail(detail.Content, parser) : null;
            if (parsed?.Description is null) unreadable++;
            items.Add(WorkdayMapping.Candidate(source, board, posting, parsed));
        }

        var searched = searches is [""] ? "the whole site" : $"{Count(searches.Count, "search", "searches")} ({string.Join(", ", searches)})";
        var message = $"{source.Label}: {searched} found {Count(found.Count, "job")}, {items.Count} read.";
        if (stoppedBy is not null) message += $" Stopped at {stoppedBy}.";
        if (unreadable > 0) message += $" {Count(unreadable, "description")} could not be loaded, so those jobs are scored on the title only.";
        if (failure is not null) message += $" One search failed: {failure}";
        var level = stoppedBy is null && unreadable == 0 && failure is null ? EventLevel.Info : EventLevel.Warning;
        return new(SourceStatus.Ok, null, items, requests, message, level);
    }

    private static BoardGathered Failed(Source source, string reason, int requests) =>
        new(SourceStatus.Failed, reason, [], requests, $"{source.Label}: could not be read safely — {reason}", EventLevel.Warning);

    private BoardGathered FetchLimitReached(Source source) =>
        new(SourceStatus.Skipped, $"Skipped: this run already used its {_limits.EffectiveFetches} page fetches.", [], 0,
            $"{source.Label}: skipped, fetch limit reached.", EventLevel.Warning);

    private static string Base(string? configured, string fallback) =>
        (string.IsNullOrWhiteSpace(configured) ? fallback : configured.Trim()).TrimEnd('/');

    private static string Host(string? configured, string fallback) =>
        (string.IsNullOrWhiteSpace(configured) ? fallback : configured.Trim()).Trim().Trim('/');

    private static string Count(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";

    private static string Count(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";
}
