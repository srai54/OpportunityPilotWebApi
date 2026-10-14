using System.Text.Json;
using Microsoft.Extensions.Options;
using OpportunityPilot.Application.Configuration;
using OpportunityPilot.Application.JobBoards;
using OpportunityPilot.Application.Research;
using OpportunityPilot.Infrastructure.Research;

namespace OpportunityPilot.UnitTests.Research;

public class FeedSearchServiceTests
{
    private sealed class FakeFetcher(Func<string, FetchResult> respond) : IWebFetcher
    {
        public List<string> Urls { get; } = [];
        public string? CheckUrl(string url) => null;
        public Task<FetchResult> FetchAsync(string url, CancellationToken ct, int? maxBytes = null) { Urls.Add(url); return Task.FromResult(respond(url)); }
        public Task<FetchResult> FetchJsonAsync(string url, CancellationToken ct, int? maxBytes = null) { Urls.Add(url); return Task.FromResult(respond(url)); }
        public Task<FetchResult> PostJsonAsync(string url, string jsonBody, CancellationToken ct) => throw new InvalidOperationException();
    }

    private static FetchResult Ok(string body, string type) => new(true, body, type, null, null, 1);

    private const string Arbeitnow = """{"data":[{"slug":"a-1","company_name":"Acme","title":"React Developer","description":"<p>Build UIs in React.</p>","remote":true,"url":"https://www.arbeitnow.com/jobs/a-1","tags":[],"job_types":[],"location":"Berlin","created_at":1791795600},{"slug":"a-2","company_name":"Globex","title":"Accountant","description":"<p>Books.</p>","remote":false,"url":"https://www.arbeitnow.com/jobs/a-2","tags":[],"job_types":[],"location":"Munich","created_at":1791709200}]}""";

    private const string Rss = """<?xml version="1.0"?><rss><channel><item><title>Acme: React Developer</title><region>Anywhere</region><description>&lt;p&gt;Same job on another site&lt;/p&gt;</description><pubDate>Mon, 12 Oct 2026 06:00:00 +0000</pubDate><link>https://weworkremotely.com/remote-jobs/acme-react</link><guid>g1</guid></item></channel></rss>""";

    private static FeedSearchService Service(FakeFetcher fetcher, AdzunaOptions? adzuna = null) =>
        new(fetcher, new ContentParser(), Options.Create(new ResearchOptions()), new FixedClock(new DateTimeOffset(2026, 10, 12, 10, 0, 0, TimeSpan.Zero)),
            adzuna: Options.Create(adzuna ?? new AdzunaOptions()));

    private const string FakeAdzunaKey = "fake-adzuna-key-feed-9c1e";

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }

    private static FetchResult Route(string url) =>
        url.Contains("arbeitnow") ? Ok(Arbeitnow, "application/json") :
        url.Contains("weworkremotely") ? Ok(Rss, "application/rss+xml") :
        FetchResult.Fail("down", 1);

    [Fact]
    public async Task Filters_by_every_word_merges_sites_and_shows_a_duplicate_once()
    {
        var fetcher = new FakeFetcher(Route);
        var r = await Service(fetcher).SearchAsync([FeedSite.Arbeitnow, FeedSite.WeWorkRemotely], "react developer", null, false, 50, CancellationToken.None);
        var job = Assert.Single(r.Jobs);   // Acme / React Developer appears on both sites but is listed once; the accountant does not match
        Assert.Equal("React Developer", job.Job.Title);
        Assert.All(r.Sites, s => Assert.Equal("Ready", s.Status));
        Assert.Equal(1, r.Sites.Single(s => s.Site == "Arbeitnow").Matched);
        Assert.Equal(2, r.Sites.Single(s => s.Site == "Arbeitnow").Listed);
    }

    [Fact]
    public async Task A_failing_site_is_reported_and_the_others_still_answer()
    {
        var r = await Service(new FakeFetcher(Route)).SearchAsync([FeedSite.Arbeitnow, FeedSite.Jobicy], "react", null, false, 50, CancellationToken.None);
        Assert.Equal("Failed", r.Sites.Single(s => s.Site == "Jobicy").Status);
        Assert.Equal("down", r.Sites.Single(s => s.Site == "Jobicy").Message);
        Assert.Single(r.Jobs);
    }

    [Fact]
    public async Task The_Muse_reads_its_newest_pages_and_keeps_the_postings_with_every_word()
    {
        var empty = """{"page": 2, "page_count": 20708, "results": []}""";
        var fetcher = new FakeFetcher(url => url.Contains("themuse.com/api/public/jobs?page=0&descending=true") ? Ok(BoardMappingTests.TheMusePage, "application/json")
            : url.Contains("themuse.com") ? Ok(empty, "application/json") : FetchResult.Fail("down", 1));

        var r = await Service(fetcher).SearchAsync([FeedSite.TheMuse], "software engineer", null, false, 50, CancellationToken.None);

        Assert.Equal(ResearchOptions.TheMusePages, fetcher.Urls.Count(u => u.Contains("themuse.com")));
        var site = Assert.Single(r.Sites);
        Assert.Equal(("The Muse", "Ready", 3, 1), (site.Site, site.Status, site.Listed, site.Matched));
        var job = Assert.Single(r.Jobs);
        Assert.Equal(("TheMuse", "The Muse", "Senior, Software Engineer (Backend)", "Walmart"), (job.Site, job.SiteName, job.Job.Title, job.Job.CompanyName));
        Assert.Equal("https://www.themuse.com/jobs/walmart/senior-software-engineer-backend-5ff764", job.Job.BoardUrl);
    }

    [Fact]
    public async Task Adzuna_without_both_keys_is_not_configured_sends_nothing_and_is_left_out_of_all_sites()
    {
        var fetcher = new FakeFetcher(_ => throw new InvalidOperationException("must not fetch"));
        var service = Service(fetcher, new AdzunaOptions { AppId = "id-only" });

        var r = await service.SearchAsync([FeedSite.Adzuna], "react developer", "Berlin", false, 50, CancellationToken.None);

        Assert.Empty(fetcher.Urls);
        var site = Assert.Single(r.Sites);
        Assert.Equal(("Adzuna", "NotConfigured", "Adzuna is not configured on the server."), (site.Site, site.Status, site.Message));
        Assert.False(service.AdzunaConfigured);
        Assert.DoesNotContain(FeedSite.Adzuna, service.DefaultSites);
        Assert.Contains(FeedSite.TheMuse, service.DefaultSites);
        Assert.Contains(FeedSite.Adzuna, Service(fetcher, new AdzunaOptions { AppId = "fake-id", AppKey = FakeAdzunaKey }).DefaultSites);
    }

    [Fact]
    public async Task Adzuna_with_keys_searches_the_country_of_the_place_and_never_shows_the_key()
    {
        var fetcher = new FakeFetcher(url => url.Contains("adzuna") ? Ok(BoardMappingTests.AdzunaWithPay, "application/json") : FetchResult.Fail("down", 1));

        var r = await Service(fetcher, new AdzunaOptions { AppId = "fake-id", AppKey = FakeAdzunaKey })
            .SearchAsync([FeedSite.Adzuna], "backend engineer feedtest", "Berlin", false, 50, CancellationToken.None);

        var url = Assert.Single(fetcher.Urls);
        Assert.Equal($"https://api.adzuna.com/v1/api/jobs/de/search/1?app_id=fake-id&app_key={FakeAdzunaKey}&what=backend%20engineer%20feedtest&where=Berlin" +
                     "&results_per_page=50&content-type=application/json", url);
        Assert.Equal(("Ready", 1, 1), (r.Sites.Single().Status, r.Sites.Single().Listed, r.Sites.Single().Matched));
        var job = Assert.Single(r.Jobs);                       // Adzuna searched the words and place itself, so a snippet without "feedtest" still counts
        Assert.Equal(("Adzuna", "Backend Engineer", "https://www.adzuna.de/land/ad/4870000001?se=x"), (job.SiteName, job.Job.Title, job.Job.BoardUrl));
        Assert.DoesNotContain(FakeAdzunaKey, JsonSerializer.Serialize(r));
    }

    [Fact]
    public async Task Adzuna_rejecting_the_keys_says_so_without_echoing_them()
    {
        var fetcher = new FakeFetcher(_ => FetchResult.Fail("The site answered 401. The page may need a login.", 1, 401));

        var r = await Service(fetcher, new AdzunaOptions { AppId = "fake-id", AppKey = FakeAdzunaKey })
            .SearchAsync([FeedSite.Adzuna], "rejected keys feedtest", null, false, 50, CancellationToken.None);

        Assert.StartsWith("https://api.adzuna.com/v1/api/jobs/in/search/1?", Assert.Single(fetcher.Urls));   // no place: India, no where
        Assert.DoesNotContain("&where=", fetcher.Urls[0]);
        Assert.Equal(("Failed", "Adzuna rejected the server's keys."), (r.Sites.Single().Status, r.Sites.Single().Message));
        Assert.DoesNotContain(FakeAdzunaKey, JsonSerializer.Serialize(r));
    }

    [Fact]
    public void Matches_needs_every_word_the_place_and_the_remote_flag()
    {
        var j = new JobBoardJobDto("x", "Sales Manager", "Acme", null, "https://x.test", "London", false, null, null, null, null, null, null, "snippet", "Sell in the UK and France");
        Assert.True(FeedSearchService.Matches(j, "sales manager", "london", false));
        Assert.True(FeedSearchService.Matches(j, "sales france", null, false));
        Assert.False(FeedSearchService.Matches(j, "sales germany", null, false));
        Assert.False(FeedSearchService.Matches(j, "sales", "paris", false));
        Assert.False(FeedSearchService.Matches(j, "sales", null, true));
    }

    [Fact]
    public void Relevance_ranks_a_title_match_above_a_mention_in_the_text()
    {
        var inTitle = new JobBoardJobDto("a", "Sales Manager", "Acme", null, "https://x.test", null, false, null, null, null, null, null, null, null, "x");
        var inText = new JobBoardJobDto("b", "Product Lead", "Acme", null, "https://x.test", null, false, null, null, null, null, null, null, null, "works with the sales manager");
        Assert.True(FeedSearchService.Relevance(inTitle, "sales manager") > FeedSearchService.Relevance(inText, "sales manager"));
    }
}
