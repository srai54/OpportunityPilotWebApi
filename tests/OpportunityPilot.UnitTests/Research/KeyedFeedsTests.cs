using OpportunityPilot.Application.JobBoards;

namespace OpportunityPilot.UnitTests.Research;

/// <summary>Mapping of the official job-search APIs added 2026-10-19 (sample answers in each API's documented shape).</summary>
public class KeyedFeedsTests
{
    [Fact]
    public void Jooble_maps_title_company_link_type_and_text()
    {
        var j = KeyedFeeds.Jooble("""{"totalCount":1,"jobs":[{"id":-123,"title":"React Developer","location":"Berlin","snippet":"&nbsp;<b>React</b> contract","type":"Contract","link":"https://jooble.org/desc/-123","company":"Acme","updated":"2026-10-18T10:00:00"}]}""")!.Single();
        Assert.Equal(("jooble:-123", "React Developer", "Acme", "Contract", "https://jooble.org/desc/-123"), (j.ProviderJobId, j.Title, j.CompanyName, j.EmploymentType, j.BoardUrl));
        Assert.Contains("React contract", j.Snippet);
    }

    [Fact]
    public void Careerjet_findwork_and_usajobs_map_their_own_shapes()
    {
        var c = KeyedFeeds.Careerjet("""{"type":"JOBS","jobs":[{"title":"Go Engineer","company":"Globex","locations":"London","description":"Remote friendly","url":"https://jobviewtrack.com/x","date":"Sat, 18 Oct 2026 10:00:00 GMT"}]}""")!.Single();
        Assert.Equal(("Go Engineer", "Globex", "London"), (c.Title, c.CompanyName, c.Location));
        var f = KeyedFeeds.Findwork("""{"count":1,"results":[{"id":77,"role":"Frontend Dev","company_name":"Initech","employment_type":"contract","location":"Anywhere","remote":true,"url":"https://findwork.dev/77","text":"<p>Vue</p>","date_posted":"2026-10-17T00:00:00Z"}]}""")!.Single();
        Assert.True(f.IsRemote);
        Assert.Equal(("findwork:77", "contract"), (f.ProviderJobId, f.EmploymentType));
        var u = KeyedFeeds.UsaJobs("""{"SearchResult":{"SearchResultItems":[{"MatchedObjectId":"801","MatchedObjectDescriptor":{"PositionTitle":"IT Specialist","OrganizationName":"Department of X","PositionLocationDisplay":"Washington, DC","PositionURI":"https://www.usajobs.gov/job/801","PublicationStartDate":"2026-10-16","PositionSchedule":[{"Name":"Full-time"}],"UserArea":{"Details":{"JobSummary":"Support systems."}}}}]}}""")!.Single();
        Assert.Equal(("usajobs:801", "Department of X", "Full-time", "Support systems."), (u.ProviderJobId, u.CompanyName, u.EmploymentType, u.Description));
    }

    [Fact]
    public void Freelancer_projects_are_remote_contract_work_with_budget()
    {
        var p = KeyedFeeds.Freelancer("""{"status":"success","result":{"projects":[{"id":555,"title":"Build a React dashboard","seo_url":"react/build-react-dashboard","preview_description":"Need a dashboard","type":"fixed","budget":{"minimum":250,"maximum":750},"currency":{"code":"USD"},"time_submitted":1792400000}]}}""")!.Single();
        Assert.Equal(("https://www.freelancer.com/projects/react/build-react-dashboard", "CONTRACTOR", true, 250m, 750m, "USD"), (p.BoardUrl, p.EmploymentType, p.IsRemote, p.SalaryMin, p.SalaryMax, p.SalaryCurrency));
    }

    [Fact]
    public void Reed_and_arbeitsagentur_map_their_own_shapes()
    {
        var r = KeyedFeeds.Reed("""{"results":[{"jobId":42,"employerName":"Hays","jobTitle":"Contract .NET Developer","locationName":"London","minimumSalary":500,"maximumSalary":600,"currency":"GBP","date":"18/10/2026","jobDescription":"Day rate, outside IR35","jobUrl":"https://www.reed.co.uk/jobs/42"}]}""")!.Single();
        Assert.Equal(("reed:42", "Hays", 500m, new DateTime(2026, 10, 18)), (r.ProviderJobId, r.CompanyName, r.SalaryMin, r.PostedAt!.Value.Date));
        var a = KeyedFeeds.Arbeitsagentur("""{"stellenangebote":[{"refnr":"10000-1","titel":"Softwareentwickler (m/w/d)","arbeitgeber":"Beispiel GmbH","arbeitsort":{"ort":"Berlin","land":"Deutschland"},"aktuelleVeroeffentlichungsdatum":"2026-10-17"}]}""")!.Single();
        Assert.Equal(("Beispiel GmbH", "Berlin, Deutschland", "https://www.arbeitsagentur.de/jobsuche/jobdetail/10000-1"), (a.CompanyName, a.Location, a.BoardUrl));
    }

    [Fact]
    public void Bad_answers_and_unsafe_links_are_dropped()
    {
        Assert.Null(KeyedFeeds.Jooble("not json"));
        Assert.Empty(KeyedFeeds.Jooble("""{"jobs":[{"title":"x","link":"javascript:alert(1)"}]}""")!);
        Assert.False(new JobFeedOptions().Has(FeedSite.Jooble));
        Assert.True(new JobFeedOptions().Has(FeedSite.Freelancer));
        Assert.True(new JobFeedOptions().Has(FeedSite.Arbeitsagentur));
        Assert.False(new JobFeedOptions().Has(FeedSite.Reed));
    }
}
