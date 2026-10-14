using System.Text.Json;
using OpportunityPilot.Application.Integrations;
using OpportunityPilot.Domain.Integrations;

namespace OpportunityPilot.UnitTests;

/// <summary>A connected service answering with canned replies, recording every request.</summary>
internal sealed class FakeConnectorHttp(Func<ConnectorRequest, ConnectorResponse> answer) : IConnectorHttp
{
    public List<ConnectorRequest> Calls { get; } = [];
    public Task<ConnectorResponse> SendAsync(ConnectorRequest request, CancellationToken ct)
    {
        Calls.Add(request);
        return Task.FromResult(answer(request));
    }
}

public class IntegrationConnectorTests
{
    [Theory]
    [InlineData(IntegrationKind.Slack, "https://hooks.slack.com/services/T0/B0/xyz", true)]
    [InlineData(IntegrationKind.Slack, "https://hooks.slack.com.evil.test/services/x", false)]
    [InlineData(IntegrationKind.Slack, "http://hooks.slack.com/services/x", false)]
    [InlineData(IntegrationKind.Teams, "https://prod-12.westus.logic.azure.com/workflows/abc/triggers/manual/paths/invoke?sig=1", true)]
    [InlineData(IntegrationKind.Teams, "https://contoso.webhook.office.com/webhookb2/abc", true)]
    [InlineData(IntegrationKind.Teams, "https://hooks.slack.com/services/x", false)]
    [InlineData(IntegrationKind.Webhook, "https://hooks.zapier.com/hooks/catch/1/abc/", true)]
    [InlineData(IntegrationKind.Webhook, "https://example.com:8443/hook", false)]
    [InlineData(IntegrationKind.Webhook, "https://user:pw@example.com/hook", false)]
    [InlineData(IntegrationKind.Webhook, "", false)]
    public void Webhook_addresses_must_be_https_on_the_services_own_domain(IntegrationKind kind, string url, bool ok) =>
        Assert.Equal(ok, WebhookPoster.CheckUrl(kind, url) is null);

    [Fact]
    public void Slack_text_cannot_turn_posting_text_into_mentions_or_links()
    {
        var body = WebhookPoster.Body(IntegrationKind.Slack, new ChannelMessage("sent", "Email sent", ["To <!channel> & co"]), DateTime.UtcNow);
        var text = JsonDocument.Parse(body).RootElement.GetProperty("text").GetString()!;
        Assert.Contains("&lt;!channel&gt; &amp; co", text);
        Assert.DoesNotContain("<!channel>", text);
    }

    [Fact]
    public void Teams_gets_an_adaptive_card_and_other_webhooks_get_plain_json()
    {
        var teams = JsonDocument.Parse(WebhookPoster.Body(IntegrationKind.Teams, new ChannelMessage("leads", "New leads", ["3 new"], "https://app.test"), DateTime.UtcNow)).RootElement;
        var card = teams.GetProperty("attachments")[0];
        Assert.Equal("application/vnd.microsoft.card.adaptive", card.GetProperty("contentType").GetString());
        Assert.Equal("New leads", card.GetProperty("content").GetProperty("body")[0].GetProperty("text").GetString());

        var hook = JsonDocument.Parse(WebhookPoster.Body(IntegrationKind.Webhook, new ChannelMessage("leads", "New leads", ["3 new"]), DateTime.UtcNow)).RootElement;
        Assert.Equal("leads", hook.GetProperty("event").GetString());
        Assert.Equal("3 new", hook.GetProperty("lines")[0].GetString());
    }

    [Fact]
    public async Task A_removed_webhook_is_reported_in_words_the_person_can_act_on()
    {
        var http = new FakeConnectorHttp(_ => new(404, "no_service"));
        var ex = await Assert.ThrowsAsync<ConnectorProblem>(() => WebhookPoster.PostAsync(http, IntegrationKind.Slack, "https://hooks.slack.com/services/x", new("test", "t", []), DateTime.UtcNow, default));
        Assert.Contains("Make a new webhook", ex.Message);
    }

    [Theory]
    [InlineData("https://www.notion.so/team/Leads-1a2b3c4d5e6f47a8b9c0d1e2f3a4b5c6?v=99887766554433221100aabbccddeeff", "1a2b3c4d5e6f47a8b9c0d1e2f3a4b5c6")]
    [InlineData("1a2b3c4d-5e6f-47a8-b9c0-d1e2f3a4b5c6", "1a2b3c4d5e6f47a8b9c0d1e2f3a4b5c6")]
    [InlineData("not a link", null)]
    public void Notion_database_id_is_read_from_the_link_not_the_view(string input, string? expected) =>
        Assert.Equal(expected, NotionTarget.DatabaseId(input));

    [Fact]
    public void Airtable_base_and_table_are_read_from_the_link()
    {
        Assert.Equal(("appAbCdEfGh123456", "tblZyXwVuT9876543"), AirtableTarget.Ids("https://airtable.com/appAbCdEfGh123456/tblZyXwVuT9876543/viwQ"));
        Assert.Equal((null, null), AirtableTarget.Ids("https://example.com"));
    }

    private static readonly LeadRecord Lead = new(Guid.NewGuid(), "Northwind", "Payroll for SMEs", "https://northwind.example", "northwind.example", "London", 82, "Shortlisted",
        "https://northwind.example", "sales@northwind.example", "Notes");

    [Fact]
    public async Task HubSpot_creates_the_company_links_the_contact_and_adds_a_note()
    {
        var http = new FakeConnectorHttp(r => r.Url switch
        {
            var u when u.EndsWith("/search", StringComparison.Ordinal) => new(200, """{"results":[]}"""),
            var u when u.EndsWith("/crm/v3/objects/companies", StringComparison.Ordinal) => new(201, """{"id":"101"}"""),
            var u when u.EndsWith("/crm/v3/objects/contacts", StringComparison.Ordinal) => new(201, """{"id":"202"}"""),
            _ => new(200, "{}")
        });
        var result = await new HubSpotTarget(http).PushAsync("pat-1", """{"portalId":"42","uiDomain":"app-eu1.hubspot.com"}""", Lead, default);

        Assert.Equal("Added", result.Outcome);
        Assert.Equal("https://app-eu1.hubspot.com/contacts/42/record/0-2/101", result.Link);
        Assert.Contains(http.Calls, c => c.Url.EndsWith("/contacts/202/associations/default/companies/101", StringComparison.Ordinal));
        var note = Assert.Single(http.Calls, c => c.Url.EndsWith("/notes", StringComparison.Ordinal));
        Assert.Contains("190", note.JsonBody);
        Assert.All(http.Calls, c => Assert.Equal("pat-1", c.Bearer));
        // The company search used the web domain, not the name.
        Assert.Contains("northwind.example", http.Calls[0].JsonBody);
    }

    [Fact]
    public async Task HubSpot_does_not_duplicate_a_company_that_is_already_there()
    {
        var http = new FakeConnectorHttp(r => r.Url.EndsWith("/search", StringComparison.Ordinal) ? new(200, """{"results":[{"id":"7"}]}""") : new(500, ""));
        var result = await new HubSpotTarget(http).PushAsync("pat-1", """{"portalId":"42"}""", Lead, default);
        Assert.Equal("AlreadyThere", result.Outcome);
        Assert.Single(http.Calls);
    }

    [Fact]
    public async Task HubSpot_refused_token_says_so()
    {
        var http = new FakeConnectorHttp(_ => new(401, """{"message":"Authentication credentials not found."}"""));
        var ex = await Assert.ThrowsAsync<ConnectorProblem>(() => new HubSpotTarget(http).VerifyAsync("bad", null, null, default));
        Assert.Contains("refused the token", ex.Message);
    }

    [Fact]
    public async Task Notion_maps_only_columns_the_database_has()
    {
        var schema = """
            {"title":[{"plain_text":"Leads"}],"properties":{
              "Company":{"type":"title"},"Website":{"type":"url"},"Match score":{"type":"number"},"Where":{"type":"rich_text"},"Location":{"type":"rich_text"}}}
            """;
        var http = new FakeConnectorHttp(r => r.Method == HttpMethod.Get ? new(200, schema)
            : r.Url.EndsWith("/query", StringComparison.Ordinal) ? new(200, """{"results":[]}""") : new(200, """{"url":"https://notion.so/p1"}"""));
        var target = new NotionTarget(http);
        var (label, settings) = await target.VerifyAsync("ntn_x", "https://www.notion.so/Leads-1a2b3c4d5e6f47a8b9c0d1e2f3a4b5c6", null, default);
        Assert.Equal("Notion: Leads", label);
        Assert.Equal("2022-06-28", http.Calls[0].Headers!["Notion-Version"]);

        var result = await target.PushAsync("ntn_x", settings, Lead, default);
        Assert.Equal("https://notion.so/p1", result.Link);
        var props = JsonDocument.Parse(http.Calls[^1].JsonBody!).RootElement.GetProperty("properties");
        Assert.Equal("Northwind", props.GetProperty("Company").GetProperty("title")[0].GetProperty("text").GetProperty("content").GetString());
        Assert.Equal("https://northwind.example", props.GetProperty("Website").GetProperty("url").GetString());
        Assert.Equal(82, props.GetProperty("Match score").GetProperty("number").GetInt32());
        Assert.Equal("London", props.GetProperty("Location").GetProperty("rich_text")[0].GetProperty("text").GetProperty("content").GetString());
        Assert.False(props.TryGetProperty("Where", out _));
    }

    [Fact]
    public async Task Notion_says_how_to_share_a_database_it_cannot_see()
    {
        var http = new FakeConnectorHttp(_ => new(404, "{}"));
        var ex = await Assert.ThrowsAsync<ConnectorProblem>(() => new NotionTarget(http).VerifyAsync("ntn_x", "1a2b3c4d5e6f47a8b9c0d1e2f3a4b5c6", null, default));
        Assert.Contains("Connections", ex.Message);
    }

    [Fact]
    public async Task Airtable_fills_matching_columns_and_escapes_the_duplicate_check()
    {
        var tables = """
            {"tables":[{"id":"tblZyXwVuT9876543","name":"Leads","primaryFieldId":"fld1","fields":[
              {"id":"fld1","name":"Name","type":"singleLineText"},{"id":"fld2","name":"Website","type":"url"},{"id":"fld3","name":"Email","type":"email"},
              {"id":"fld4","name":"Score","type":"number"},{"id":"fld5","name":"Owner","type":"singleLineText"}]}]}
            """;
        var http = new FakeConnectorHttp(r => r.Url.Contains("/meta/", StringComparison.Ordinal) ? new(200, tables)
            : r.Method == HttpMethod.Get ? new(200, """{"records":[]}""") : new(200, """{"records":[{"id":"rec1"}]}"""));
        var target = new AirtableTarget(http);
        var (label, settings) = await target.VerifyAsync("pat.x", "https://airtable.com/appAbCdEfGh123456", "leads", default);
        Assert.Equal("Airtable: Leads", label);

        var result = await target.PushAsync("pat.x", settings, Lead with { Name = "O'Brien & Co" }, default);
        Assert.Equal("https://airtable.com/appAbCdEfGh123456/tblZyXwVuT9876543/rec1", result.Link);
        Assert.Contains(Uri.EscapeDataString("'O\\'Brien & Co'"), http.Calls[1].Url);
        var fields = JsonDocument.Parse(http.Calls[^1].JsonBody!).RootElement.GetProperty("records")[0].GetProperty("fields");
        Assert.Equal("sales@northwind.example", fields.GetProperty("Email").GetString());
        Assert.Equal(82, fields.GetProperty("Score").GetInt32());
        Assert.False(fields.TryGetProperty("Owner", out _));
    }

    [Fact]
    public void Events_are_limited_to_known_names()
    {
        Assert.Equal("sent,digest", IntegrationEvents.Normalise(["DIGEST", "sent", "nonsense", "sent"]));
        Assert.Equal("sent,leads,digest,replies", IntegrationEvents.Normalise(null));
        Assert.Equal("sent,leads,digest", IntegrationEvents.Normalise(["sent", "leads", "digest"]));
    }
}

public class ConnectorHttpTests
{
    [Fact]
    public async Task A_webhook_on_this_machine_is_never_contacted_and_says_why()
    {
        using var http = new HttpClient(OpportunityPilot.Infrastructure.Integrations.ConnectorHttp.Handler());
        var reply = await new OpportunityPilot.Infrastructure.Integrations.ConnectorHttp(http).SendAsync(new ConnectorRequest(HttpMethod.Post, "https://localhost/hook", "{}"), default);
        Assert.Equal(ConnectorResponse.Blocked, reply.Body);
        var ex = await Assert.ThrowsAsync<ConnectorProblem>(() => WebhookPoster.PostAsync(new FakeConnectorHttp(_ => reply), IntegrationKind.Webhook, "https://localhost/hook", new("test", "t", []), DateTime.UtcNow, default));
        Assert.Contains("private network", ex.Message);
    }
}
