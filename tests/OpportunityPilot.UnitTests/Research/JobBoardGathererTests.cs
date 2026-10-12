using Microsoft.Extensions.Options;
using OpportunityPilot.Application.Campaigns;
using OpportunityPilot.Application.Configuration;
using OpportunityPilot.Application.JobBoards;
using OpportunityPilot.Application.Research;
using OpportunityPilot.Application.Research.Boards;
using OpportunityPilot.Domain.Common;
using OpportunityPilot.Domain.Research;
using OpportunityPilot.Infrastructure.Research;

namespace OpportunityPilot.UnitTests.Research;

public class JobBoardGathererTests
{
    private static readonly DateTime T0 = new(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);
    private const string FakeKey = "fake-adzuna-key-0000";

    /// <summary>Answers JSON fetches from a script and records every URL asked for. Page fetches are a test failure.</summary>
    private sealed class ScriptedFetcher(Func<string, FetchResult> respond) : IWebFetcher
    {
        public List<string> Urls { get; } = [];
        public List<(string Url, string Body)> Posts { get; } = [];
        public string? CheckUrl(string url) => null;
        public Task<FetchResult> FetchAsync(string url, CancellationToken ct, int? maxBytes = null)
        {
            Urls.Add(url);
            return Task.FromResult(respond(url));
        }

        public Task<FetchResult> FetchJsonAsync(string url, CancellationToken ct, int? maxBytes = null)
        {
            Urls.Add(url);
            return Task.FromResult(respond(url));
        }

        public Task<FetchResult> PostJsonAsync(string url, string jsonBody, CancellationToken ct)
        {
            Posts.Add((url, jsonBody));
            return Task.FromResult(respond(url));
        }
    }

    private static FetchResult Json(string body) => new(true, body, "application/json", null, null, 1);

    /// <summary>Answers JSearch searches from a script and records every request; "configured" mirrors Jsearch:Key.</summary>
    private sealed class ScriptedBoards(bool configured, Func<JobBoardSearchRequest, JobBoardSearchResult>? respond = null) : IJobBoardSearch
    {
        public List<JobBoardSearchRequest> Requests { get; } = [];
        public bool Configured => configured;
        public Task<JobBoardSearchResult> SearchAsync(JobBoardSearchRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(respond?.Invoke(request) ?? new(request.Board, "Ready", [], 0, null, T0, false, "test"));
        }
    }

    private static JobBoardGatherer Gatherer(IWebFetcher fetcher, AdzunaOptions? adzuna = null, ResearchOptions? research = null,
        IJobBoardSearch? boards = null) =>
        new(fetcher, new ContentParser(), Options.Create(research ?? new ResearchOptions()), Options.Create(adzuna ?? new AdzunaOptions()),
            boards ?? new ScriptedBoards(false));

    private static JobBoardJobDto Posting(string id, string title, string company = "Acme", string? url = null) =>
        new(id, title, company, null, url ?? $"https://www.indeed.com/viewjob?jk={id}", "Pune, MH, IN", false, "FULLTIME",
            null, null, null, null, T0, "C# services", "We build C# services on Azure.");

    private static Source SearchSource(string board) => SourceOf(SourceKind.JobSearch, board, $"{board} search");

    private static Task<BoardGathered> Search(IJobBoardSearch boards, string board, CampaignCriteria criteria,
        OpportunityMode mode = OpportunityMode.Job, int room = 100) =>
        Gatherer(NoFetch, boards: boards).JobSearchAsync(SearchSource(board), criteria, mode, room, 50, CancellationToken.None);

    private static readonly IWebFetcher NoFetch = new ScriptedFetcher(_ => throw new InvalidOperationException("Job search must not use the fetcher."));

    [Fact]
    public async Task Job_search_without_a_jsearch_key_fails_with_a_plain_reason_and_calls_nothing()
    {
        var boards = new ScriptedBoards(false);
        var result = await Search(boards, "Indeed", DotNet);
        Assert.Equal(SourceStatus.Failed, result.Status);
        Assert.Contains("Jsearch:Key", result.SafeError);
        Assert.Empty(boards.Requests);
    }

    [Fact]
    public async Task Indeed_searches_each_keyword_in_the_first_place_dedupes_and_keeps_the_indeed_link()
    {
        var boards = new ScriptedBoards(true, r => new(r.Board, "Ready",
            r.Query == ".NET" ? [Posting("a", ".NET Developer"), Posting("b", "C# Engineer")] : [Posting("b", "C# Engineer")],
            10, null, T0, false, "test"));
        var criteria = new CampaignCriteria { Keywords = [".NET", "C#"], Locations = ["Remote", "Pune"] };
        var result = await Search(boards, "Indeed", criteria);

        Assert.Equal(SourceStatus.Ok, result.Status);
        Assert.Equal([".NET", "C#"], boards.Requests.Select(r => r.Query));
        Assert.All(boards.Requests, r => { Assert.Equal(JobBoard.Indeed, r.Board); Assert.Equal("Pune", r.Location); Assert.False(r.RemoteOnly); });
        Assert.Equal(["a", "b"], result.Items.Select(i => i.ExternalId));
        Assert.All(result.Items, i => Assert.Equal(OpportunityPilot.Domain.Opportunities.JobPlatform.Indeed, i.Platform));
        Assert.Equal(2, result.Requests);
        Assert.Contains("2 Indeed postings found, 2 read", result.Message);
    }

    [Fact]
    public async Task LinkedIn_postings_keep_the_linkedin_job_id_so_the_agent_can_apply()
    {
        var boards = new ScriptedBoards(true, r => new(r.Board, "Ready",
            [Posting("js-1", ".NET Developer", url: "https://www.linkedin.com/jobs/view/net-developer-at-acme-4012345678"),
             Posting("js-2", "C# Engineer", url: "https://in.linkedin.com/jobs/view/4099999999/")], 2, null, T0, false, "test"));
        var result = await Search(boards, "LinkedIn", DotNet);
        Assert.Equal(JobBoard.LinkedIn, Assert.Single(boards.Requests).Board);
        Assert.Equal(["4012345678", "4099999999"], result.Items.Select(i => i.ExternalId));
        Assert.All(result.Items, i => Assert.Equal(OpportunityPilot.Domain.Opportunities.JobPlatform.LinkedIn, i.Platform));
        Assert.Equal("https://www.linkedin.com/jobs/view/4012345678/", result.Items[0].ApplyUrl);
    }

    [Fact]
    public async Task Seek_postings_are_seek_jobs()
    {
        var boards = new ScriptedBoards(true, r => new(r.Board, "Ready", [Posting("s1", ".NET Developer", url: "https://www.seek.com.au/job/1")], 1, null, T0, false, "test"));
        var result = await Search(boards, "Seek", DotNet);
        Assert.Equal(JobBoard.Seek, boards.Requests[0].Board);
        Assert.Equal(OpportunityPilot.Domain.Opportunities.JobPlatform.Seek, Assert.Single(result.Items).Platform);
    }

    [Fact]
    public async Task Sales_campaigns_get_one_hiring_company_per_employer_with_postings_as_evidence()
    {
        var boards = new ScriptedBoards(true, r => new(r.Board, "Ready",
            [Posting("a", "React Developer", "Acme"), Posting("b", "Node Engineer", "ACME"), Posting("c", "QA Lead", "Beta")], 3, null, T0, false, "test"));
        var criteria = new CampaignCriteria { Signals = ["hiring React developers"], Locations = ["Pune"] };
        var result = await Search(boards, "Indeed", criteria, OpportunityMode.Customer);

        Assert.Equal(["hiring React developers"], boards.Requests.Select(r => r.Query));
        Assert.Equal(["Acme", "Beta"], result.Items.Select(i => i.Organization));
        var acme = result.Items[0];
        Assert.Null(acme.Platform);
        Assert.Contains("Hiring on Indeed: React Developer", acme.Text);
        Assert.Contains("Hiring on Indeed: Node Engineer", acme.Text);
        Assert.Equal(2, acme.Links.Count);
        Assert.Contains("2 hiring companies from 3 Indeed postings", result.Message);
    }

    [Fact]
    public async Task Remote_only_locations_search_remote_and_cached_searches_cost_no_fetch()
    {
        var boards = new ScriptedBoards(true, r => new(r.Board, "Ready", [Posting("a", ".NET Developer")], 1, null, T0, true, "test"));
        var result = await Search(boards, "Indeed", new CampaignCriteria { Keywords = [".NET"], Locations = ["Remote"] });
        Assert.True(Assert.Single(boards.Requests).RemoteOnly);
        Assert.Null(boards.Requests[0].Location);
        Assert.Equal(0, result.Requests);
        Assert.Contains("reused", result.Message);
    }

    [Fact]
    public async Task Nothing_to_search_for_is_skipped()
    {
        var boards = new ScriptedBoards(true);
        var result = await Search(boards, "Indeed", new CampaignCriteria());
        Assert.Equal(SourceStatus.Skipped, result.Status);
        Assert.Empty(boards.Requests);
    }

    [Fact]
    public async Task Provider_failure_is_reported()
    {
        var boards = new ScriptedBoards(true, r => new(r.Board, "Failed", [], 0, "The JSearch monthly or hourly quota is used up.", T0, false, "test"));
        var result = await Search(boards, "Indeed", DotNet);
        Assert.Equal(SourceStatus.Failed, result.Status);
        Assert.Contains("quota", result.SafeError);
    }

    private static Source SourceOf(SourceKind kind, string? url, string label) =>
        new(Guid.NewGuid(), Guid.NewGuid(), kind, label, url, null, null, null, T0);

    private static Task<BoardGathered> Gather(JobBoardGatherer g, Source source, CampaignCriteria criteria, int room = 100, int fetchesLeft = 50,
        Func<CancellationToken, Task<bool>>? keepGoing = null) =>
        g.GatherAsync(source, criteria, room, fetchesLeft, keepGoing ?? (_ => Task.FromResult(true)), CancellationToken.None);

    private static string GreenhouseBoard(int jobs) =>
        "{\"jobs\":[" + string.Join(",", Enumerable.Range(1, jobs).Select(i =>
            $"{{\"id\":{i},\"title\":\"{(i % 2 == 0 ? ".NET Engineer" : "Sales Lead")} {i}\",\"absolute_url\":\"https://boards.example/{i}\"," +
            $"\"location\":{{\"name\":\"Pune\"}},\"company_name\":\"Acme\",\"updated_at\":\"2026-09-{(i % 28) + 1:00}T00:00:00Z\"}}")) + "]}";

    private static readonly CampaignCriteria DotNet = new() { Keywords = [".NET"], RequiredSkills = ["C#"] };

    [Fact]
    public async Task Greenhouse_lists_once_prefilters_titles_and_reads_only_matching_descriptions()
    {
        var fetcher = new ScriptedFetcher(url => url.EndsWith("/jobs")
            ? Json(GreenhouseBoard(6))
            : Json("{\"content\":\"&lt;p&gt;C# services&lt;/p&gt;\"}"));
        var source = SourceOf(SourceKind.Greenhouse, "acme", "Greenhouse board acme");

        var result = await Gather(Gatherer(fetcher), source, DotNet);

        Assert.Equal(SourceStatus.Ok, result.Status);
        Assert.Equal("https://boards-api.greenhouse.io/v1/boards/acme/jobs", fetcher.Urls[0]);
        Assert.Equal(4, fetcher.Urls.Count);                                       // list + the 3 ".NET Engineer" jobs
        Assert.All(fetcher.Urls.Skip(1), u => Assert.Matches(@"/v1/boards/acme/jobs/(2|4|6)$", u));
        Assert.Equal(3, result.Items.Count);
        Assert.Equal(4, result.Requests);
        Assert.All(result.Items, c => Assert.Equal("C# services", c.Text));
        Assert.Equal("Greenhouse board acme: 6 jobs listed, 3 matched your keywords, 3 read.", result.Message);
    }

    [Fact]
    public async Task Greenhouse_stops_at_the_fetch_budget_and_says_so()
    {
        var fetcher = new ScriptedFetcher(url => url.EndsWith("/jobs") ? Json(GreenhouseBoard(10)) : Json("{\"content\":\"C#\"}"));

        var result = await Gather(Gatherer(fetcher, research: new ResearchOptions { MaxFetches = 3 }),
            SourceOf(SourceKind.Greenhouse, "acme", "Greenhouse board acme"), DotNet, fetchesLeft: 3);

        Assert.Equal(3, fetcher.Urls.Count);                                       // list + 2 details, then the budget is spent
        Assert.Equal(2, result.Items.Count);
        Assert.Equal(3, result.Requests);
        Assert.Contains("5 matched your keywords, 2 read. Stopped at the run's limit of 3 fetches.", result.Message);
        Assert.Equal(EventLevel.Warning, result.Level);
    }

    [Fact]
    public async Task Greenhouse_stops_at_the_candidate_room()
    {
        var fetcher = new ScriptedFetcher(url => url.EndsWith("/jobs") ? Json(GreenhouseBoard(10)) : Json("{\"content\":\"C#\"}"));

        var result = await Gather(Gatherer(fetcher), SourceOf(SourceKind.Greenhouse, "acme", "Greenhouse board acme"), DotNet, room: 1);

        Assert.Single(result.Items);
        Assert.Equal(2, fetcher.Urls.Count);
        Assert.Contains("Stopped at the run's limit of 100 candidates", result.Message);
    }

    [Fact]
    public async Task Greenhouse_keeps_the_run_alive_between_description_reads_and_stops_on_cancel()
    {
        var fetcher = new ScriptedFetcher(url => url.EndsWith("/jobs") ? Json(GreenhouseBoard(60)) : Json("{\"content\":\"C#\"}"));
        var pings = 0;

        var result = await Gather(Gatherer(fetcher), SourceOf(SourceKind.Greenhouse, "acme", "Greenhouse board acme"), DotNet,
            keepGoing: _ => Task.FromResult(++pings < 2));

        Assert.Equal(2, pings);                                                    // after 10 reads (continue), after 20 (cancelled)
        Assert.Equal(20, result.Items.Count);
        Assert.Contains("Stopped at a cancel request.", result.Message);
    }

    [Fact]
    public async Task Greenhouse_keeps_a_job_whose_description_failed_and_reports_it()
    {
        var fetcher = new ScriptedFetcher(url => url.EndsWith("/jobs") ? Json(GreenhouseBoard(2)) : FetchResult.Fail("The site answered 500.", 4, 500));

        var result = await Gather(Gatherer(fetcher), SourceOf(SourceKind.Greenhouse, "acme", "Greenhouse board acme"), DotNet);

        var only = Assert.Single(result.Items);
        Assert.Equal("", only.Text);
        Assert.Equal(5, result.Requests);                                          // retries count against the budget
        Assert.Contains("1 description could not be loaded", result.Message);
    }

    [Theory]
    [InlineData(404, "No public Greenhouse board was found for \"acme\". Check the board token.")]
    [InlineData(503, "The site answered 503.")]
    public async Task A_greenhouse_board_that_cannot_be_listed_fails_with_a_safe_reason(int status, string reason)
    {
        var fetcher = new ScriptedFetcher(_ => FetchResult.Fail($"The site answered {status}.", 1, status));

        var result = await Gather(Gatherer(fetcher), SourceOf(SourceKind.Greenhouse, "acme", "Greenhouse board acme"), DotNet);

        Assert.Equal(SourceStatus.Failed, result.Status);
        Assert.Equal(reason, result.SafeError);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task A_greenhouse_body_of_the_wrong_shape_fails_and_an_empty_board_is_skipped()
    {
        var wrong = await Gather(Gatherer(new ScriptedFetcher(_ => Json("{\"error\":\"x\"}"))),
            SourceOf(SourceKind.Greenhouse, "acme", "G"), DotNet);
        var empty = await Gather(Gatherer(new ScriptedFetcher(_ => Json("{\"jobs\":[]}"))),
            SourceOf(SourceKind.Greenhouse, "acme", "G"), DotNet);

        Assert.Equal((SourceStatus.Failed, "Greenhouse sent a response that could not be read."), (wrong.Status, wrong.SafeError));
        Assert.Equal(SourceStatus.Skipped, empty.Status);
    }

    [Fact]
    public async Task A_board_source_with_no_fetches_left_is_skipped_without_a_request()
    {
        var fetcher = new ScriptedFetcher(_ => throw new InvalidOperationException("must not fetch"));

        var greenhouse = await Gather(Gatherer(fetcher), SourceOf(SourceKind.Greenhouse, "acme", "G"), DotNet, fetchesLeft: 0);
        var lever = await Gather(Gatherer(fetcher), SourceOf(SourceKind.Lever, "acme", "L"), DotNet, fetchesLeft: 0);

        Assert.Equal(SourceStatus.Skipped, greenhouse.Status);
        Assert.Equal(SourceStatus.Skipped, lever.Status);
        Assert.Empty(fetcher.Urls);
    }

    [Fact]
    public async Task Workday_searches_public_site_then_maps_job_details()
    {
        var fetcher = new ScriptedFetcher(url => url.EndsWith("/jobs", StringComparison.Ordinal)
            ? Json("""{"total":1,"jobPostings":[{"title":"Platform Engineer","externalPath":"/job/India/Platform-Engineer_R123","locationsText":"Bengaluru","bulletFields":["R123"]}]}""")
            : Json("""{"jobPostingInfo":{"jobDescription":"<p>Build C# services.</p>","location":"Bengaluru, India","startDate":"2026-10-01","timeType":"Full time","jobReqId":"R123"}}"""));
        var source = SourceOf(SourceKind.Workday, "acme.wd5/External", "Workday careers");

        var result = await Gather(Gatherer(fetcher), source, DotNet);

        Assert.Equal(SourceStatus.Ok, result.Status);
        Assert.Single(fetcher.Posts);
        Assert.Contains("/wday/cxs/acme/External/jobs", fetcher.Posts[0].Url);
        Assert.Contains(".NET", fetcher.Posts[0].Body);
        var item = Assert.Single(result.Items);
        Assert.Equal(OpportunityPilot.Domain.Opportunities.JobPlatform.Workday, item.Platform);
        Assert.Equal("R123", item.ExternalId);
        Assert.Equal("Build C# services.", item.Text);
        Assert.Equal("https://acme.wd5.myworkdayjobs.com/External/job/India/Platform-Engineer_R123", item.ApplyUrl);
    }

    [Fact]
    public async Task Lever_reads_one_page_of_up_to_100_postings_within_the_room()
    {
        var fetcher = new ScriptedFetcher(_ => Json(BoardMappingTests.LeverPostings));

        var result = await Gather(Gatherer(fetcher), SourceOf(SourceKind.Lever, "acme-labs", "Lever company acme-labs"), DotNet, room: 1);

        Assert.Equal(["https://api.lever.co/v0/postings/acme-labs?mode=json&limit=100"], fetcher.Urls);
        Assert.Single(result.Items);
        Assert.Equal("Lever company acme-labs: 2 postings listed, 1 read. Stopped at the run's limit of 100 candidates.", result.Message);
    }

    [Fact]
    public async Task An_unknown_lever_company_fails_with_a_safe_reason()
    {
        var result = await Gather(Gatherer(new ScriptedFetcher(_ => FetchResult.Fail("The site answered 404.", 1, 404))),
            SourceOf(SourceKind.Lever, "nobody", "Lever company nobody"), DotNet);

        Assert.Equal(SourceStatus.Failed, result.Status);
        Assert.Equal("No public Lever postings were found for \"nobody\". Check the company slug.", result.SafeError);
    }

    [Fact]
    public async Task Adzuna_without_keys_fails_safely_without_any_request()
    {
        var fetcher = new ScriptedFetcher(_ => throw new InvalidOperationException("must not fetch"));

        var result = await Gather(Gatherer(fetcher, new AdzunaOptions { AppId = "id-only" }), SourceOf(SourceKind.Adzuna, null, "Adzuna search"), DotNet);

        Assert.Equal(SourceStatus.Failed, result.Status);
        Assert.Equal("Adzuna is not configured on the server.", result.SafeError);
        Assert.Equal(0, result.Requests);
        Assert.Empty(fetcher.Urls);
    }

    [Fact]
    public async Task Adzuna_searches_at_most_three_keywords_in_the_first_non_remote_location_and_never_echoes_the_key()
    {
        var fetcher = new ScriptedFetcher(_ => Json(BoardMappingTests.AdzunaResults));
        var criteria = new CampaignCriteria { Keywords = [".NET developer", "C#", "backend", "fourth"], Locations = ["Remote", "Pune"] };

        var result = await Gather(Gatherer(fetcher, new AdzunaOptions { AppId = "fake-id", AppKey = FakeKey }),
            SourceOf(SourceKind.Adzuna, null, "Adzuna search"), criteria);

        Assert.Equal(3, fetcher.Urls.Count);
        Assert.StartsWith("https://api.adzuna.com/v1/api/jobs/in/search/1?app_id=fake-id&app_key=" + FakeKey + "&what=.NET%20developer&where=Pune" +
                          "&results_per_page=50&max_days_old=14&content-type=application/json", fetcher.Urls[0]);
        Assert.Contains("&what=C%23&", fetcher.Urls[1]);
        Assert.Equal(2, result.Items.Count);                                       // the same ids from every keyword count once
        Assert.Equal("Adzuna search: searched 3 keywords in Pune, 2 jobs found, 2 read.", result.Message);
        Assert.DoesNotContain(FakeKey, result.Message);
    }

    [Fact]
    public async Task Adzuna_rejecting_the_keys_fails_without_echoing_them()
    {
        var fetcher = new ScriptedFetcher(_ => FetchResult.Fail("The site answered 401. The page may need a login.", 1, 401));

        var result = await Gather(Gatherer(fetcher, new AdzunaOptions { AppId = "fake-id", AppKey = FakeKey }),
            SourceOf(SourceKind.Adzuna, null, "Adzuna search"), DotNet);

        Assert.Equal(SourceStatus.Failed, result.Status);
        Assert.Equal("Adzuna rejected the server's keys. Check Adzuna:AppId and Adzuna:AppKey.", result.SafeError);
        Assert.DoesNotContain(FakeKey, result.Message);
    }

    [Fact]
    public async Task Adzuna_without_campaign_keywords_is_skipped()
    {
        var fetcher = new ScriptedFetcher(_ => throw new InvalidOperationException("must not fetch"));

        var result = await Gather(Gatherer(fetcher, new AdzunaOptions { AppId = "fake-id", AppKey = FakeKey }),
            SourceOf(SourceKind.Adzuna, null, "Adzuna search"), new CampaignCriteria { RequiredSkills = ["C#"] });

        Assert.Equal(SourceStatus.Skipped, result.Status);
        Assert.Empty(fetcher.Urls);
    }

    [Fact]
    public async Task Configured_api_bases_replace_the_real_hosts()
    {
        var fetcher = new ScriptedFetcher(_ => Json("[]"));
        var options = new ResearchOptions { LeverApiBase = "http://127.0.0.1:5999/" };

        await Gather(Gatherer(fetcher, research: options), SourceOf(SourceKind.Lever, "acme", "L"), DotNet);

        Assert.Equal(["http://127.0.0.1:5999/v0/postings/acme?mode=json&limit=100"], fetcher.Urls);
    }
}
