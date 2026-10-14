using OpportunityPilot.Application.JobBoards;

namespace OpportunityPilot.UnitTests;

public class NaukriApifyTests
{
    [Fact]
    public void Builds_Naukris_own_search_address()
    {
        Assert.Equal("https://www.naukri.com/data-scientist-jobs-in-bengaluru?k=data%20scientist&l=Bengaluru", NaukriApify.SearchUrl("data scientist", "Bengaluru"));
        Assert.Equal("https://www.naukri.com/net-developer-jobs?k=.NET%20developer", NaukriApify.SearchUrl(".NET developer", null));
    }

    [Fact]
    public void Reads_the_scrapers_jobs_and_keeps_only_naukri_links_once()
    {
        const string json = """
            [
              {"title":"Java Full Stack Developer","company":"Reliance Power","experience":"1-3 Yrs","salary":"Not disclosed","location":"Mumbai",
               "skills":["Java","SQL Server"],"postedLabel":"21 Days Ago","url":"https://www.naukri.com/job-listings-java-full-stack-developer-reliance-power-mumbai-1-to-3-years-210926010009"},
              {"title":"Same job again","company":"Reliance Power","url":"https://www.naukri.com/job-listings-java-full-stack-developer-reliance-power-mumbai-1-to-3-years-210926010009"},
              {"title":"Elsewhere","company":"X","url":"https://evil.test/job/1"},
              {"title":"No company","url":"https://www.naukri.com/job-listings-x-111111111111"}
            ]
            """;
        var job = Assert.Single(NaukriApify.Parse(json));
        Assert.Equal(("210926010009", "Java Full Stack Developer", "Reliance Power", "Mumbai"), (job.ProviderJobId, job.Title, job.CompanyName, job.Location));
        Assert.Contains("Experience: 1-3 Yrs", job.Description);
        Assert.Contains("Skills: Java, SQL Server", job.Description);
        Assert.DoesNotContain("Not disclosed", job.Description);
        Assert.Empty(NaukriApify.Parse("""{"error":"x"}"""));
    }

    [Fact]
    public void Off_without_a_token()
    {
        Assert.False(new ApifyOptions().Configured);
        Assert.True(new ApifyOptions { Token = "t" }.Configured);
    }
}
