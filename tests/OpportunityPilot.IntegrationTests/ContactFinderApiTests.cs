using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using OpportunityPilot.Application.Research;

namespace OpportunityPilot.IntegrationTests;

/// <summary>A7: suggestions come only from text that is written down, the fetch is bounded to three pages of the company's own site, and nothing is stored.</summary>
public class ContactFinderApiTests(PostgresApiFactory factory) : IClassFixture<PostgresApiFactory>
{
    private sealed class FakeSite : IWebFetcher
    {
        public List<string> Fetched { get; } = [];
        public string? CheckUrl(string url) => url.StartsWith("https://", StringComparison.Ordinal) || url.StartsWith("http://", StringComparison.Ordinal) ? null : "Use a web address starting with https://.";
        public Task<FetchResult> FetchAsync(string url, CancellationToken ct, int? maxBytes = null)
        {
            Fetched.Add(url);
            return Task.FromResult(url.EndsWith("/contact", StringComparison.Ordinal)
                ? new FetchResult(true, "<p>Sales: <a href=\"mailto:sales@northwind.example\">sales@northwind.example</a></p>", "text/html", url, null, 1)
                : url.EndsWith("/about", StringComparison.Ordinal)
                    ? new FetchResult(true, "<p>Elsewhere: info@other-company.test</p>", "text/html", "https://other-company.test/about", null, 1)
                    : FetchResult.Fail("Not found.", 1, 404));
        }
        public Task<FetchResult> FetchJsonAsync(string url, CancellationToken ct, int? maxBytes = null) => Task.FromResult(FetchResult.Fail("offline", 1));
        public Task<FetchResult> PostJsonAsync(string url, string jsonBody, CancellationToken ct) => throw new InvalidOperationException();
    }

    private const string Company = """
        Northwind Integrations
        Website: https://northwind.example
        Location: London
        Northwind implements Dynamics 365. Hiring contact: careers@northwind.example.
        """;

    private WebApplicationFactory<Program> App(FakeSite site) => factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IWebFetcher>(site)));

    [Fact]
    public async Task Suggests_published_contacts_from_the_posting_and_the_company_contact_page_and_ignores_pages_that_redirect_elsewhere()
    {
        var site = new FakeSite();
        await using var app = App(site);
        var user = PostgresApiFactory.ClientFor(app, "contacts@example.test");
        var campaign = await ResearchApi.CreateCampaignAsync(user, "Customer", new { keywords = new[] { "Dynamics 365" }, locations = new[] { "London" } });
        await ResearchApi.AddPasteAsync(user, campaign.Id(), Company);
        await ResearchApi.QueueAsync(user, campaign.Id());
        await PostgresApiFactory.RunResearchAsync(app.Services);
        var id = Assert.Single(await ResearchApi.OpportunitiesAsync(user, campaign.Id())).Id();

        var result = await (await user.PostAsJsonAsync($"/api/v1/opportunities/{id}/contacts/find", new { })).Json(HttpStatusCode.OK);
        var values = result.GetProperty("candidates").EnumerateArray().Select(c => c.Str("value")).ToList();
        Assert.Contains("careers@northwind.example", values);   // written in the saved posting
        Assert.Contains("sales@northwind.example", values);     // published on /contact
        Assert.DoesNotContain("info@other-company.test", values); // /about redirected off the company's domain
        Assert.True(site.Fetched.Count <= 3);
        Assert.All(site.Fetched, u => Assert.StartsWith("https://northwind.example/", u));
        Assert.Equal(site.Fetched.Count, result.GetProperty("pagesChecked").GetArrayLength());
        var first = result.GetProperty("candidates")[0];
        Assert.True(first.GetProperty("matchesCompanySite").GetBoolean());
        Assert.False(string.IsNullOrEmpty(first.Str("sourceUrl")));
    }

    [Fact]
    public async Task Refuses_a_bad_website_and_never_shows_another_persons_opportunity()
    {
        var site = new FakeSite();
        await using var app = App(site);
        var owner = PostgresApiFactory.ClientFor(app, "contacts-owner@example.test");
        var other = PostgresApiFactory.ClientFor(app, "contacts-other@example.test");
        var campaign = await ResearchApi.CreateCampaignAsync(owner, "Customer", new { keywords = new[] { "Dynamics 365" }, locations = new[] { "London" } });
        await ResearchApi.AddPasteAsync(owner, campaign.Id(), Company);
        await ResearchApi.QueueAsync(owner, campaign.Id());
        await PostgresApiFactory.RunResearchAsync(app.Services);
        var id = Assert.Single(await ResearchApi.OpportunitiesAsync(owner, campaign.Id())).Id();

        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync($"/api/v1/opportunities/{id}/contacts/find", new { website = "ftp://northwind.example" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync($"/api/v1/opportunities/{id}/contacts/find", new { })).StatusCode);
        Assert.Empty(site.Fetched);
    }
}
