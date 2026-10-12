using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using OpportunityPilot.Application.Research;
using OpportunityPilot.Application.Research.Boards;
using OpportunityPilot.Domain.Opportunities;
using OpportunityPilot.Domain.Research;

namespace OpportunityPilot.Application.JobBoards;

/// <summary>The free public job feeds that can be searched live, without any key or quota.</summary>
public enum FeedSite { Arbeitnow, Jobicy, WeWorkRemotely, Remotive, RemoteOk }

public sealed record FeedSiteStatus(string Site, string Status, int Listed, int Matched, string? Message, bool FromCache);

public sealed record FeedSearchResult(IReadOnlyList<FeedSiteStatus> Sites, IReadOnlyList<FeedJobDto> Jobs, DateTime ObservedAt);

/// <summary>One posting from a feed, in the same shape the paid boards use plus the site it came from.</summary>
public sealed record FeedJobDto(string Site, string SiteName, JobBoardJobDto Job);

/// <summary>
/// "Search all": reads the free public feeds (Arbeitnow, Jobicy, We Work Remotely, Remotive, Remote OK), keeps each feed's
/// text for 30 minutes so repeated searches cost nothing, and filters it by the person's words and place. Read-only.
/// </summary>
public sealed class FeedSearchService(IWebFetcher fetcher, IContentParser parser, IOptions<ResearchOptions> research, TimeProvider clock, IRapidApiBoardSearch? regional = null)
{
    public static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(30);
    public const int MaxTake = 100;
    private static readonly ConcurrentDictionary<FeedSite, (DateTime At, string Text)> Cache = new();
    private readonly ResearchOptions _limits = research.Value;

    public static string Name(FeedSite site) => site switch
    {
        FeedSite.Arbeitnow => "Arbeitnow",
        FeedSite.Jobicy => "Jobicy",
        FeedSite.WeWorkRemotely => "We Work Remotely",
        FeedSite.Remotive => "Remotive",
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
        var reads = await Task.WhenAll(sites.Distinct().Select(s => ReadAsync(s, now, ct)));
        foreach (var r in await regionalTask)
        {
            var ofBoard = r.Jobs.Where(j => Matches(j, query, null, remoteOnly)).Select(j => new FeedJobDto(r.BoardId, r.Name, j)).ToList();
            statuses.Add(new FeedSiteStatus(r.Name, r.Status == "Ready" ? "Ready" : "Failed", r.Jobs.Count, ofBoard.Count, r.Message, r.FromCache));
            jobs.AddRange(ofBoard);
        }
        foreach (var (site, jobsOfSite, status) in reads)
        {
            var matched = jobsOfSite.Where(j => Matches(j.Job, query, location, remoteOnly)).ToList();
            statuses.Add(status with { Matched = matched.Count });
            jobs.AddRange(matched);
        }
        // Best matches first (words in the title count most), then newest; the same title at the same company on two sites is shown once.
        var merged = jobs.OrderByDescending(j => Relevance(j.Job, query)).ThenByDescending(j => j.Job.PostedAt ?? DateTime.MinValue)
            .GroupBy(j => (j.Job.Title.Trim().ToLowerInvariant(), j.Job.CompanyName.Trim().ToLowerInvariant()))
            .Select(g => g.First()).Take(take).ToList();
        return new FeedSearchResult(statuses, merged, now);
    }

    private async Task<(FeedSite Site, List<FeedJobDto> Jobs, FeedSiteStatus Status)> ReadAsync(FeedSite site, DateTime now, CancellationToken ct)
    {
        var name = Name(site);
        var fromCache = false;
        string? text;
        if (Cache.TryGetValue(site, out var hit) && now - hit.At < CacheFor) { text = hit.Text; fromCache = true; }
        else
        {
            var result = site switch
            {
                FeedSite.Arbeitnow => await fetcher.FetchJsonAsync($"{Base(_limits.ArbeitnowApiBase, ResearchOptions.DefaultArbeitnowApiBase)}/api/job-board-api", ct, ResearchOptions.LargeFeedBytes),
                FeedSite.Jobicy => await fetcher.FetchJsonAsync($"{Base(_limits.JobicyApiBase, ResearchOptions.DefaultJobicyApiBase)}/api/v2/remote-jobs?count=50", ct),
                FeedSite.WeWorkRemotely => await fetcher.FetchAsync($"{Base(_limits.WeWorkRemotelyBase, ResearchOptions.DefaultWeWorkRemotelyBase)}/remote-jobs.rss", ct, ResearchOptions.LargeFeedBytes),
                FeedSite.Remotive => await fetcher.FetchJsonAsync($"{Base(_limits.RemotiveApiBase, ResearchOptions.DefaultRemotiveApiBase)}/api/remote-jobs", ct, ResearchOptions.LargeFeedBytes),
                _ => await fetcher.FetchJsonAsync($"{Base(_limits.RemoteOkApiBase, ResearchOptions.DefaultRemoteOkApiBase)}/api", ct, ResearchOptions.LargeFeedBytes)
            };
            if (!result.Ok || result.Content is null)
                return (site, [], new(name, "Failed", 0, 0, result.FailureReason ?? $"{name} could not be read.", false));
            text = result.Content;
            Cache[site] = (now, text);
        }
        var kind = site switch { FeedSite.Arbeitnow => SourceKind.Arbeitnow, FeedSite.Jobicy => SourceKind.Jobicy, FeedSite.WeWorkRemotely => SourceKind.WeWorkRemotely, FeedSite.Remotive => SourceKind.Remotive, _ => SourceKind.RemoteOk };
        var source = new Source(Guid.NewGuid(), Guid.NewGuid(), kind, name, null, null, null, null, now);
        var candidates = site switch
        {
            FeedSite.Arbeitnow => BoardMapping.ArbeitnowCandidates(text, source, parser),
            FeedSite.Jobicy => BoardMapping.JobicyCandidates(text, source, parser),
            FeedSite.WeWorkRemotely => BoardMapping.WeWorkRemotelyCandidates(text, source, parser),
            FeedSite.Remotive => BoardMapping.RemotiveCandidates(text, source, parser),
            _ => BoardMapping.RemoteOkCandidates(text, source, parser)
        };
        if (candidates is null) return (site, [], new(name, "Failed", 0, 0, $"{name} sent a response that could not be read.", fromCache));
        var jobs = candidates.Select(c => ToDto(site, name, c)).Where(j => j is not null).Select(j => j!).ToList();
        return (site, jobs, new(name, "Ready", jobs.Count, 0, null, fromCache));
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
