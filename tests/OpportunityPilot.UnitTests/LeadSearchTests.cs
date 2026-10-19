using System.Text.Json;
using Microsoft.Extensions.Options;
using OpportunityPilot.Application.Common;
using OpportunityPilot.Application.Configuration;
using OpportunityPilot.Application.JobBoards;
using OpportunityPilot.Application.Sales;

namespace OpportunityPilot.UnitTests;

public sealed class LeadSearchTests
{
    private sealed class FakeHttp(int status, string? body) : ILeadSearchHttp
    {
        public string? Path, Json, Key, Method;
        public Task<(int Status, string? Body)> SendAsync(string method, string host, string pathAndQuery, string key, string? json, CancellationToken ct)
        {
            Method = method; Path = pathAndQuery.Split('?')[0]; Query = pathAndQuery.Contains('?') ? pathAndQuery.Split('?')[1] : ""; Json = json; Key = key;
            return Task.FromResult((status, body));
        }
        public string Query = "";
    }

    private static LeadSearchService Service(FakeHttp http, string? key = "k", int perDay = 20, LeadSearchOptions? o = null) => new(http, Options.Create(o ?? new LeadSearchOptions()),
        Options.Create(new RapidApiBoardsOptions { Key = key }), new ProviderBudget(TimeProvider.System, Options.Create(new BudgetOptions { ApolloPerDay = perDay })));

    [Fact]
    public void Body_uses_apollo_filter_names_and_size_ranges()
    {
        var json = LeadSearchService.BuildBody(new("SaaS, fintech", ["United States", "Sweden", "Germany"], ["10-20", "20-50", "50-100"]), new LeadSearchOptions { SizeParam = "organization_num_employees_ranges" });
        var b = JsonDocument.Parse(json).RootElement;
        Assert.Equal(["SaaS", "fintech"], b.GetProperty("q_organization_keyword_tags").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(["United States", "Sweden", "Germany"], b.GetProperty("organization_locations").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(["10,20", "21,50", "51,100"], b.GetProperty("organization_num_employees_ranges").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(10, b.GetProperty("per_page").GetInt32());
    }

    [Fact]
    public void Validation_rejects_unknown_sizes_and_empty_searches()
    {
        Assert.Contains("sizes", LeadSearchService.Validate(new("x", null, ["5-10"])).Keys);
        Assert.Contains("keywords", LeadSearchService.Validate(new(null, null, null)).Keys);
        Assert.Empty(LeadSearchService.Validate(new(null, ["Sweden"], null)));
    }

    [Fact]
    public async Task Maps_companies_and_keeps_only_the_chosen_sizes()
    {
        var body = """{"organizations":[{"name":"Acme AB","website_url":"acme.se","linkedin_url":"http://www.linkedin.com/company/acme","estimated_num_employees":35,"city":"Stockholm","country":"Sweden"},{"name":"Big GmbH","estimated_num_employees":900,"country":"Germany"},{"name":"Unknown Inc"}]}""";
        var http = new FakeHttp(200, body);
        var r = await Service(http, o: new LeadSearchOptions { SizeParam = "organization_num_employees_ranges" }).SearchAsync(new("saas", ["Sweden"], ["20-50"]), default);
        Assert.Equal("Ready", r.Status);
        Assert.Equal("/search_organizations", http.Path);
        Assert.Equal(["Acme AB", "Unknown Inc"], r.Leads.Select(l => l.Name));
        Assert.Equal(1, r.Hidden);
        var acme = r.Leads[0];
        Assert.Equal("20-50", acme.SizeBand);
        Assert.Equal("Stockholm, Sweden", acme.Location);
        Assert.Equal("https://acme.se/", acme.Website);
    }

    [Fact]
    public async Task Sends_a_get_with_apollo_query_parameters()
    {
        var http = new FakeHttp(200, "{\"data\":{\"people\":[]}}");
        await Service(http).SearchAsync(new(null, ["Sweden", "Germany"], ["10-20", "50-100"], "people", "CTO, Head of Engineering"), default);
        Assert.Equal("GET", http.Method);
        Assert.Equal("/search_people", http.Path);
        Assert.Null(http.Json);
        // Live format (2026-10-19): one comma-separated value per filter; no size filter (the listing rejected it).
        Assert.Equal("page=1&per_page=10&person_locations=Sweden%2CGermany&person_titles=CTO%2CHead%20of%20Engineering", http.Query);
    }

    /// <summary>Trimmed from the listing's real Search People answer (2026-10-19): data.people[], organization without a head count.</summary>
    [Fact]
    public async Task Reads_the_real_search_people_answer()
    {
        var body = """{"data":{"people":[{"id":"557147df7369645875b82900","first_name":"Ali","last_name":"Ali","name":"Ali Ali","linkedin_url":"http://www.linkedin.com/in/ali-al-ali-81221292","title":"Vice President/ Senior Corporate Banker","city":"Al Khobar","state":"Eastern Province","country":"Saudi Arabia","email":"email_not_unlocked@domain.com","organization":{"id":"606e9acf53d20100fe309fca","name":"The Saudi National Bank - SNB","website_url":"http://www.alahli.com","linkedin_url":"http://www.linkedin.com/company/snbalahli","founded_year":1953}},{"id":"54c285377468691639b72ba2","name":"Ali Ali","title":"Senior Vice President, Strategic Partnerships","linkedin_url":"http://www.linkedin.com/in/ali-al-ali-b149417b","city":null,"country":"United Arab Emirates","organization":{"name":"Emirates Development Bank (EDB)","website_url":"http://www.edb.gov.ae"}}]}}""";
        var r = await Service(new FakeHttp(200, body)).SearchAsync(new(null, ["Sweden"], ["10-20"], "people", "CTO"), default);
        Assert.Equal("Ready", r.Status);
        Assert.Equal(2, r.Leads.Count);
        Assert.Equal(0, r.Hidden); // no head count in this answer: nothing is hidden by size
        var first = r.Leads[0];
        Assert.Equal(("Ali Ali", "The Saudi National Bank - SNB", "Vice President/ Senior Corporate Banker", "Al Khobar, Saudi Arabia"), (first.Name, first.Company, first.Title, first.Location));
        Assert.Equal("http://www.alahli.com/", first.Website);
        Assert.Equal("http://www.linkedin.com/in/ali-al-ali-81221292", first.LinkedIn);
        Assert.Null(first.Employees);
    }

    [Fact]
    public async Task Maps_people_with_their_company()
    {
        var body = """{"people":[{"first_name":"Ana","last_name":"Berg","title":"CTO","linkedin_url":"https://linkedin.com/in/ana","country":"Sweden","organization":{"name":"Acme AB","estimated_num_employees":15}}]}""";
        var r = await Service(new FakeHttp(200, body)).SearchAsync(new(null, ["Sweden"], null, "people", "CTO"), default);
        var p = Assert.Single(r.Leads);
        Assert.Equal(("Ana Berg", "Acme AB", "CTO", "10-20"), (p.Name, p.Company, p.Title, p.SizeBand));
    }

    [Fact]
    public async Task No_key_no_call_and_daily_cap_stops_calls()
    {
        var http = new FakeHttp(200, "{}");
        Assert.Equal("NotConfigured", (await Service(http, key: null).SearchAsync(new("x", null, null), default)).Status);
        Assert.Null(http.Path);
        var capped = Service(http, perDay: 0);
        Assert.Equal("DailyLimit", (await capped.SearchAsync(new("x", null, null), default)).Status);
    }

    [Fact]
    public async Task Uses_the_jsearch_key_when_no_rapidapi_key_is_set()
    {
        var http = new FakeHttp(200, "{\"organizations\":[]}");
        var service = new LeadSearchService(http, Options.Create(new LeadSearchOptions()), Options.Create(new RapidApiBoardsOptions()),
            new ProviderBudget(TimeProvider.System, Options.Create(new BudgetOptions())), Options.Create(new JsearchOptions { Key = "account-key" }));
        Assert.Equal("Ready", (await service.SearchAsync(new("x", null, null), default)).Status);
        Assert.Equal("account-key", http.Key);
    }

    [Fact]
    public async Task Unsubscribed_and_unreadable_answers_say_so()
    {
        Assert.Contains("not subscribed", (await Service(new FakeHttp(403, "{}")).SearchAsync(new("x", null, null), default)).Message);
        Assert.Equal("Failed", (await Service(new FakeHttp(200, """{"weird":1}""")).SearchAsync(new("x", null, null), default)).Status);
    }
}
