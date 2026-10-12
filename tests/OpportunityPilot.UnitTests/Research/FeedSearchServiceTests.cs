using Microsoft.Extensions.Options;
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

    private static FeedSearchService Service(FakeFetcher fetcher) =>
        new(fetcher, new ContentParser(), Options.Create(new ResearchOptions()), new FixedClock(new DateTimeOffset(2026, 10, 12, 10, 0, 0, TimeSpan.Zero)));

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
    public void Matches_needs_every_word_the_place_and_the_remote_flag()
    {
        var j = new JobBoardJobDto("x", "Sales Manager", "Acme", null, "https://x.test", "London", false, null, null, null, null, null, null, "snippet", "Sell in the UK and France");
        Assert.True(FeedSearchService.Matches(j, "sales manager", "london", false));
        Assert.True(FeedSearchService.Matches(j, "sales france", null, false));
        Assert.False(FeedSearchService.Matches(j, "sales germany", null, false));
        Assert.False(FeedSearchService.Matches(j, "sales", "paris", false));
        Assert.False(FeedSearchService.Matches(j, "sales", null, true));
    }
}
