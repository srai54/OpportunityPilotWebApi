using OpportunityPilot.Application.Trends;

namespace OpportunityPilot.UnitTests;

public class TrendTests
{
    // Trimmed from a real JSearch v5 response (2026-10-08/09): mixed sites, one Indeed posting, hourly pay, remote.
    private const string Page1 = """
    {"status":"OK","data":{"jobs":[
     {"job_id":"a","job_title":"Sr. Software Developer","employer_name":"Therapy Brands","job_publisher":"Indeed","job_employment_type":"Full-time",
      "job_is_remote":true,"job_city":null,"job_posted_at_datetime_utc":"2026-10-08T00:00:00.000Z",
      "job_description":"8+ years of .NET and C# with MongoDB, AWS or Azure. Strong SQL. Docker and CI/CD pipelines."},
     {"job_id":"b","job_title":"Software Developer Oracle (IT)","employer_name":"Apex Systems","job_publisher":"LinkedIn","job_employment_type":"Contractor",
      "job_city":"Chicago","job_posted_at_datetime_utc":"2026-10-07T22:00:00.000Z","job_min_salary":65,"job_max_salary":67,"job_salary_period":"HOUR",
      "job_description":"Oracle, ODI, REST APIs, SQL, PL/SQL and Python."},
     {"job_id":"c","job_title":"UI Developer (ReactJS)","employer_name":"Magicforce","job_publisher":"Indeed","job_employment_type":"Full-time",
      "job_city":"Chicago","job_posted_at_datetime_utc":"2026-09-20T00:00:00.000Z","job_min_salary":60,"job_max_salary":70,"job_salary_period":"HOUR",
      "job_description":"React, TypeScript, Angular, Storybook, Playwright and Azure DevOps. Go to market with AI tools."},
     {"job_id":"a","job_title":"duplicate","employer_name":"Therapy Brands","job_publisher":"Indeed"}
    ],"cursor":"next-1"}}
    """;

    [Fact]
    public void Parser_reads_every_site_with_pay_remote_and_the_cursor_and_drops_duplicates()
    {
        var (postings, cursor) = JsearchTrendParser.Parse(Page1);
        Assert.Equal("next-1", cursor);
        Assert.Equal(["a", "b", "c"], postings.Select(p => p.Id));
        Assert.Equal(["Indeed", "LinkedIn", "Indeed"], postings.Select(p => p.Site));
        Assert.True(postings[0].IsRemote);
        Assert.Equal(65m, postings[1].PayMin);
    }

    [Fact]
    public void Skills_are_matched_as_whole_terms_and_Go_is_not_counted_from_plain_English()
    {
        var (postings, _) = JsearchTrendParser.Parse(Page1);
        Assert.Contains(".NET", postings[0].Skills);
        Assert.Contains("C#", postings[0].Skills);
        Assert.Contains("CI/CD", postings[0].Skills);
        Assert.DoesNotContain("Java", postings[0].Skills);          // JavaScript/Java must not match inside other words
        Assert.DoesNotContain("Golang", postings[2].Skills);        // "Go to market" is not the language
        Assert.Contains("React", postings[2].Skills);
        Assert.Contains("Playwright", postings[2].Skills);
    }

    [Fact]
    public void Report_counts_sites_skills_companies_pay_remote_and_freshness()
    {
        var (postings, _) = JsearchTrendParser.Parse(Page1);
        var now = new DateTime(2026, 10, 9, 6, 0, 0, DateTimeKind.Utc);
        var r = TrendAnalyzer.Analyze(postings, 1, now, now, 188, false, "test");

        Assert.Equal(3, r.Sampled);
        Assert.Equal(("Indeed", 2, 67), (r.Sites[0].Name, r.Sites[0].Count, r.Sites[0].SharePercent));
        Assert.Equal(33, r.RemotePercent);
        Assert.Equal(67, r.PayListedPercent);
        var pay = Assert.Single(r.Pay);
        Assert.Equal(("per hour", 2, 62.5m, 68.5m), (pay.Period, pay.Postings, pay.MedianMin, pay.MedianMax));
        Assert.Equal(2, r.Freshness.Last24Hours);   // 30 h and 32 h old: inside the 36-hour "last day" bucket
        Assert.Equal(0, r.Freshness.Last3Days);
        Assert.Equal(1, r.Freshness.Older);
        Assert.Contains(r.TopSkills, s => s.Name == "SQL" && s.Count == 2);
        Assert.Contains(r.Locations, l => l.Name == "Chicago" && l.Count == 2);
    }

    private sealed class FakeSource(params TrendPage[] pages) : ITrendSource
    {
        public int Calls { get; private set; }
        public List<string?> Cursors { get; } = [];
        public bool Configured => true;
        public Task<TrendPage> PageAsync(TrendPageRequest request, CancellationToken ct)
        {
            Cursors.Add(request.Cursor);
            return Task.FromResult(pages[Math.Min(Calls++, pages.Length - 1)]);
        }
    }

    private static TrendPosting P(string id, string company) => new(id, "Dev", company, "LinkedIn", "Chicago", false, "FULLTIME", null, null, null, null, ["SQL"]);

    [Fact]
    public async Task Service_follows_the_cursor_for_the_requested_pages_and_merges_without_duplicates()
    {
        var source = new FakeSource(
            new TrendPage("Ready", null, [P("1", "A"), P("2", "B")], "c1", 190, false, DateTime.UtcNow),
            new TrendPage("Ready", null, [P("2", "B"), P("3", "C")], null, 189, false, DateTime.UtcNow));
        var report = await new TrendService(source, TimeProvider.System).ReportAsync("dev", "Chicago", "month", null, 3, default);

        Assert.Equal(3, report.Sampled);
        Assert.Equal(2, report.Pages);                       // the second page had no cursor, so no third search was made
        Assert.Equal([null, "c1"], source.Cursors);
        Assert.Equal(189, report.QuotaRemaining);
    }

    [Fact]
    public async Task Service_reports_a_provider_problem_instead_of_an_empty_trend()
    {
        var source = new FakeSource(new TrendPage("NotConfigured", "no key", [], null, null, false, null));
        var report = await new TrendService(source, TimeProvider.System).ReportAsync("dev", null, "month", null, 1, default);
        Assert.Equal("NotConfigured", report.Status);
        Assert.Equal(0, report.Sampled);
    }

    [Fact]
    public async Task Service_never_searches_more_than_three_pages()
    {
        var source = new FakeSource(new TrendPage("Ready", null, [P("1", "A")], "again", 1, false, DateTime.UtcNow));
        await new TrendService(source, TimeProvider.System).ReportAsync("dev", null, "month", null, 99, default);
        Assert.Equal(3, source.Calls);
    }
}
