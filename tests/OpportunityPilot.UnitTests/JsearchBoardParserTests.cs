using OpportunityPilot.Application.JobBoards;

namespace OpportunityPilot.UnitTests;

public class JsearchBoardParserTests
{
    private const string Json = """
    {"status":"OK","data":[
      {"job_id":"a1","job_title":".NET Developer","employer_name":"Acme","job_publisher":"Indeed",
       "job_apply_link":"https://www.indeed.com/viewjob?jk=abc","job_city":"Austin","job_state":"TX","job_country":"US",
       "job_is_remote":true,"job_employment_type":"FULLTIME","job_min_salary":90000,"job_max_salary":120000,
       "job_salary_currency":"USD","job_salary_period":"YEAR","job_posted_at_datetime_utc":"2026-10-07T10:00:00.000Z"},
      {"job_id":"b2","job_title":"Backend Engineer","employer_name":"Beta","job_publisher":"LinkedIn",
       "job_apply_link":"https://www.linkedin.com/jobs/view/1",
       "apply_options":[{"publisher":"LinkedIn","apply_link":"https://www.linkedin.com/jobs/view/1"},
                        {"publisher":"Indeed","apply_link":"https://in.indeed.com/viewjob?jk=def"}]},
      {"job_id":"c3","job_title":"QA","employer_name":"Gamma","job_publisher":"Glassdoor","job_apply_link":"https://glassdoor.com/x"},
      {"job_id":"d4","job_title":"Fake","employer_name":"Evil","job_publisher":"Indeed","job_apply_link":"https://indeed.com.evil.example/x"},
      {"job_id":"e5","job_title":"Data Analyst","employer_name":"Delta","job_publisher":"SEEK","job_apply_link":"https://www.seek.com.au/job/123"},
      {"job_id":"f6","job_title":"Plain http","employer_name":"Eps","job_publisher":"LinkedIn","job_apply_link":"http://www.linkedin.com/jobs/view/2"},
      {"job_id":"a1","job_title":".NET Developer","employer_name":"Acme","job_publisher":"Indeed","job_apply_link":"https://www.indeed.com/viewjob?jk=abc"}
    ]}
    """;

    [Theory]
    [InlineData(JobBoard.Indeed, new[] { "a1", "b2" })]
    [InlineData(JobBoard.LinkedIn, new[] { "b2" })]
    [InlineData(JobBoard.Seek, new[] { "e5" })]
    public void Keeps_only_postings_with_a_real_link_on_that_board(JobBoard board, string[] ids)
    {
        var (jobs, total) = JsearchBoardParser.Parse(Json, board);
        Assert.Equal(7, total);
        Assert.Equal(ids, jobs.Select(j => j.ProviderJobId));
    }

    // Shape returned by JSearch v5 on 2026-10-08 (trimmed from a real playground response).
    private const string V5Json = """
    {"status":"OK","request_id":"x","parameters":{"query":"developer jobs in chicago","num_pages":1},
     "data":{"jobs":[
      {"job_id":"v1","job_title":"Sr. Software Developer","employer_name":"Therapy Brands Thrive, LLC","job_publisher":"Indeed",
       "job_employment_type":"Full-time","job_apply_link":"https://www.indeed.com/viewjob?jk=2090bb9261592361",
       "apply_options":[{"apply_link":"https://www.indeed.com/viewjob?jk=2090bb9261592361","is_direct":false,"publisher":"Indeed"}],
       "job_is_remote":true,"job_posted_at_datetime_utc":"2026-10-02T00:00:00.000Z","job_location":"Anywhere",
       "job_city":null,"job_state":null,"job_country":null,"job_min_salary":null,"job_max_salary":null,"job_salary_period":null},
      {"job_id":"v2","job_title":"Software Developer Oracle (IT)","employer_name":"Apex Systems","job_publisher":"LinkedIn",
       "job_apply_link":"https://www.linkedin.com/jobs/view/software-developer-oracle-it-at-apex-systems-4476931825",
       "job_city":"Chicago","job_state":"Illinois","job_country":"US","job_min_salary":65,"job_max_salary":67,"job_salary_period":"HOUR"}
     ],"cursor":"abc"}}
    """;

    [Fact]
    public void Reads_the_next_page_cursor_only_when_present()
    {
        Assert.Equal("abc", JsearchBoardParser.NextCursor(V5Json));
        Assert.Null(JsearchBoardParser.NextCursor(Json));
        Assert.Null(JsearchBoardParser.NextCursor("{\"data\":{\"jobs\":[],\"cursor\":\"\"}}"));
    }

    [Fact]
    public void Reads_the_v5_wrapped_jobs_list()
    {
        var (indeed, total) = JsearchBoardParser.Parse(V5Json, JobBoard.Indeed);
        Assert.Equal(2, total);
        var job = Assert.Single(indeed);
        Assert.Equal("https://www.indeed.com/viewjob?jk=2090bb9261592361", job.BoardUrl);
        Assert.Equal("Anywhere", job.Location);
        Assert.True(job.IsRemote);
        var (linkedIn, _) = JsearchBoardParser.Parse(V5Json, JobBoard.LinkedIn);
        Assert.Equal(65m, Assert.Single(linkedIn).SalaryMin);
    }

    [Fact]
    public void Uses_the_board_apply_option_when_the_publisher_differs()
    {
        var (jobs, _) = JsearchBoardParser.Parse(Json, JobBoard.Indeed);
        Assert.Equal("https://in.indeed.com/viewjob?jk=def", jobs[1].BoardUrl);
    }

    [Fact]
    public void Maps_fields_without_inventing_values()
    {
        var (jobs, _) = JsearchBoardParser.Parse(Json, JobBoard.Indeed);
        var a = jobs[0];
        Assert.Equal("Austin, TX, US", a.Location);
        Assert.True(a.IsRemote);
        Assert.Equal(90000m, a.SalaryMin);
        Assert.Equal("USD", a.SalaryCurrency);
        Assert.Equal(new DateTime(2026, 10, 7, 10, 0, 0, DateTimeKind.Utc), a.PostedAt);
        Assert.Null(jobs[1].SalaryMin);
        Assert.Null(jobs[1].PostedAt);
    }

    [Fact]
    public void Missing_data_gives_no_jobs() => Assert.Empty(JsearchBoardParser.Parse("""{"status":"ERROR"}""", JobBoard.Indeed).Jobs);
}

public class JsearchIndianBoardsTests
{
    private const string Json = """
        {"data":[
          {"job_id":"n1","job_publisher":"Naukri.com","job_title":"Data Scientist","employer_name":"Larsen & Toubro","job_apply_link":"https://www.naukri.com/job-listings-data-scientist-lt-123","job_city":"Mumbai","job_country":"IN"},
          {"job_id":"i1","job_publisher":"Instahyre","job_title":"Backend Engineer","employer_name":"Acme","job_apply_link":"https://www.instahyre.com/job-456-backend-engineer/","job_country":"IN"},
          {"job_id":"l1","job_publisher":"LinkedIn","job_title":"Analyst","employer_name":"Globex","job_apply_link":"https://www.linkedin.com/jobs/view/9","job_country":"IN"}
        ]}
        """;

    [Theory]
    [InlineData(JobBoard.Naukri, "n1")]
    [InlineData(JobBoard.Instahyre, "i1")]
    public void Naukri_and_Instahyre_keep_only_their_own_postings(JobBoard board, string id)
    {
        var (jobs, _) = JsearchBoardParser.Parse(Json, board);
        Assert.Equal([id], jobs.Select(j => j.ProviderJobId));
    }
}
