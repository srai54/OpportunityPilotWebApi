using Microsoft.Extensions.Options;
using OpportunityPilot.Application.JobBoards;
using OpportunityPilot.Application.Research;
using OpportunityPilot.Infrastructure.Research;

namespace OpportunityPilot.UnitTests.Research;

public class RapidApiBoardTests
{
    // The field map is the owner's; these shapes are invented to exercise the mapper, not copied from any real listing.
    private static RapidApiBoardConfig Config() => new()
    {
        Name = "Sample Gulf Board", Host = "sample-board.p.rapidapi.com", Path = "/search", ListPath = "data.jobs",
        Id = "job_id", Title = "job_title", Company = "employer.name", Location = "place", Url = "link", Description = "details", Posted = "posted_on", Remote = "is_remote",
    };

    private const string Json = """{"data":{"jobs":[{"job_id":77,"job_title":"Sales Executive","employer":{"name":"Contoso Gulf"},"place":"Dubai","link":"https://example.test/jobs/77","details":"Sell payroll software to SMEs in the UAE.","posted_on":"2026-10-10T08:00:00Z","is_remote":false},{"job_title":"No link","link":"http://insecure.test/x"}]}}""";

    [Fact]
    public void Maps_through_dotted_paths_and_keeps_only_https_jobs_with_a_title()
    {
        var jobs = RapidApiMapping.Map(Json, "sample", Config())!;
        var job = Assert.Single(jobs);
        Assert.Equal(("sample:77", "Sales Executive", "Contoso Gulf", "Dubai"), (job.ProviderJobId, job.Title, job.CompanyName, job.Location));
        Assert.Equal("https://example.test/jobs/77", job.BoardUrl);
        Assert.Equal(new DateTime(2026, 10, 10, 8, 0, 0, DateTimeKind.Utc), job.PostedAt);
        Assert.False(job.IsRemote);
    }

    [Fact]
    public void A_response_that_does_not_match_the_map_is_reported_not_guessed()
    {
        Assert.Null(RapidApiMapping.Map("""{"other":[]}""", "x", Config()));
        Assert.Null(RapidApiMapping.Map("not json", "x", Config()));
        Assert.Null(RapidApiMapping.Map("", "x", Config()));
    }

    [Fact]
    public void Only_rapidapi_hosts_with_a_path_and_a_key_are_ever_active()
    {
        var good = Config();
        var wrongHost = Config(); wrongHost.Host = "evil.example.com";
        var traversal = Config(); traversal.Host = "x.p.rapidapi.com/../admin";
        var noPath = Config(); noPath.Path = "";
        Assert.True(good.Valid);
        Assert.False(wrongHost.Valid);
        Assert.False(traversal.Valid);
        Assert.False(noPath.Valid);
        var withoutKey = new RapidApiBoardsOptions { Boards = { ["sample"] = good } };
        Assert.False(withoutKey.Configured);
        Assert.Empty(withoutKey.Active);
        var ready = new RapidApiBoardsOptions { Key = "k", Boards = { ["sample"] = good, ["bad"] = wrongHost } };
        Assert.True(ready.Configured);
        Assert.Equal(["sample"], ready.Active.Select(b => b.Key));
    }

    private sealed class FakeRegional(RapidApiBoardResult result) : IRapidApiBoardSearch
    {
        public IReadOnlyList<(string Id, string Name)> Boards => [(result.BoardId, result.Name)];
        public Task<RapidApiBoardResult> SearchAsync(string boardId, string query, string? location, CancellationToken ct) => Task.FromResult(result);
    }

    private sealed class NoFetch : IWebFetcher
    {
        public string? CheckUrl(string url) => null;
        public Task<FetchResult> FetchAsync(string url, CancellationToken ct, int? maxBytes = null) => Task.FromResult(FetchResult.Fail("offline", 1));
        public Task<FetchResult> FetchJsonAsync(string url, CancellationToken ct, int? maxBytes = null) => Task.FromResult(FetchResult.Fail("offline", 1));
        public Task<FetchResult> PostJsonAsync(string url, string jsonBody, CancellationToken ct) => throw new InvalidOperationException();
    }

    [Fact]
    public async Task All_sites_includes_a_configured_regional_board_and_says_when_it_fails()
    {
        var job = new JobBoardJobDto("sample:1", "Sales Executive", "Contoso Gulf", null, "https://example.test/1", "Dubai", false, null, null, null, null, null, null, null, "Sell software");
        var ok = new FeedSearchService(new NoFetch(), new ContentParser(), Options.Create(new ResearchOptions()), TimeProvider.System, new FakeRegional(new("sample", "Sample Gulf Board", "Ready", [job], null, false)));
        var r = await ok.SearchAsync([], "sales", null, false, 20, CancellationToken.None);
        Assert.Equal("Sample Gulf Board", Assert.Single(r.Jobs).SiteName);
        Assert.Equal("Ready", r.Sites.Single(s => s.Site == "Sample Gulf Board").Status);

        var down = new FeedSearchService(new NoFetch(), new ContentParser(), Options.Create(new ResearchOptions()), TimeProvider.System, new FakeRegional(new("sample", "Sample Gulf Board", "Failed", [], "The key is not subscribed to this API.", false)));
        var r2 = await down.SearchAsync([], "sales", null, false, 20, CancellationToken.None);
        Assert.Empty(r2.Jobs);
        Assert.Equal("The key is not subscribed to this API.", r2.Sites.Single().Message);
    }
}
