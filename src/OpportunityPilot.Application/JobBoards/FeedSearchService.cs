using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using OpportunityPilot.Application.Configuration;
using OpportunityPilot.Application.Research;
using OpportunityPilot.Application.Research.Boards;
using OpportunityPilot.Domain.Opportunities;
using OpportunityPilot.Domain.Research;

namespace OpportunityPilot.Application.JobBoards;

/// <summary>
/// The job sites "Search all" can read live. All are free public feeds without a key, except Adzuna, which needs the server's
/// <c>Adzuna__AppId</c>/<c>Adzuna__AppKey</c> and reports NotConfigured (without calling out) when they are missing.
/// </summary>
public enum FeedSite { Arbeitnow, Jobicy, WeWorkRemotely, Remotive, RemoteOk, Himalayas, TheMuse, Adzuna, Instahyre, WorkingNomads, HackerNews, Jooble, Careerjet, Findwork, UsaJobs, Freelancer, Reed, Arbeitsagentur }

public sealed record FeedSiteStatus(string Site, string Status, int Listed, int Matched, string? Message, bool FromCache);

public sealed record FeedSearchResult(IReadOnlyList<FeedSiteStatus> Sites, IReadOnlyList<FeedJobDto> Jobs, DateTime ObservedAt);

/// <summary>One posting from a feed, in the same shape the paid boards use plus the site it came from.</summary>
public sealed record FeedJobDto(string Site, string SiteName, JobBoardJobDto Job);

/// <summary>
/// "Search all": reads the free public feeds (Arbeitnow, Jobicy, We Work Remotely, Remotive, Remote OK, Himalayas, The Muse) and, when the
/// server has keys, Adzuna; keeps each answer for 30 minutes so repeated searches cost nothing, and filters it by the person's words and
/// place. Read-only.
/// </summary>
public sealed class FeedSearchService(IWebFetcher fetcher, IContentParser parser, IOptions<ResearchOptions> research, TimeProvider clock,
    IRapidApiBoardSearch? regional = null, IOptions<AdzunaOptions>? adzuna = null, KeyedFeeds? keyed = null)
{
    public static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(30);
    public const int MaxTake = 100;

    /// <summary>Adzuna answers are kept per search (country, words, place); past this many the oldest are dropped.</summary>
    public const int MaxAdzunaCached = 200;

    private static readonly ConcurrentDictionary<FeedSite, (DateTime At, IReadOnlyList<string> Pages)> Cache = new();

    // Keyed by country, words and place only: the keys are never part of a cache key.
    private static readonly ConcurrentDictionary<string, (DateTime At, string Text)> AdzunaCache = new(StringComparer.Ordinal);
    private readonly ResearchOptions _limits = research.Value;

    public bool AdzunaConfigured => adzuna?.Value.Configured == true;

    /// <summary>What "all sites" means on this server: every feed, and Adzuna only when its keys are set.</summary>
    public IReadOnlyList<FeedSite> DefaultSites => Enum.GetValues<FeedSite>()
        .Where(s => (s != FeedSite.Adzuna || AdzunaConfigured) && (!KeyedFeeds.Sites.Contains(s) || keyed?.Configured(s) == true)).ToList();

    public static string Name(FeedSite site) => site switch
    {
        FeedSite.Arbeitnow => "Arbeitnow",
        FeedSite.Jobicy => "Jobicy",
        FeedSite.WeWorkRemotely => "We Work Remotely",
        FeedSite.Remotive => "Remotive",
        FeedSite.Himalayas => "Himalayas",
        FeedSite.TheMuse => "The Muse",
        FeedSite.Instahyre => "Instahyre",
        FeedSite.Adzuna => "Adzuna",
        FeedSite.WorkingNomads => "Working Nomads",
        FeedSite.HackerNews => "HN Who is hiring",
        FeedSite.Jooble => "Jooble",
        FeedSite.Careerjet => "Careerjet",
        FeedSite.Findwork => "Findwork",
        FeedSite.UsaJobs => "USAJOBS",
        FeedSite.Freelancer => "Freelancer.com",
        FeedSite.Reed => "Reed",
        FeedSite.Arbeitsagentur => "Bundesagentur für Arbeit",
        _ => "Remote OK"
    };

    public async Task<FeedSearchResult> SearchAsync(IReadOnlyList<FeedSite> sites, string query, string? location, bool remoteOnly, int take, CancellationToken ct)
    {
        take = Math.Clamp(take, 1, MaxTake);
        var now = clock.GetUtcNow().UtcDateTime;
        var statuses = new List<FeedSiteStatus>();
        var jobs = new List<FeedJobDto>();
        var regionalTask = regional is null || regional.Boards.Count == 0 ? Task.FromResult<RapidApiBoardResult[]>([])
            : Task.WhenAll(regional.Boards.Select(b => regional.SearchAsync(b.Id, query, location, ct)));
        var reads = await Task.WhenAll(sites.Distinct().Select(s => ReadAsync(s, now, query, location, ct)));
        foreach (var r in await regionalTask)
        {
            var ofBoard = r.Jobs.Where(j => Matches(j, query, null, remoteOnly)).Select(j => new FeedJobDto(r.BoardId, r.Name, j)).ToList();
            statuses.Add(new FeedSiteStatus(r.Name, r.Status == "Ready" ? "Ready" : "Failed", r.Jobs.Count, ofBoard.Count, r.Message, r.FromCache));
            jobs.AddRange(ofBoard);
        }
        foreach (var (site, jobsOfSite, status) in reads)
        {
            // Adzuna was searched with the words and the place, over full descriptions it sends only a snippet of, so only "remote only" is applied again.
            var matched = jobsOfSite.Where(j => site == FeedSite.Adzuna || KeyedFeeds.Sites.Contains(site) ? !remoteOnly || j.Job.IsRemote : Matches(j.Job, query, location, remoteOnly)).ToList();
            statuses.Add(status with { Matched = matched.Count });
            jobs.AddRange(matched);
        }
        // Best matches first (words in the title count most), then newest; the same title at the same company on two sites is shown once.
        var merged = jobs.OrderByDescending(j => Relevance(j.Job, query)).ThenByDescending(j => j.Job.PostedAt ?? DateTime.MinValue)
            .GroupBy(j => (j.Job.Title.Trim().ToLowerInvariant(), j.Job.CompanyName.Trim().ToLowerInvariant()))
            .Select(g => g.First()).Take(take).ToList();
        return new FeedSearchResult(statuses, merged, now);
    }

    private async Task<(FeedSite Site, List<FeedJobDto> Jobs, FeedSiteStatus Status)> ReadAsync(FeedSite site, DateTime now, string query, string? location, CancellationToken ct)
    {
        var name = Name(site);
        if (site == FeedSite.Adzuna) return await ReadAdzunaAsync(now, query, location, ct);
        if (site == FeedSite.HackerNews) return await ReadHackerNewsAsync(now, query, ct);
        if (KeyedFeeds.Sites.Contains(site))
        {
            if (keyed is null) return (site, [], new(name, "NotConfigured", 0, 0, $"{name} is not configured on the server.", false));
            var (found, status, message, cached) = await keyed.SearchAsync(site, query, location, ct);
            return (site, found.Select(j => new FeedJobDto(site.ToString(), name, j)).ToList(), new(name, status, found.Count, 0, message, cached));
        }
        var fromCache = false;
        IReadOnlyList<string> pages;
        // Himalayas is searched by the person's words, so its answer depends on them and is not shared between searches.
        if (site != FeedSite.Himalayas && Cache.TryGetValue(site, out var hit) && now - hit.At < CacheFor) { pages = hit.Pages; fromCache = true; }
        else
        {
            if (site is FeedSite.TheMuse or FeedSite.Instahyre)
            {
                // No word search: the newest few pages are read together. The first must answer; a later one failing is skipped.
                var answers = site == FeedSite.TheMuse
                    ? await Task.WhenAll(Enumerable.Range(0, ResearchOptions.TheMusePages)
                        .Select(p => fetcher.FetchJsonAsync(ResearchOptions.TheMuseUrl(_limits.TheMuseApiBase, p), ct, ResearchOptions.LargeFeedBytes)))
                    : await Task.WhenAll(Enumerable.Range(0, ResearchOptions.InstahyrePages)
                        .Select(p => fetcher.FetchJsonAsync(ResearchOptions.InstahyreUrl(_limits.InstahyreApiBase, p), ct, ResearchOptions.LargeFeedBytes)));
                if (!answers[0].Ok || answers[0].Content is null)
                    return (site, [], new(name, "Failed", 0, 0, answers[0].FailureReason ?? $"{name} could not be read.", false));
                pages = answers.TakeWhile(a => a.Ok && a.Content is not null).Select(a => a.Content!).ToList();
            }
            else
            {
                var result = site switch
                {
                    FeedSite.Arbeitnow => await fetcher.FetchJsonAsync($"{Base(_limits.ArbeitnowApiBase, ResearchOptions.DefaultArbeitnowApiBase)}/api/job-board-api", ct, ResearchOptions.LargeFeedBytes),
                    FeedSite.Jobicy => await fetcher.FetchJsonAsync($"{Base(_limits.JobicyApiBase, ResearchOptions.DefaultJobicyApiBase)}/api/v2/remote-jobs?count=50", ct),
                    FeedSite.WeWorkRemotely => await fetcher.FetchAsync($"{Base(_limits.WeWorkRemotelyBase, ResearchOptions.DefaultWeWorkRemotelyBase)}/remote-jobs.rss", ct, ResearchOptions.LargeFeedBytes),
                    FeedSite.Himalayas => await fetcher.FetchJsonAsync($"{Base(_limits.HimalayasApiBase, ResearchOptions.DefaultHimalayasApiBase)}/jobs/api/search?q={Uri.EscapeDataString(query)}&limit=60", ct, ResearchOptions.LargeFeedBytes),
                    FeedSite.Remotive => await fetcher.FetchJsonAsync($"{Base(_limits.RemotiveApiBase, ResearchOptions.DefaultRemotiveApiBase)}/api/remote-jobs", ct, ResearchOptions.LargeFeedBytes),
                    FeedSite.WorkingNomads => await fetcher.FetchJsonAsync($"{Base(_limits.WorkingNomadsApiBase, ResearchOptions.DefaultWorkingNomadsApiBase)}/api/exposed_jobs/", ct, ResearchOptions.LargeFeedBytes),
                    _ => await fetcher.FetchJsonAsync($"{Base(_limits.RemoteOkApiBase, ResearchOptions.DefaultRemoteOkApiBase)}/api", ct, ResearchOptions.LargeFeedBytes)
                };
                if (!result.Ok || result.Content is null)
                    return (site, [], new(name, "Failed", 0, 0, result.FailureReason ?? $"{name} could not be read.", false));
                pages = [result.Content];
            }
            if (site != FeedSite.Himalayas) Cache[site] = (now, pages);
        }
        var kind = site switch
        {
            FeedSite.Arbeitnow => SourceKind.Arbeitnow, FeedSite.Jobicy => SourceKind.Jobicy, FeedSite.WeWorkRemotely => SourceKind.WeWorkRemotely,
            FeedSite.Remotive => SourceKind.Remotive, FeedSite.Himalayas => SourceKind.Remotive, FeedSite.TheMuse => SourceKind.TheMuse, FeedSite.Instahyre => SourceKind.Instahyre, FeedSite.WorkingNomads => SourceKind.Remotive, _ => SourceKind.RemoteOk
        };
        var source = new Source(Guid.NewGuid(), Guid.NewGuid(), kind, name, null, null, null, null, now);
        var candidates = new List<Candidate>();
        for (var page = 0; page < pages.Count; page++)
        {
            var text = pages[page];
            var read = site switch
            {
                FeedSite.Arbeitnow => BoardMapping.ArbeitnowCandidates(text, source, parser),
                FeedSite.Jobicy => BoardMapping.JobicyCandidates(text, source, parser),
                FeedSite.WeWorkRemotely => BoardMapping.WeWorkRemotelyCandidates(text, source, parser),
                FeedSite.Himalayas => BoardMapping.HimalayasCandidates(text, source, parser),
                FeedSite.Remotive => BoardMapping.RemotiveCandidates(text, source, parser),
                FeedSite.TheMuse => BoardMapping.TheMuseCandidates(text, source, parser),
                FeedSite.Instahyre => BoardMapping.InstahyreCandidates(text, source),
                FeedSite.WorkingNomads => BoardMapping.WorkingNomadsCandidates(text, source, parser),
                _ => BoardMapping.RemoteOkCandidates(text, source, parser)
            };
            if (read is null && page == 0) return (site, [], new(name, "Failed", 0, 0, $"{name} sent a response that could not be read.", fromCache));
            if (read is null) break;   // a later page that could not be read keeps what the earlier ones gave
            candidates.AddRange(read);
        }
        // A posting can move to the next page between two reads (The Muse), so one id is listed once.
        var jobs = candidates.DistinctBy(c => c.ExternalId ?? c.Title).Select(c => ToDto(site, name, c)).Where(j => j is not null).Select(j => j!).ToList();
        return (site, jobs, new(name, "Ready", jobs.Count, 0, null, fromCache));
    }

    /// <summary>
    /// Adzuna, searched with the person's words in the country of their place (<see cref="AdzunaCountry"/>). Without both server keys it
    /// answers NotConfigured and sends nothing. The request URL carries the keys, so it goes straight to the fetcher and is never logged,
    /// cached by, or put into a message.
    /// </summary>
    private async Task<(FeedSite Site, List<FeedJobDto> Jobs, FeedSiteStatus Status)> ReadAdzunaAsync(DateTime now, string query, string? location, CancellationToken ct)
    {
        const FeedSite site = FeedSite.Adzuna;
        var name = Name(site);
        if (adzuna?.Value is not { Configured: true } keys)
            return (site, [], new(name, "NotConfigured", 0, 0, "Adzuna is not configured on the server.", false));
        var (country, where) = AdzunaCountry.Resolve(location);
        var cacheKey = $"{country}\n{query.Trim().ToLowerInvariant()}\n{where?.ToLowerInvariant()}";
        var fromCache = false;
        string text;
        if (AdzunaCache.TryGetValue(cacheKey, out var hit) && now - hit.At < CacheFor) { text = hit.Text; fromCache = true; }
        else
        {
            var root = Base(_limits.AdzunaApiBase, ResearchOptions.DefaultAdzunaApiBase);
            var result = await fetcher.FetchJsonAsync(JobBoardGatherer.AdzunaSearchUrl(root, country, keys, query, where) + "&content-type=application/json", ct);
            if (!result.Ok || result.Content is null)
                return (site, [], new(name, "Failed", 0, 0, result.StatusCode is 401 or 403
                    ? "Adzuna rejected the server's keys." : result.FailureReason ?? "Adzuna could not be read.", false));
            text = result.Content;
            if (AdzunaCache.Count >= MaxAdzunaCached)
                foreach (var old in AdzunaCache.OrderBy(e => e.Value.At).Take(AdzunaCache.Count - MaxAdzunaCached + 1).Select(e => e.Key).ToList())
                    AdzunaCache.TryRemove(old, out _);
            AdzunaCache[cacheKey] = (now, text);
        }
        var source = new Source(Guid.NewGuid(), Guid.NewGuid(), SourceKind.Adzuna, name, null, null, null, null, now);
        if (BoardMapping.AdzunaCandidates(text, source, parser) is not { } candidates)
            return (site, [], new(name, "Failed", 0, 0, $"{name} sent a response that could not be read.", fromCache));
        var jobs = candidates.Select(c => ToDto(site, name, c)).Where(j => j is not null).Select(j => j!).ToList();
        return (site, jobs, new(name, "Ready", jobs.Count, 0, null, fromCache));
    }

    /// <summary>
    /// Hacker News "Ask HN: Who is hiring?" (monthly, many remote and contract roles from foreign companies), through the free public
    /// Algolia HN API: the newest thread by the whoishiring account, then its top-level comments matching the words. Read only.
    /// </summary>
    private async Task<(FeedSite Site, List<FeedJobDto> Jobs, FeedSiteStatus Status)> ReadHackerNewsAsync(DateTime now, string query, CancellationToken ct)
    {
        const FeedSite site = FeedSite.HackerNews;
        var name = Name(site);
        var root = Base(_limits.HackerNewsApiBase, ResearchOptions.DefaultHackerNewsApiBase);
        var thread = await fetcher.FetchJsonAsync($"{root}/api/v1/search_by_date?tags=story,author_whoishiring&query=who%20is%20hiring&hitsPerPage=1", ct);
        if (!thread.Ok || BoardMapping.HackerNewsThreadId(thread.Content) is not { } storyId)
            return (site, [], new(name, "Failed", 0, 0, thread.FailureReason ?? $"{name} could not be read.", false));
        var comments = await fetcher.FetchJsonAsync($"{root}/api/v1/search?tags=comment,story_{storyId}&query={Uri.EscapeDataString(query)}&hitsPerPage=100", ct, ResearchOptions.LargeFeedBytes);
        if (!comments.Ok || comments.Content is null) return (site, [], new(name, "Failed", 0, 0, comments.FailureReason ?? $"{name} could not be read.", false));
        var source = new Source(Guid.NewGuid(), Guid.NewGuid(), SourceKind.RemoteOk, name, null, null, null, null, now);
        if (BoardMapping.HackerNewsCandidates(comments.Content, storyId, source, parser) is not { } candidates)
            return (site, [], new(name, "Failed", 0, 0, $"{name} sent a response that could not be read.", false));
        var jobs = candidates.Select(c => ToDto(site, name, c)).Where(j => j is not null).Select(j => j!).ToList();
        return (site, jobs, new(name, "Ready", jobs.Count, 0, null, false));
    }

    private static FeedJobDto? ToDto(FeedSite site, string name, Candidate c)
    {
        var url = c.ApplyUrl ?? c.Url;
        if (string.IsNullOrWhiteSpace(url)) return null;
        var remote = string.Equals(c.WorkplaceType, "remote", StringComparison.OrdinalIgnoreCase) || (c.Location ?? string.Empty).Contains("remote", StringComparison.OrdinalIgnoreCase);
        var description = c.Text.Replace("Workplace: Remote", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
        var snippet = description.Length <= 240 ? description : description[..240].TrimEnd() + "…";
        var id = $"{site}:{c.ExternalId ?? c.Title}";
        return new FeedJobDto(site.ToString(), name,
            new JobBoardJobDto(id, c.Title, c.Organization, null, url, c.Location, remote, null, null, null, null, null, c.PostedAt, snippet, description));
    }

    /// <summary>How well a posting fits the words: 4 per word in the title, 2 per word in the company, 1 per word in the text.</summary>
    public static int Relevance(JobBoardJobDto job, string query)
    {
        var score = 0;
        foreach (var word in query.Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (job.Title.Contains(word, StringComparison.OrdinalIgnoreCase)) score += 4;
            if (job.CompanyName.Contains(word, StringComparison.OrdinalIgnoreCase)) score += 2;
            if ((job.Description ?? job.Snippet ?? string.Empty).Contains(word, StringComparison.OrdinalIgnoreCase)) score += 1;
        }
        return score;
    }

    /// <summary>Every word of the query must appear in the title, company or text; the place (if given) must appear in the location or text.</summary>
    public static bool Matches(JobBoardJobDto job, string query, string? location, bool remoteOnly)
    {
        if (remoteOnly && !job.IsRemote) return false;
        var haystack = $"{job.Title} {job.CompanyName} {job.Description ?? job.Snippet}";
        foreach (var word in query.Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (!haystack.Contains(word, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.IsNullOrWhiteSpace(location) && !$"{job.Location} {job.Description}".Contains(location.Trim(), StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private static string Base(string configured, string fallback) => (string.IsNullOrWhiteSpace(configured) ? fallback : configured).TrimEnd('/');
}
