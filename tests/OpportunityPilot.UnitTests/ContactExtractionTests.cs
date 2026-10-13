using OpportunityPilot.Application.Opportunities;

namespace OpportunityPilot.UnitTests;

public class ContactExtractionTests
{
    private const string Html = """
        <a href="mailto:Careers@Northwind.example">Careers</a>
        <p>Sales: sales@northwind.example, or Asha Rao at asha.rao@northwind.example.</p>
        <img src="logo@2x.png"> <p>Billing: someone@gmail.com. Fake: name@example.com</p>
        <a href="https://www.linkedin.com/company/northwind-integrations/">LinkedIn</a>
        """;

    [Fact]
    public void Only_addresses_written_in_the_text_are_returned_with_their_source_and_without_noise()
    {
        var found = ContactExtraction.Extract(Html, "https://northwind.example/contact", "Published on /contact", "northwind.example", jobSearch: false);
        var emails = found.Where(c => c.Kind == "Email").Select(c => c.Value).ToList();
        Assert.Contains("careers@northwind.example", emails);
        Assert.Contains("sales@northwind.example", emails);
        Assert.Contains("asha.rao@northwind.example", emails);
        Assert.Contains("someone@gmail.com", emails);
        Assert.DoesNotContain(emails, e => e.EndsWith(".png") || e.Contains("example.com"));
        Assert.All(found, c => Assert.Equal("https://northwind.example/contact", c.SourceUrl));
        Assert.Contains(found, c => c.Kind == "LinkedIn" && c.Value.StartsWith("https://www.linkedin.com/company/northwind-integrations"));
    }

    [Fact]
    public void Company_domain_role_inboxes_rank_first_and_the_right_inbox_for_the_search_type_wins()
    {
        var sales = ContactExtraction.Extract(Html, "u", "n", "northwind.example", jobSearch: false);
        Assert.Equal("sales@northwind.example", sales.First(c => c.Kind == "Email").Value);
        var job = ContactExtraction.Extract(Html, "u", "n", "northwind.example", jobSearch: true);
        Assert.Equal("careers@northwind.example", job.First(c => c.Kind == "Email").Value);
        // A personal address is labelled as such and ranks below the role inboxes on the company's domain.
        var personal = sales.First(c => c.Value == "asha.rao@northwind.example");
        Assert.Contains("Personal address", personal.Label);
        Assert.True(personal.MatchesCompanySite);
        Assert.False(sales.First(c => c.Value == "someone@gmail.com").MatchesCompanySite);
        Assert.True(sales.First(c => c.Value == "sales@northwind.example").Rank > personal.Rank);
    }

    [Fact]
    public void Nothing_is_invented_when_the_text_has_no_address_and_obfuscated_ones_are_left_alone()
    {
        Assert.Empty(ContactExtraction.Extract("Write to us: team [at] northwind [dot] example", "u", "n", "northwind.example", false));
        Assert.Empty(ContactExtraction.Extract(null, "u", "n", null, false));
    }

    [Fact]
    public void Lists_are_merged_keeping_the_best_copy_and_capped()
    {
        var a = ContactExtraction.Extract("sales@northwind.example", "page1", "n", null, false);
        var b = ContactExtraction.Extract("sales@northwind.example", "page2", "n", "northwind.example", false);
        var merged = ContactExtraction.Merge([a, b]);
        var one = Assert.Single(merged);
        Assert.Equal("page2", one.SourceUrl);
        var many = Enumerable.Range(0, 20).Select(i => ContactExtraction.Extract($"p{i}@x{i}.test", "u", "n", null, false));
        Assert.Equal(ContactExtraction.MaxCandidates, ContactExtraction.Merge(many).Count);
    }

    [Theory]
    [InlineData("https://www.Northwind.example/path", "northwind.example")]
    [InlineData("http://shop.northwind.example", "shop.northwind.example")]
    [InlineData("ftp://northwind.example", null)]
    [InlineData("northwind.example", null)]
    [InlineData(null, null)]
    public void The_company_host_is_read_from_a_plain_web_address_only(string? url, string? host) => Assert.Equal(host, ContactExtraction.HostOf(url));
}
