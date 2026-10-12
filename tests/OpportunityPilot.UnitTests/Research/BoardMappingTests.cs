using OpportunityPilot.Application.Campaigns;
using OpportunityPilot.Application.Research;
using OpportunityPilot.Application.Research.Boards;
using OpportunityPilot.Domain.Opportunities;
using OpportunityPilot.Domain.Research;
using OpportunityPilot.Infrastructure.Research;

namespace OpportunityPilot.UnitTests.Research;

/// <summary>Fixtures are trimmed copies of the real API shapes (checked 2026-10-05).</summary>
public class BoardMappingTests
{
    private static readonly DateTime T0 = new(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);
    private static readonly ContentParser Parser = new();

    private static Source SourceOf(SourceKind kind, string? url, string label) =>
        new(Guid.NewGuid(), Guid.NewGuid(), kind, label, url, null, null, null, T0);

    public const string GreenhouseList = """
        {
          "jobs": [
            {
              "absolute_url": "https://stripe.com/jobs/search?gh_jid=7171717",
              "data_compliance": [{ "type": "gdpr", "requires_consent": false }],
              "internal_job_id": 3000001,
              "location": { "name": "Bengaluru, India" },
              "metadata": null,
              "id": 7171717,
              "updated_at": "2026-09-30T10:00:00-04:00",
              "requisition_id": "R-1",
              "title": "Backend Engineer, .NET Payments",
              "company_name": "Stripe",
              "first_published": "2026-09-01T09:00:00-04:00"
            },
            {
              "absolute_url": "https://stripe.com/jobs/search?gh_jid=7171718",
              "location": { "name": "Remote" },
              "id": 7171718,
              "updated_at": "2026-10-02T10:00:00-04:00",
              "title": "Account Executive",
              "company_name": "Stripe"
            },
            { "id": 7171719, "title": null },
            { "title": "No id at all" }
          ],
          "meta": { "total": 4 }
        }
        """;

    public const string GreenhouseDetail = """
        {
          "id": 7171717,
          "title": "Backend Engineer, .NET Payments",
          "content": "&lt;div class=&quot;intro&quot;&gt;&lt;p&gt;We build &lt;strong&gt;C# and .NET&lt;/strong&gt; services. 3-5 years.&lt;/p&gt;&lt;script&gt;track()&lt;/script&gt;&lt;ul&gt;&lt;li&gt;Kafka &amp;amp; Docker&lt;/li&gt;&lt;/ul&gt;&lt;/div&gt;"
        }
        """;

    public const string LeverPostings = """
        [
          {
            "additionalPlain": "Benefits text",
            "categories": { "commitment": "Full-time", "location": "Pune", "team": "Engineering", "allLocations": ["Pune"] },
            "createdAt": 1727700000000,
            "descriptionPlain": "We need a C# developer.",
            "description": "<div>We need a C# developer.</div>",
            "id": "5ac21346-8e0c-4494-8e7a-3eb92ff77902",
            "lists": [
              { "text": "Requirements", "content": "<li>.NET 8</li><li>SQL &amp; Postgres</li>" },
              { "text": "Nice to have", "content": "<li>Docker</li>" }
            ],
            "text": "Senior .NET Developer",
            "country": "IN",
            "workplaceType": "hybrid",
            "hostedUrl": "https://jobs.lever.co/acme-labs/5ac21346-8e0c-4494-8e7a-3eb92ff77902",
            "applyUrl": "https://jobs.lever.co/acme-labs/5ac21346-8e0c-4494-8e7a-3eb92ff77902/apply"
          },
          {
            "categories": { "location": "Remote" },
            "id": "b1",
            "text": "Designer",
            "workplaceType": "unspecified",
            "hostedUrl": "https://jobs.lever.co/acme-labs/b1"
          },
          { "id": "no-title" }
        ]
        """;

    public const string AdzunaResults = """
        {
          "__CLASS__": "Adzuna::API::Response::JobSearchResults",
          "count": 2,
          "mean": 1200000,
          "results": [
            {
              "id": "4567890123",
              "title": "Senior <strong>.NET</strong> Developer",
              "company": { "display_name": "Globex", "__CLASS__": "Adzuna::API::Response::Company" },
              "location": { "display_name": "Pune, Maharashtra", "area": ["India", "Maharashtra", "Pune"] },
              "redirect_url": "https://www.adzuna.in/land/ad/4567890123?se=abc",
              "description": "Build <strong>C#</strong> APIs on .NET for payments…",
              "created": "2026-09-29T08:00:00Z",
              "salary_is_predicted": "1"
            },
            {
              "id": 99,
              "title": "QA Analyst",
              "location": { "display_name": "Mumbai" },
              "redirect_url": "javascript:alert(1)"
            }
          ]
        }
        """;

    [Fact]
    public void Greenhouse_list_maps_jobs_and_drops_ones_without_id_or_title()
    {
        var jobs = BoardMapping.GreenhouseJobs(GreenhouseList)!;

        Assert.Equal(2, jobs.Count);
        var job = jobs[0];
        Assert.Equal("7171717", job.Id);
        Assert.Equal("Backend Engineer, .NET Payments", job.Title);
        Assert.Equal("https://stripe.com/jobs/search?gh_jid=7171717", job.Url);
        Assert.Equal("Bengaluru, India", job.Location);
        Assert.Equal("Stripe", job.Company);
        Assert.Equal(new DateTime(2026, 9, 30, 14, 0, 0, DateTimeKind.Utc), job.UpdatedAt);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"jobs\": 3}")]
    [InlineData("{\"meta\": {}}")]
    public void A_body_that_is_not_a_greenhouse_listing_is_unreadable(string body) =>
        Assert.Null(BoardMapping.GreenhouseJobs(body));

    [Fact]
    public void Greenhouse_content_is_unescaped_then_reduced_to_text()
    {
        var text = BoardMapping.GreenhouseDescription(GreenhouseDetail, Parser)!;

        Assert.Contains("We build C# and .NET services. 3-5 years.", text);
        Assert.Contains("Kafka & Docker", text);
        Assert.DoesNotContain("<", text);
        Assert.DoesNotContain("&lt;", text);
        Assert.DoesNotContain("track()", text);
    }

    [Fact]
    public void A_greenhouse_job_becomes_a_candidate_with_platform_id_and_apply_url()
    {
        var source = SourceOf(SourceKind.Greenhouse, "stripe", "Greenhouse board stripe");
        var job = BoardMapping.GreenhouseJobs(GreenhouseList)![0];

        var c = BoardMapping.GreenhouseCandidate(source, "stripe", job, "C# and .NET services.");

        Assert.Equal(source.Id, c.SourceId);
        Assert.Equal("Backend Engineer, .NET Payments", c.Title);
        Assert.Equal("Stripe", c.Organization);
        Assert.Equal("Bengaluru, India", c.Location);
        Assert.Equal(JobPlatform.Greenhouse, c.Platform);
        Assert.Equal("7171717", c.ExternalId);
        Assert.Equal(job.Url, c.Url);
        Assert.Equal(job.Url, c.ApplyUrl);
        Assert.Equal(job.UpdatedAt, c.PostedAt);
        Assert.Equal("C# and .NET services.", c.Text);
        Assert.Contains("Company: Stripe", c.Excerpt);
        Assert.Equal("job:Greenhouse:7171717", Candidates.DedupeKey(Domain.Common.OpportunityMode.Job, c.Platform, c.ExternalId, c.Title, c.Organization, null));
    }

    [Fact]
    public void A_greenhouse_job_without_company_name_uses_the_board_token_and_without_content_has_no_text()
    {
        var source = SourceOf(SourceKind.Greenhouse, "acme", "Greenhouse board acme");
        var c = BoardMapping.GreenhouseCandidate(source, "acme", new GreenhouseJob("1", "Engineer", null, null, null, null), null);

        Assert.Equal("acme", c.Organization);
        Assert.Equal("", c.Text);
        Assert.Null(c.Url);
    }

    [Fact]
    public void Lever_postings_map_hosted_and_apply_urls_lists_and_the_work_mode_hint()
    {
        var source = SourceOf(SourceKind.Lever, "acme-labs", "Lever company acme-labs");

        var items = BoardMapping.LeverCandidates(LeverPostings, source, "acme-labs", Parser)!;

        Assert.Equal(2, items.Count);
        var c = items[0];
        Assert.Equal("Senior .NET Developer", c.Title);
        Assert.Equal("Acme Labs", c.Organization);
        Assert.Equal("Pune", c.Location);
        Assert.Equal("IN", c.Country);
        Assert.Equal(JobPlatform.Lever, c.Platform);
        Assert.Equal("5ac21346-8e0c-4494-8e7a-3eb92ff77902", c.ExternalId);
        Assert.Equal("https://jobs.lever.co/acme-labs/5ac21346-8e0c-4494-8e7a-3eb92ff77902", c.Url);
        Assert.Equal("https://jobs.lever.co/acme-labs/5ac21346-8e0c-4494-8e7a-3eb92ff77902/apply", c.ApplyUrl);
        Assert.Equal("hybrid", c.WorkplaceType);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1727700000000).UtcDateTime, c.PostedAt);
        Assert.StartsWith("Workplace: Hybrid\nWe need a C# developer.", c.Text);
        Assert.Contains("Requirements\n.NET 8", c.Text);
        Assert.Contains("SQL & Postgres", c.Text);
        Assert.Contains("Nice to have\nDocker", c.Text);
        Assert.DoesNotContain("<li>", c.Text);
        Assert.DoesNotContain("Benefits text", c.Text);   // only descriptionPlain + lists, per the contract

        var designer = items[1];
        Assert.DoesNotContain("Workplace", designer.Text);  // "unspecified" gives no hint
        Assert.Equal(designer.Url, designer.ApplyUrl);      // no applyUrl → the hosted page
    }

    [Theory]
    [InlineData("remote", "Workplace: Remote")]
    [InlineData("on-site", "Workplace: On-site")]
    public void Lever_workplace_types_become_words_the_work_mode_rule_reads(string workplaceType, string expected)
    {
        var json = $$"""[{ "id": "x", "text": "Engineer", "workplaceType": "{{workplaceType}}", "descriptionPlain": "Build things." }]""";
        var c = BoardMapping.LeverCandidates(json, SourceOf(SourceKind.Lever, "acme", "Lever"), "acme", Parser)!.Single();
        Assert.StartsWith(expected, c.Text);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"ok\": false, \"error\": \"Document not found\"}")]
    [InlineData("<html></html>")]
    public void A_body_that_is_not_a_lever_array_is_unreadable(string body) =>
        Assert.Null(BoardMapping.LeverCandidates(body, SourceOf(SourceKind.Lever, "acme", "Lever"), "acme", Parser));

    [Fact]
    public void Adzuna_results_map_with_markup_stripped_and_unsafe_urls_dropped()
    {
        var source = SourceOf(SourceKind.Adzuna, null, "Adzuna search");

        var items = BoardMapping.AdzunaCandidates(AdzunaResults, source, Parser)!;

        Assert.Equal(2, items.Count);
        var c = items[0];
        Assert.Equal("Senior .NET Developer", c.Title);
        Assert.Equal("Globex", c.Organization);
        Assert.Equal("Pune, Maharashtra", c.Location);
        Assert.Equal(JobPlatform.Adzuna, c.Platform);
        Assert.Equal("4567890123", c.ExternalId);
        Assert.Equal("https://www.adzuna.in/land/ad/4567890123?se=abc", c.Url);
        Assert.Equal(c.Url, c.ApplyUrl);
        Assert.Equal("Build C# APIs on .NET for payments…", c.Text);

        var qa = items[1];
        Assert.Equal("99", qa.ExternalId);                  // numeric ids are kept as text
        Assert.Equal("", qa.Organization);
        Assert.Null(qa.Url);                                 // javascript: is never kept as a link
    }

    [Theory]
    [InlineData("{\"results\": {}}")]
    [InlineData("{\"exception\": \"AUTH_FAIL\"}")]
    public void A_body_that_is_not_adzuna_results_is_unreadable(string body) =>
        Assert.Null(BoardMapping.AdzunaCandidates(body, SourceOf(SourceKind.Adzuna, null, "Adzuna"), Parser));

    [Fact]
    public void Title_terms_are_keyword_phrases_their_words_and_required_skills_without_short_common_words()
    {
        var terms = BoardMapping.TitleTerms(new CampaignCriteria
        {
            Keywords = ["Head of Engineering", ".NET developer"],
            RequiredSkills = ["C#", ".NET"]
        });

        Assert.Equal(["Head of Engineering", "Head", "Engineering", ".NET developer", ".NET", "developer", "C#"], terms);
    }

    [Fact]
    public void Prefilter_keeps_matching_titles_best_match_first_then_most_recent()
    {
        var jobs = new List<GreenhouseJob>
        {
            new("1", "Office Manager", null, null, null, new DateTime(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc)),
            new("2", "Backend Engineer", null, null, null, new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc)),
            new("3", "Senior .NET Backend Engineer", null, null, null, new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc)),
            new("4", "Backend Engineer, Payments", null, null, null, new DateTime(2026, 10, 4, 0, 0, 0, DateTimeKind.Utc)),
            new("5", "JavaScript Engineer", null, null, null, null),
            new("6", "Dotnet Platform Lead", null, null, null, null)
        };
        var terms = BoardMapping.TitleTerms(new CampaignCriteria { Keywords = ["backend"], RequiredSkills = [".NET", "Java"] });

        var kept = BoardMapping.Prefilter(jobs, terms);

        // "JavaScript" does not match "Java" (word boundary); "Dotnet" matches ".NET" (the rules' alias).
        Assert.Equal(["3", "4", "2", "6"], kept.Select(j => j.Id));
    }

    [Fact]
    public void Prefilter_without_keywords_or_required_skills_keeps_everything_most_recent_first()
    {
        var jobs = new List<GreenhouseJob>
        {
            new("old", "A", null, null, null, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
            new("new", "B", null, null, null, new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc))
        };

        var kept = BoardMapping.Prefilter(jobs, BoardMapping.TitleTerms(new CampaignCriteria { PreferredSkills = ["Go"] }));

        Assert.Equal(["new", "old"], kept.Select(j => j.Id));
    }

    [Theory]
    [InlineData("leverdemo", "Leverdemo")]
    [InlineData("acme-labs", "Acme Labs")]
    [InlineData("a--b", "A B")]
    public void Lever_slugs_are_title_cased_for_the_organization(string slug, string expected) =>
        Assert.Equal(expected, BoardMapping.TitleCase(slug));

    [Fact]
    public void Additional_public_boards_map_to_honest_candidates()
    {
        var ashby = BoardMapping.AshbyCandidates("""{"jobs":[{"id":"a1","title":"Engineer","location":"Remote","workplaceType":"Remote","jobUrl":"https://jobs.ashbyhq.com/acme/a1","applyUrl":"https://jobs.ashbyhq.com/acme/a1/application","descriptionPlain":"C# APIs","publishedAt":"2026-10-01T00:00:00Z"}]}""",
            SourceOf(SourceKind.Ashby, "acme", "Ashby"), "acme", Parser)!.Single();
        Assert.Equal((JobPlatform.Ashby, "a1", "Acme", "Remote"), (ashby.Platform, ashby.ExternalId, ashby.Organization, ashby.WorkplaceType));

        var smart = BoardMapping.SmartRecruitersCandidates("""{"content":[{"id":"s1","name":"Developer","company":{"name":"Globex"},"location":{"city":"Pune","country":"in","remote":true},"releasedDate":"2026-10-01T00:00:00Z"}]}""",
            SourceOf(SourceKind.SmartRecruiters, "globex", "SmartRecruiters"), "globex", Parser)!.Single();
        Assert.Equal(JobPlatform.SmartRecruiters, smart.Platform);
        Assert.Equal("Pune, in", smart.Location);
        Assert.StartsWith("https://jobs.smartrecruiters.com/globex/s1", smart.ApplyUrl);

        var recruitee = BoardMapping.RecruiteeCandidates("""{"offers":[{"id":7,"title":"Backend Engineer","company_name":"Acme","location":"Remote","careers_url":"https://acme.recruitee.com/o/backend","careers_apply_url":"https://acme.recruitee.com/o/backend/c/new","description":"<p>.NET and SQL</p>"}]}""",
            SourceOf(SourceKind.Recruitee, "acme", "Recruitee"), "acme", Parser)!.Single();
        Assert.Equal((JobPlatform.Recruitee, "7", ".NET and SQL"), (recruitee.Platform, recruitee.ExternalId, recruitee.Text));

        var workable = BoardMapping.WorkableCandidates("""{"jobs":[{"shortcode":"W1","title":"Platform Engineer","location":{"location_str":"India"},"description":"<p>Docker</p>","url":"https://apply.workable.com/acme/j/W1/"}]}""",
            SourceOf(SourceKind.Workable, "acme", "Workable"), "acme", Parser)!.Single();
        Assert.Equal((JobPlatform.Workable, "W1", "Docker"), (workable.Platform, workable.ExternalId, workable.Text));
    }

    [Fact]
    public void Aggregate_remote_feeds_skip_metadata_and_map_jobs()
    {
        var remotive = BoardMapping.RemotiveCandidates("""{"jobs":[{"id":1,"url":"https://remotive.com/job/1","title":"Remote Developer","company_name":"Acme","candidate_required_location":"Worldwide","description":"<p>Go services</p>","publication_date":"2026-10-01T00:00:00Z"}]}""",
            SourceOf(SourceKind.Remotive, null, "Remotive"), Parser)!.Single();
        Assert.Equal((JobPlatform.Remotive, "1", "Go services"), (remotive.Platform, remotive.ExternalId, remotive.Text));

        var remoteOk = BoardMapping.RemoteOkCandidates("""[{"legal":"Please link back"},{"id":"2","position":"Engineer","company":"Globex","location":"Remote","url":"https://remoteok.com/remote-jobs/2","description":"<p>Rust</p>","date":"2026-10-01T00:00:00Z"}]""",
            SourceOf(SourceKind.RemoteOk, null, "Remote OK"), Parser)!.Single();
        Assert.Equal((JobPlatform.RemoteOk, "2", "Rust"), (remoteOk.Platform, remoteOk.ExternalId, remoteOk.Text));
    }

    // Shapes below are copied from the real responses seen on 2026-10-12 (trimmed to one job each).
    [Fact]
    public void Arbeitnow_maps_a_job_with_remote_flag_and_unix_date()
    {
        var c = BoardMapping.ArbeitnowCandidates("""{"data":[{"slug":"cs-manager-nuremberg-188405","company_name":"Galvanek Energiesysteme","title":"Customer Success Manager (m/w/d)","description":"<p>30 Kunden taeglich.</p>","remote":true,"url":"https://www.arbeitnow.com/jobs/companies/galvanek/cs-manager-nuremberg-188405","tags":["customer service"],"job_types":["full time"],"location":"Nuremberg","created_at":1791795600}],"links":{},"meta":{"current_page":1}}""",
            SourceOf(SourceKind.Arbeitnow, null, "Arbeitnow"), Parser)!.Single();
        Assert.Equal((JobPlatform.Arbeitnow, "cs-manager-nuremberg-188405", "Galvanek Energiesysteme", "Nuremberg"), (c.Platform, c.ExternalId, c.Organization, c.Location));
        Assert.Contains("Workplace: Remote", c.Text);
        Assert.Contains("30 Kunden", c.Text);
        Assert.Equal(new DateTime(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc), c.PostedAt);
        Assert.Null(BoardMapping.ArbeitnowCandidates("""{"oops":1}""", SourceOf(SourceKind.Arbeitnow, null, "A"), Parser));
    }

    [Fact]
    public void Jobicy_maps_pay_and_remote()
    {
        var c = BoardMapping.JobicyCandidates("""{"apiVersion":"2.2.19","jobCount":1,"jobs":[{"id":155094,"url":"https://jobicy.com/jobs/155094-enterprise-account-executive-iberia-2","jobSlug":"x","jobTitle":"Enterprise Account Executive, Iberia","companyName":"Cresta","jobIndustry":["Sales"],"jobType":["Full-Time"],"jobGeo":"Spain","jobLevel":"Director","jobExcerpt":"Sell.","jobDescription":"<p>Own the Iberia market.</p>","pubDate":"2026-10-12T07:02:38+00:00","salaryMin":80000,"salaryMax":100000,"salaryCurrency":"EUR","salaryPeriod":"yearly"}]}""",
            SourceOf(SourceKind.Jobicy, null, "Jobicy"), Parser)!.Single();
        Assert.Equal((JobPlatform.Jobicy, "155094", "Cresta", "Spain"), (c.Platform, c.ExternalId, c.Organization, c.Location));
        Assert.Contains("Salary: EUR 80000 - 100000 yearly", c.Text);
        Assert.Contains("Own the Iberia market.", c.Text);
        Assert.Contains("Workplace: Remote", c.Text);
    }

    [Fact]
    public void WeWorkRemotely_reads_the_rss_and_splits_company_from_title()
    {
        const string rss = """
            <?xml version="1.0" encoding="UTF-8"?><rss version="2.0"><channel><title>We Work Remotely</title>
            <item><title>Amwell: Sr Product Marketing Manager</title><region>Anywhere in the World</region><category>Sales and Marketing</category><type>Full-Time</type>
            <description>&lt;p&gt;Headquarters: Remote US&lt;/p&gt;</description><pubDate>Mon, 12 Oct 2026 06:00:00 +0000</pubDate>
            <link>https://weworkremotely.com/remote-jobs/amwell-sr-product-marketing-manager</link><guid>https://weworkremotely.com/remote-jobs/amwell-sr-product-marketing-manager</guid></item>
            </channel></rss>
            """;
        var c = BoardMapping.WeWorkRemotelyCandidates(rss, SourceOf(SourceKind.WeWorkRemotely, null, "WWR"), Parser)!.Single();
        Assert.Equal((JobPlatform.WeWorkRemotely, "Amwell", "Sr Product Marketing Manager", "Anywhere in the World"), (c.Platform, c.Organization, c.Title, c.Location));
        Assert.Contains("Headquarters: Remote US", c.Text);
        Assert.Equal(new DateTime(2026, 10, 12, 6, 0, 0, DateTimeKind.Utc), c.PostedAt);
    }

    [Fact]
    public void WeWorkRemotely_refuses_a_feed_with_a_dtd_and_junk()
    {
        const string evil = """<?xml version="1.0"?><!DOCTYPE rss [<!ENTITY x "boom">]><rss><channel><item><title>A: B</title><link>https://x.test/1</link></item></channel></rss>""";
        Assert.Null(BoardMapping.WeWorkRemotelyCandidates(evil, SourceOf(SourceKind.WeWorkRemotely, null, "WWR"), Parser));
        Assert.Null(BoardMapping.WeWorkRemotelyCandidates("not xml", SourceOf(SourceKind.WeWorkRemotely, null, "WWR"), Parser));
        Assert.Null(BoardMapping.WeWorkRemotelyCandidates("", SourceOf(SourceKind.WeWorkRemotely, null, "WWR"), Parser));
    }
}
