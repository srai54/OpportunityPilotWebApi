using System.Text.Json;
using Microsoft.Extensions.Options;
using OpportunityPilot.Application.Common;
using OpportunityPilot.Application.JobBoards;
using OpportunityPilot.Application.Sales;

namespace OpportunityPilot.UnitTests;

public sealed class LeadSearchTests
{
    private sealed class FakeHttp(int status, string? body) : ILeadSearchHttp
    {
        public string? Path, Json, Key;
        public Task<(int Status, string? Body)> PostAsync(string host, string path, string key, string json, CancellationToken ct)
        {
            Path = path; Json = json; Key = key;
            return Task.FromResult((status, body));
        }
    }

    private static LeadSearchService Service(FakeHttp http, string? key = "k", int perDay = 20) => new(http, Options.Create(new LeadSearchOptions()),
        Options.Create(new RapidApiBoardsOptions { Key = key }), new ProviderBudget(TimeProvider.System, Options.Create(new BudgetOptions { ApolloPerDay = perDay })));

    [Fact]
    public void Body_uses_apollo_filter_names_and_size_ranges()
    {
        var json = LeadSearchService.BuildBody(new("SaaS, fintech", ["United States", "Sweden", "Germany"], ["10-20", "20-50", "50-100"]), new LeadSearchOptions());
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
        var r = await Service(http).SearchAsync(new("saas", ["Sweden"], ["20-50"]), default);
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
    public async Task Unsubscribed_and_unreadable_answers_say_so()
    {
        Assert.Contains("not subscribed", (await Service(new FakeHttp(403, "{}")).SearchAsync(new("x", null, null), default)).Message);
        Assert.Equal("Failed", (await Service(new FakeHttp(200, """{"weird":1}""")).SearchAsync(new("x", null, null), default)).Status);
    }
}
