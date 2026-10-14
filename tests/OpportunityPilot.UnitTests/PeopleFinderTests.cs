using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpportunityPilot.Application.Abstractions;
using OpportunityPilot.Application.Common;
using OpportunityPilot.Application.Integrations;
using OpportunityPilot.Application.Mail;
using OpportunityPilot.Application.Opportunities;
using OpportunityPilot.Domain.Common;
using OpportunityPilot.Domain.Integrations;
using OpportunityPilot.Domain.Opportunities;
using OpportunityPilot.Infrastructure.Persistence;

namespace OpportunityPilot.UnitTests;

/// <summary>REACH2: Hunter and Apollo people lookups with the person's own key — parsing, domain, errors, and that nothing is stored but a note.</summary>
public class PeopleFinderTests
{
    private static readonly DateTime T0 = new(2026, 10, 14, 9, 0, 0, DateTimeKind.Utc);
    private const string HunterKey = "hunter-key-SECRET123";
    private const string ApolloKey = "apollo-key-SECRET456";

    // Shape of Hunter's GET /v2/domain-search reply (fields as documented at hunter.io/api-documentation/v2).
    private const string HunterDomainSearch = """
        {"data":{"domain":"northwind.example","organization":"Northwind","pattern":"{first}","emails":[
          {"value":"asha@northwind.example","type":"personal","confidence":94,"sources":[{"domain":"blog.example","uri":"https://blog.example/a"},{"domain":"x.example","uri":"https://x.example"}],
           "first_name":"Asha","last_name":"Rao","position":"CTO","seniority":"executive","department":"executive","linkedin":"https://www.linkedin.com/in/asharao","verification":{"date":"2026-09-01","status":"valid"}},
          {"value":"ravi@northwind.example","type":"personal","confidence":71,"sources":[{"domain":"y.example","uri":"https://y.example"}],
           "first_name":"Ravi","last_name":"Menon","position":"Senior Talent Acquisition Partner","linkedin":"ravimenon","verification":null},
          {"value":"careers@northwind.example","type":"generic","confidence":88,"sources":[],"first_name":null,"last_name":null,"position":null,"linkedin":null},
          {"value":null,"type":"personal","confidence":50}
        ]},"meta":{"results":4,"limit":10,"offset":0}}
        """;

    // Shape of Apollo's POST /api/v1/mixed_people/api_search reply (docs.apollo.io/reference/people-api-search): no emails, obfuscated last name.
    private const string ApolloSearch = """
        {"total_entries":2,"people":[
          {"id":"67bdafd0c3a4c50001bbd7c2","first_name":"Maya","last_name_obfuscated":"Pa***l","title":"Head of Talent","has_email":true,"organization":{"name":"Northwind"}},
          {"id":"67bdafd0c3a4c50001bbd7c3","first_name":"Tom","last_name_obfuscated":"Le***e","title":"Founder","has_email":false,"organization":{"name":"Northwind"}},
          {"id":"../../admin","first_name":"Bad","title":"x"}
        ]}
        """;

    // Shape of Apollo's POST /api/v1/people/match reply (docs.apollo.io/reference/people-enrichment).
    private const string ApolloMatch = """
        {"request_id":1,"person":{"id":"67bdafd0c3a4c50001bbd7c2","first_name":"Maya","last_name":"Patel","name":"Maya Patel","title":"Head of Talent",
          "email":"maya@northwind.example","email_status":"verified","linkedin_url":"http://www.linkedin.com/in/mayapatel","match_confidence":"high"}}
        """;

    private static FakeConnectorHttp Services(int status = 200) => new(r =>
        status != 200 ? new(status, """{"errors":[{"id":"x","code":1,"details":"Upstream said no."}]}""") : r.Url switch
        {
            var u when u.StartsWith("https://api.hunter.io/v2/account", StringComparison.Ordinal) => new(200, """{"data":{"email":"asha@agency.example","plan_name":"Free","requests":{"searches":{"used":3,"available":25}}}}"""),
            var u when u.StartsWith("https://api.hunter.io/v2/domain-search", StringComparison.Ordinal) => new(200, HunterDomainSearch),
            var u when u.StartsWith("https://api.apollo.io/api/v1/auth/health", StringComparison.Ordinal) => new(200, """{"healthy":true,"is_logged_in":true}"""),
            var u when u.StartsWith("https://api.apollo.io/api/v1/mixed_people/api_search", StringComparison.Ordinal) => new(200, ApolloSearch),
            var u when u.StartsWith("https://api.apollo.io/api/v1/people/match", StringComparison.Ordinal) => new(200, ApolloMatch),
            _ => new(404, "{}")
        });

    [Theory]
    [InlineData("https://www.Northwind.example/careers?x=1", "northwind.example")]
    [InlineData("northwind.example", "northwind.example")]
    [InlineData("http://jobs.northwind.co.uk", "jobs.northwind.co.uk")]
    [InlineData("https://10.0.0.1", null)]
    [InlineData("https://localhost", null)]
    [InlineData("ftp://northwind.example", null)]
    [InlineData("not a site", null)]
    [InlineData("", null)]
    public void The_company_domain_comes_from_the_website_and_nothing_else(string website, string? expected) =>
        Assert.Equal(expected, PeopleSearch.DomainOf(website));

    [Fact]
    public void Titles_default_to_people_who_hire_and_people_who_buy()
    {
        Assert.Equal(PeopleSearch.DefaultTitles, PeopleSearch.Titles(null));
        Assert.Equal(PeopleSearch.DefaultTitles, PeopleSearch.Titles(["  ", ""]));
        Assert.Equal(["CTO", "VP Sales"], PeopleSearch.Titles([" CTO", "cto", "VP Sales"]));
    }

    [Fact]
    public void Hunter_people_are_parsed_wanted_titles_first_and_labelled_as_third_party()
    {
        var people = HunterSource.Parse(HunterDomainSearch, ["talent acquisition"]);

        Assert.Equal(["ravi@northwind.example", "asha@northwind.example", "careers@northwind.example"], people.Select(p => p.Email));
        var ravi = people[0];
        Assert.Equal(("Ravi Menon", "Senior Talent Acquisition Partner", 71, "https://www.linkedin.com/in/ravimenon"), (ravi.Name, ravi.Title, ravi.EmailConfidence, ravi.LinkedinUrl));
        Assert.Contains("seen on 1 public page,", ravi.Note);
        Assert.All(people, p => Assert.Equal("Hunter", p.Source));
        Assert.All(people, p => Assert.Contains("Third-party data", p.Note));
        Assert.Contains("Hunter's own check said \"valid\"", people[1].Note);
        Assert.Contains("A shared inbox", people[2].Note);
        Assert.Equal("careers@northwind.example", people[2].Name);
        Assert.Empty(HunterSource.Parse("not json", []));
    }

    [Fact]
    public async Task Hunter_sends_the_key_in_a_header_never_in_the_url()
    {
        var http = Services();
        var hunter = new HunterSource(http);
        Assert.Equal("Hunter Free plan (asha@agency.example)", await hunter.VerifyAsync(HunterKey, default));
        await hunter.FindAsync(HunterKey, "northwind.example", PeopleSearch.DefaultTitles, default);

        Assert.All(http.Calls, c => Assert.DoesNotContain(HunterKey, c.Url));
        Assert.All(http.Calls, c => Assert.Equal(HunterKey, c.Headers!["X-API-KEY"]));
        Assert.Equal("https://api.hunter.io/v2/domain-search?domain=northwind.example&limit=10&department=hr,executive,management,it", http.Calls[^1].Url);
    }

    [Fact]
    public async Task Apollo_search_hides_emails_and_reveal_returns_the_work_email()
    {
        var http = Services();
        var apollo = new ApolloSource(http);
        Assert.Equal("Apollo account", await apollo.VerifyAsync(ApolloKey, default));
        var people = await apollo.FindAsync(ApolloKey, "northwind.example", ["Head of Talent", "founder"], default);

        Assert.Equal(2, people.Count);   // the id that is not an Apollo id is dropped
        Assert.All(people, p => Assert.Null(p.Email));
        Assert.Equal(("Maya Pa***l", "67bdafd0c3a4c50001bbd7c2"), (people[0].Name, people[0].PersonId));
        Assert.Contains("uses an Apollo credit", people[0].Note);
        Assert.Contains("no email", people[1].Note);
        var search = http.Calls[^1];
        Assert.Equal(HttpMethod.Post, search.Method);
        Assert.Contains("q_organization_domains_list[]=northwind.example", search.Url);
        Assert.Contains("person_titles[]=Head%20of%20Talent", search.Url);

        var revealed = await apollo.RevealAsync(ApolloKey, "67bdafd0c3a4c50001bbd7c2", default);
        Assert.Equal(("Maya Patel", "maya@northwind.example", "http://www.linkedin.com/in/mayapatel"), (revealed!.Name, revealed.Email, revealed.LinkedinUrl));
        Assert.Contains("Apollo's own status: \"verified\"", revealed.Note);
        Assert.Contains("Third-party data", revealed.Note);
        Assert.Contains("reveal_personal_emails=false", http.Calls[^1].Url);
        Assert.All(http.Calls, c => Assert.Equal(ApolloKey, c.Headers!["x-api-key"]));
        Assert.All(http.Calls, c => Assert.DoesNotContain(ApolloKey, c.Url));
    }

    [Fact]
    public void Apollo_placeholder_emails_are_not_emails()
    {
        var p = ApolloSource.ParseMatch("""{"person":{"id":"abc","name":"Tom Lee","email":"email_not_unlocked@domain.com"}}""");
        Assert.Null(p!.Email);
        Assert.Contains("no work email", p.Note);
        Assert.Null(ApolloSource.ParseMatch("""{"person":null}"""));
    }

    [Theory]
    [InlineData(401, "refused the API key")]
    [InlineData(429, "limit for this key is used up")]
    [InlineData(0, "could not be reached")]
    [InlineData(500, "answered 500: Upstream said no.")]
    public async Task Service_refusals_are_explained_without_the_key_or_the_url(int status, string expected)
    {
        foreach (IPeopleSource source in new IPeopleSource[] { new HunterSource(Services(status)), new ApolloSource(Services(status)) })
        {
            var ex = await Assert.ThrowsAsync<ConnectorProblem>(() => source.FindAsync(HunterKey, "northwind.example", ["CTO"], default));
            Assert.Contains(expected, ex.Message);
            Assert.DoesNotContain("SECRET", ex.Message);
            Assert.DoesNotContain("https://", ex.Message);
        }
    }

    [Fact]
    public async Task Apollo_key_without_access_to_people_search_says_what_key_to_make()
    {
        var http = new FakeConnectorHttp(r => r.Url.Contains("/auth/health", StringComparison.Ordinal) ? new(200, """{"is_logged_in":true}""")
            : new(403, """{"error":"api/v1/mixed_people/api_search is not accessible with this api_key","error_code":"API_INACCESSIBLE"}"""));
        var ex = await Assert.ThrowsAsync<ConnectorProblem>(() => new ApolloSource(http).VerifyAsync(ApolloKey, default));
        Assert.Contains("People API Search", ex.Message);
        Assert.EndsWith("Free Apollo accounts need a work email address. Apollo said: api/v1/mixed_people/api_search is not accessible with this api_key.", ex.Message);

        var loggedOut = new FakeConnectorHttp(_ => new(200, """{"healthy":true,"is_logged_in":false}"""));
        Assert.Contains("refused the API key", (await Assert.ThrowsAsync<ConnectorProblem>(() => new ApolloSource(loggedOut).VerifyAsync("bad", default))).Message);
    }

    // ---- The service: owner scoping, connection, domain, and what is written.

    private sealed class Owner(Guid id) : ICurrentUser { public Guid OwnerId => id; }
    private sealed class Clock(DateTime now) : TimeProvider { public override DateTimeOffset GetUtcNow() => new(now); }
    private static readonly MailOptions Mail = new() { TokenKey = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray()) };

    private sealed record Setup(PeopleFinder Finder, InMemoryAppDbContext Db, FakeConnectorHttp Http, Opportunity Company, Opportunity Job, Guid Owner);

    private static async Task<Setup> Create(int status = 200, bool connect = true)
    {
        var db = new InMemoryAppDbContext(new DbContextOptionsBuilder<InMemoryAppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var owner = Guid.NewGuid();
        var company = new Opportunity(owner, Guid.NewGuid(), OpportunityMode.Customer, "customer:northwind", T0);
        company.ApplyResearch("Payroll buyer", "Northwind", "London", "https://www.northwind.example", null, null, null, null, 80, 80, FilterOutcome.Qualified, null, "[]", "[]", "[]", 0, Guid.NewGuid(), T0);
        var job = new Opportunity(owner, Guid.NewGuid(), OpportunityMode.Job, "job:board:1", T0);
        job.ApplyResearch(".NET developer", "Northwind", "London", "https://boards.example/jobs/1", null, null, null, null, 80, 80, FilterOutcome.Qualified, null, "[]", "[]", "[]", 0, Guid.NewGuid(), T0);
        var protector = new TokenProtector(Options.Create(Mail));
        db.AddRange(company, job);
        if (connect)
        {
            db.Add(new IntegrationConnection(owner, IntegrationKind.Hunter, "Hunter", protector.Protect(HunterKey), "{}", "", T0));
            db.Add(new IntegrationConnection(owner, IntegrationKind.Apollo, "Apollo", protector.Protect(ApolloKey), "{}", "", T0));
        }
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var http = Services(status);
        var finder = new PeopleFinder(db, new Owner(owner), new Clock(T0), protector, [new HunterSource(http), new ApolloSource(http)]);
        return new(finder, db, http, company, job, owner);
    }

    [Fact]
    public async Task A_company_lead_uses_its_website_and_records_only_a_note()
    {
        var s = await Create();
        var result = await s.Finder.FindAsync(s.Company.Id, new("hunter", null, null), default);

        Assert.Equal(("Hunter", "northwind.example", 3), (result.Source, result.Domain, result.People.Count));
        Assert.Contains("Hunter plan", result.CreditsNote);
        var note = Assert.Single(await s.Db.Activities.ToListAsync());
        Assert.Equal("Looked up 3 people at northwind.example with Hunter (third-party data, not verified).", note.Detail);
        Assert.DoesNotContain("@", note.Detail);
        Assert.NotNull((await s.Db.IntegrationConnections.SingleAsync(c => c.Kind == IntegrationKind.Hunter)).LastUsedAt);
    }

    [Fact]
    public async Task A_job_lead_needs_the_company_website_because_its_link_is_the_job_board()
    {
        var s = await Create();
        var ex = await Assert.ThrowsAsync<RequestValidationException>(() => s.Finder.FindAsync(s.Job.Id, new("Apollo", null, null), default));
        Assert.Equal("Add the company website first.", ex.Errors["website"][0]);
        Assert.Empty(s.Http.Calls);

        var result = await s.Finder.FindAsync(s.Job.Id, new("Apollo", ["Engineering Manager"], "https://northwind.example"), default);
        Assert.Equal("northwind.example", result.Domain);
        Assert.Contains("no credits", result.CreditsNote);
    }

    [Fact]
    public async Task Not_connected_is_409_and_someone_elses_lead_is_404()
    {
        var s = await Create(connect: false);
        Assert.Equal("Connect Hunter in Connectors first.", (await Assert.ThrowsAsync<ConflictException>(() => s.Finder.FindAsync(s.Company.Id, new("Hunter", null, null), default))).Message);
        await Assert.ThrowsAsync<NotFoundException>(() => s.Finder.FindAsync(Guid.NewGuid(), new("Hunter", null, null), default));
        await Assert.ThrowsAsync<RequestValidationException>(() => s.Finder.FindAsync(s.Company.Id, new("HubSpot", null, null), default));
    }

    [Fact]
    public async Task A_refused_key_is_a_409_with_the_reason_and_is_kept_as_the_last_error()
    {
        var s = await Create(status: 401);
        var ex = await Assert.ThrowsAsync<ConflictException>(() => s.Finder.FindAsync(s.Company.Id, new("Hunter", null, null), default));
        Assert.Contains("refused the API key", ex.Message);
        Assert.Contains("refused the API key", (await s.Db.IntegrationConnections.SingleAsync(c => c.Kind == IntegrationKind.Hunter)).LastError);
        Assert.Empty(await s.Db.Activities.ToListAsync());
    }

    [Fact]
    public async Task Reveal_is_apollo_only_and_records_that_a_credit_was_used()
    {
        var s = await Create();
        await Assert.ThrowsAsync<RequestValidationException>(() => s.Finder.RevealAsync(s.Company.Id, new("Hunter", "67bdafd0c3a4c50001bbd7c2"), default));
        await Assert.ThrowsAsync<RequestValidationException>(() => s.Finder.RevealAsync(s.Company.Id, new("Apollo", "../x"), default));

        var result = await s.Finder.RevealAsync(s.Company.Id, new("Apollo", "67bdafd0c3a4c50001bbd7c2"), default);
        Assert.Equal("maya@northwind.example", result.Person.Email);
        Assert.Contains("Apollo credits", result.CreditsNote);
        Assert.Contains("uses an Apollo credit", Assert.Single(await s.Db.Activities.ToListAsync()).Detail);
    }
}
