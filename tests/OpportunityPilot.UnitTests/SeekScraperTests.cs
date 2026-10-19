using OpportunityPilot.Application.JobBoards;

namespace OpportunityPilot.UnitTests;

public sealed class SeekScraperTests
{
    /// <summary>Shape from the listing's own OpenAPI description (2026-10-19): data.results[] with advertiser, locations, work_types.</summary>
    private const string Answer = """
        {"data":{"query":"software engineer","location":"Sydney NSW","page":1,"total_count":8421,"page_size":32,"count":2,"has_more":true,
        "results":[
          {"job_id":"94059954","title":"Founding Engineer","teaser":"Build the product from day one","bullet_points":["Six-figure ARR in months"],
           "advertiser":{"id":"1","name":"Visaible"},"logo_url":"https://image.seek.com.au/logo.png","locations":[{"country_code":"AU","label":"Sydney NSW"}],
           "listed_at":"2026-08-18T21:50:30.000Z","salary_label":"$12,000 – $17,000 per month","work_types":["Contract/Temp"],"work_arrangement":"Remote"},
          {"job_id":"94059955","title":"Backend Developer","advertiser":{"name":"Acme"},"locations":[{"label":"Melbourne VIC"}],"work_types":["Full time"],"work_arrangement":"Hybrid"},
          {"job_id":"bad id!","title":"Skipped"}
        ]}}
        """;

    [Fact]
    public void Reads_search_results_with_contract_remote_and_the_seek_link()
    {
        var jobs = SeekScraper.Parse(Answer)!;
        Assert.Equal(2, jobs.Count);
        var j = jobs[0];
        Assert.Equal(("94059954", "Founding Engineer", "Visaible", "Sydney NSW"), (j.ProviderJobId, j.Title, j.CompanyName, j.Location));
        Assert.Equal("https://www.seek.com.au/job/94059954", j.BoardUrl);
        Assert.Equal("CONTRACTOR", j.EmploymentType);
        Assert.True(j.IsRemote);
        Assert.Equal(new DateTime(2026, 8, 18, 21, 50, 30, DateTimeKind.Utc), j.PostedAt);
        Assert.Contains("$12,000", j.Snippet);
        Assert.Equal("FULLTIME", jobs[1].EmploymentType);
        Assert.False(jobs[1].IsRemote);
    }

    /// <summary>The real answer of one live search (".NET developer", Sydney NSW, 2026-10-19): 32 rows with one sponsored repeat.</summary>
    [Fact]
    public void Reads_the_real_live_answer_and_drops_repeats()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "seek-live-2026-10-19.json"));
        var jobs = SeekScraper.Parse(json)!;
        Assert.True(jobs.Count is > 20 and < 32);
        Assert.Equal(jobs.Count, jobs.Select(j => j.ProviderJobId).Distinct().Count());
        Assert.Contains(jobs, j => j.EmploymentType == "CONTRACTOR");
        Assert.All(jobs, j => Assert.StartsWith("https://www.seek.com.au/job/", j.BoardUrl));
        Assert.All(jobs, j => Assert.False(string.IsNullOrWhiteSpace(j.CompanyName)));
    }

    [Fact]
    public void Builds_the_search_path_and_rejects_other_shapes()
    {
        Assert.Equal("/v1/seek/search?query=.NET%20developer&location=Sydney%20NSW&page=2", SeekScraper.PathAndQuery(new SeekOptions(), ".NET developer", "Sydney NSW", 2));
        Assert.Equal("/v1/seek/search?query=react", SeekScraper.PathAndQuery(new SeekOptions(), "react", null, 1));
        Assert.Null(SeekScraper.Parse("{\"people\":[]}"));
    }
}
