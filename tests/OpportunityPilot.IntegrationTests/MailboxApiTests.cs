using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using OpportunityPilot.Application.Mail;
using OpportunityPilot.Domain.Mail;

namespace OpportunityPilot.IntegrationTests;

/// <summary>K4: connect a mailbox, send an approved draft through the gateway, and the ways it must refuse. The provider is a fake; nothing leaves the machine.</summary>
public class MailboxApiTests(PostgresApiFactory factory) : IClassFixture<PostgresApiFactory>
{
    private const string Company = """
        Northwind Integrations
        Website: https://northwind.example
        Location: London
        Northwind implements Dynamics 365 and is hiring .NET developers.
        """;

    private sealed class FakeTransport : IMailTransport
    {
        public List<OutgoingMail> Sent { get; } = [];
        public bool RefreshWorks { get; set; } = true;
        public HashSet<string> RefuseTo { get; } = [];
        public MailSendResult NextResult { get; set; } = new(true, "msg-1", "Accepted by Gmail (message msg-1). Accepted is not a delivery confirmation.", null);
        public string AuthorizeUrl(MailProvider provider, string redirectUri, string state, string challenge) => $"https://login.example.test/authorize?state={Uri.EscapeDataString(state)}&redirect_uri={Uri.EscapeDataString(redirectUri)}";
        public Task<MailTokens> ExchangeCodeAsync(MailProvider provider, string code, string verifier, string redirectUri, CancellationToken ct) =>
            code == "good" ? Task.FromResult(new MailTokens("access", "refresh-secret", "me@contoso.test")) : throw new InvalidOperationException("bad code");
        public Task<string?> RefreshAsync(MailProvider provider, string refreshToken, CancellationToken ct) => Task.FromResult(RefreshWorks && refreshToken == "refresh-secret" ? "access" : null);
        public Task<MailSendResult> SendAsync(MailProvider provider, string accessToken, OutgoingMail mail, CancellationToken ct)
        {
            if (RefuseTo.Contains(mail.To)) return Task.FromResult(new MailSendResult(false, null, string.Empty, "The mail provider did not accept the message (HTTP 403). Nothing was sent."));
            if (NextResult.Accepted) Sent.Add(mail);
            return Task.FromResult(NextResult);
        }
    }

    private sealed class FakeReader : IMailboxReader
    {
        public MailboxReadResult Next { get; set; } = new([]);
        public int Calls { get; private set; }
        public Task<MailboxReadResult> ReadHeadersSinceAsync(string credentialsJson, DateTime sinceUtc, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(Next);
        }
    }

    private static readonly string TokenKey = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());

    private WebApplicationFactory<Program> App(FakeTransport fake, bool configured = true, FakeReader? reader = null) =>
        factory.WithWebHostBuilder(b =>
        {
            b.ConfigureTestServices(s =>
            {
                s.AddSingleton<IMailTransport>(fake);
                if (reader is not null) s.AddSingleton<IMailboxReader>(reader);
            });
            if (!configured) return;
            b.UseSetting("Mail:TokenKey", TokenKey);
            b.UseSetting("Mail:PublicApiBase", "https://api.example.test");
            b.UseSetting("Mail:WebBase", "https://app.example.test");
            b.UseSetting("Mail:Google:ClientId", "g-id");
            b.UseSetting("Mail:Google:ClientSecret", "g-secret");
            b.UseSetting("Mail:Microsoft:ClientId", "ms-id");
            b.UseSetting("Mail:Microsoft:ClientSecret", "ms-secret");
        });

    private static async Task<Guid> ApprovedDraftAsync(HttpClient user, string recipient)
    {
        var campaign = await ResearchApi.CreateCampaignAsync(user, "Customer", new { keywords = new[] { "Dynamics 365" }, locations = new[] { "London" } });
        await ResearchApi.AddPasteAsync(user, campaign.Id(), Company);
        await ResearchApi.QueueAsync(user, campaign.Id());
        await PostgresApiFactory.RunResearchAsync(PostgresApiFactoryServices.Current!);
        var opportunityId = Assert.Single(await ResearchApi.OpportunitiesAsync(user, campaign.Id())).Id();
        await (await user.PatchAsJsonAsync($"/api/v1/opportunities/{opportunityId}/status", new { status = "Shortlisted" })).Json(HttpStatusCode.OK);
        var draft = await (await user.PostAsJsonAsync($"/api/v1/opportunities/{opportunityId}/drafts", new { channel = "Email", recipient })).Json(HttpStatusCode.Created);
        draft = await (await user.PutAsJsonAsync($"/api/v1/drafts/{draft.Id()}", new
        {
            recipient, subject = "Dynamics 365 delivery", body = "Hello Northwind team, we deliver .NET integration work. Regards, Asha", expectedVersion = draft.Int("version")
        })).Json(HttpStatusCode.OK);
        draft = await (await user.PostAsJsonAsync($"/api/v1/drafts/{draft.Id()}/approve", new { version = draft.Int("version") })).Json(HttpStatusCode.OK);
        return draft.Id();
    }

    /// <summary>Connects Google for this user by walking the same two steps a browser does.</summary>
    private static async Task ConnectAsync(WebApplicationFactory<Program> app, HttpClient user, string provider = "Google")
    {
        var begin = await (await user.PostAsync($"/api/v1/mailboxes/{provider}/connect", null)).Json(HttpStatusCode.OK);
        var state = System.Web.HttpUtility.ParseQueryString(new Uri(begin.Str("url")).Query)["state"]!;
        var anonymous = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var back = await anonymous.GetAsync($"/api/v1/mailboxes/callback/{provider}?code=good&state={Uri.EscapeDataString(state)}");
        Assert.Equal(HttpStatusCode.Redirect, back.StatusCode);
        // The anonymous callback only hands the code back to the web app; the signed-in person finishes it.
        Assert.StartsWith("https://app.example.test/settings?mailboxCode=good&mailboxState=", back.Headers.Location!.ToString());
        var done = await (await user.PostAsJsonAsync($"/api/v1/mailboxes/{provider}/complete", new { code = "good", state })).Json(HttpStatusCode.OK);
        Assert.Equal($"https://app.example.test/settings?mailbox=connected&provider={provider}", done.Str("next"));
    }

    [Fact]
    public async Task Without_credentials_the_mailboxes_say_not_configured_and_nothing_can_be_connected()
    {
        await using var app = App(new FakeTransport(), configured: false);
        var user = PostgresApiFactory.ClientFor(app, "mail-none@example.test");
        var list = (await user.GetJson("/api/v1/mailboxes")).EnumerateArray().ToList();
        Assert.Equal(3, list.Count);
        Assert.All(list.Where(m => m.Str("provider") != "Smtp"), m => { Assert.False(m.GetProperty("configured").GetBoolean()); Assert.False(m.GetProperty("connected").GetBoolean()); });
        // Email with an app password needs no provider app, only the server's own key, so it is the one way that is ready out of the box.
        Assert.True(list.Single(m => m.Str("provider") == "Smtp").GetProperty("configured").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, (await user.PostAsync("/api/v1/mailboxes/Google/connect", null)).StatusCode);
    }

    [Fact]
    public async Task A_connected_mailbox_sends_an_approved_draft_once_and_the_token_never_leaves_the_server()
    {
        var fake = new FakeTransport();
        await using var app = App(fake);
        PostgresApiFactoryServices.Current = app.Services;
        var user = PostgresApiFactory.ClientFor(app, "mail-send@example.test");
        await ConnectAsync(app, user);

        var listText = await (await user.GetAsync("/api/v1/mailboxes")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("refresh-secret", listText);
        var google = JsonDocument.Parse(listText).RootElement.EnumerateArray().Single(m => m.Str("provider") == "Google");
        Assert.Equal("me@contoso.test", google.Str("emailAddress"));
        Assert.True(google.GetProperty("connected").GetBoolean());

        var draftId = await ApprovedDraftAsync(user, "ops@northwind.example");
        var version = (await user.GetJson($"/api/v1/drafts/{draftId}")).Int("version");
        var execution = await (await user.PostAsJsonAsync($"/api/v1/drafts/{draftId}/send-via-mailbox", new { version, provider = "Google" })).Json(HttpStatusCode.OK);
        Assert.Equal("Succeeded", execution.Str("state"));
        Assert.Equal("Gmail", execution.Str("provider"));
        Assert.Contains("not a delivery confirmation", execution.Str("receipt"));
        var sent = Assert.Single(fake.Sent);
        Assert.Equal(("ops@northwind.example", "Dynamics 365 delivery"), (sent.To, sent.Subject));

        // A second press must not send again.
        Assert.Equal(HttpStatusCode.Conflict, (await user.PostAsJsonAsync($"/api/v1/drafts/{draftId}/send-via-mailbox", new { version, provider = "Google" })).StatusCode);
        Assert.Single(fake.Sent);
        Assert.Equal("Sent", (await user.GetJson($"/api/v1/drafts/{draftId}")).Str("state"));

        // Disconnecting removes the connection.
        Assert.Equal(HttpStatusCode.NoContent, (await user.DeleteAsync("/api/v1/mailboxes/Google")).StatusCode);
        Assert.False((await user.GetJson("/api/v1/mailboxes")).EnumerateArray().Single(m => m.Str("provider") == "Google").GetProperty("connected").GetBoolean());
    }

    private static async Task<(Guid Id, int Version)> ApprovedWithVersionAsync(HttpClient user, string recipient)
    {
        var id = await ApprovedDraftAsync(user, recipient);
        return (id, (await user.GetJson($"/api/v1/drafts/{id}")).Int("version"));
    }

    [Fact]
    public async Task Send_all_sends_each_approved_message_once_and_stops_at_the_first_failure()
    {
        var fake = new FakeTransport();
        await using var app = App(fake);
        PostgresApiFactoryServices.Current = app.Services;
        var user = PostgresApiFactory.ClientFor(app, "mail-batch@example.test");
        await ConnectAsync(app, user);
        var a = await ApprovedWithVersionAsync(user, "a@northwind.example");
        var b = await ApprovedWithVersionAsync(user, "b@northwind.example");
        var c = await ApprovedWithVersionAsync(user, "c@northwind.example");

        var first = (await (await user.PostAsJsonAsync("/api/v1/drafts/send-via-mailbox-batch", new { provider = "Google", items = new[] { new { id = a.Id, version = a.Version } } })).Json(HttpStatusCode.OK)).EnumerateArray().ToList();
        Assert.Equal("Sent", Assert.Single(first).Str("outcome"));
        Assert.Single(fake.Sent);

        // The second message is refused by the provider: the first (already sent) is not repeated, the third is not attempted.
        fake.RefuseTo.Add("b@northwind.example");
        var run = (await (await user.PostAsJsonAsync("/api/v1/drafts/send-via-mailbox-batch", new
        {
            provider = "Google", items = new[] { new { id = a.Id, version = a.Version }, new { id = b.Id, version = b.Version }, new { id = c.Id, version = c.Version } }
        })).Json(HttpStatusCode.OK)).EnumerateArray().ToList();
        Assert.Equal(["Failed", "NotAttempted", "NotAttempted"], run.Select(x => x.Str("outcome")).ToArray());
        Assert.Contains("already sent", run[0].Str("reason"));
        Assert.Equal(["a@northwind.example"], fake.Sent.Select(m => m.To).ToArray());

        // Without the already-sent one, the refused message fails on its own and the last is not attempted.
        var second = (await (await user.PostAsJsonAsync("/api/v1/drafts/send-via-mailbox-batch", new { provider = "Google", items = new[] { new { id = b.Id, version = b.Version }, new { id = c.Id, version = c.Version } } })).Json(HttpStatusCode.OK)).EnumerateArray().ToList();
        Assert.Equal(["Failed", "NotAttempted"], second.Select(x => x.Str("outcome")).ToArray());
        Assert.Contains("Nothing was sent", second[0].Str("reason"));
        Assert.NotEqual("Sent", (await user.GetJson($"/api/v1/drafts/{c.Id}")).Str("state"));

        // Once the provider accepts, the remaining two go out in one action.
        fake.RefuseTo.Clear();
        var rest = (await (await user.PostAsJsonAsync("/api/v1/drafts/send-via-mailbox-batch", new { provider = "Google", items = new[] { new { id = b.Id, version = b.Version }, new { id = c.Id, version = c.Version } } })).Json(HttpStatusCode.OK)).EnumerateArray().ToList();
        Assert.All(rest, x => Assert.Equal("Sent", x.Str("outcome")));
        Assert.Equal(3, fake.Sent.Count);
    }

    [Fact]
    public async Task Send_all_refuses_an_empty_oversized_or_duplicated_list()
    {
        await using var app = App(new FakeTransport());
        var user = PostgresApiFactory.ClientFor(app, "mail-batch-bad@example.test");
        var id = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.BadRequest, (await user.PostAsJsonAsync("/api/v1/drafts/send-via-mailbox-batch", new { provider = "Google", items = Array.Empty<object>() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await user.PostAsJsonAsync("/api/v1/drafts/send-via-mailbox-batch", new { provider = "Google", items = new[] { new { id, version = 1 }, new { id, version = 1 } } })).StatusCode);
        var many = Enumerable.Range(0, 26).Select(_ => new { id = Guid.NewGuid(), version = 1 }).ToArray();
        Assert.Equal(HttpStatusCode.BadRequest, (await user.PostAsJsonAsync("/api/v1/drafts/send-via-mailbox-batch", new { provider = "Google", items = many })).StatusCode);
    }

    [Fact]
    public async Task A_forged_or_expired_sign_in_stores_nothing()
    {
        await using var app = App(new FakeTransport());
        var user = PostgresApiFactory.ClientFor(app, "mail-forged@example.test");
        var anonymous = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var back = await (await user.PostAsJsonAsync("/api/v1/mailboxes/Google/complete", new { code = "good", state = "forged" })).Json(HttpStatusCode.OK);
        Assert.Equal("https://app.example.test/settings?mailbox=expired&provider=Google", back.Str("next"));

        // A consent link started by someone else cannot be finished by this person (no mailbox lands on either account).
        var attacker = PostgresApiFactory.ClientFor(app, "mail-attacker@example.test");
        var begin = await (await attacker.PostAsync("/api/v1/mailboxes/Google/connect", null)).Json(HttpStatusCode.OK);
        var theirs = System.Web.HttpUtility.ParseQueryString(new Uri(begin.Str("url")).Query)["state"]!;
        var stolen = await (await user.PostAsJsonAsync("/api/v1/mailboxes/Google/complete", new { code = "good", state = theirs })).Json(HttpStatusCode.OK);
        Assert.Contains("mailbox=expired", stolen.Str("next"));
        Assert.DoesNotContain((await attacker.GetJson("/api/v1/mailboxes")).EnumerateArray(), m => m.GetProperty("connected").GetBoolean());
        Assert.DoesNotContain((await user.GetJson("/api/v1/mailboxes")).EnumerateArray(), m => m.GetProperty("connected").GetBoolean());
        var declined = await anonymous.GetAsync("/api/v1/mailboxes/callback/Google?error=access_denied");
        Assert.Contains("mailbox=declined", declined.Headers.Location!.ToString());
    }

    [Fact]
    public async Task A_revoked_token_asks_to_reconnect_and_nothing_is_sent()
    {
        var fake = new FakeTransport { RefreshWorks = false };
        await using var app = App(fake);
        PostgresApiFactoryServices.Current = app.Services;
        var user = PostgresApiFactory.ClientFor(app, "mail-revoked@example.test");
        await ConnectAsync(app, user);
        var draftId = await ApprovedDraftAsync(user, "ops@northwind.example");
        var version = (await user.GetJson($"/api/v1/drafts/{draftId}")).Int("version");

        Assert.Equal(HttpStatusCode.Conflict, (await user.PostAsJsonAsync($"/api/v1/drafts/{draftId}/send-via-mailbox", new { version, provider = "Google" })).StatusCode);
        Assert.Empty(fake.Sent);
        Assert.Equal("NeedsReconnect", (await user.GetJson("/api/v1/mailboxes")).EnumerateArray().Single(m => m.Str("provider") == "Google").Str("status"));
        Assert.NotEqual("Sent", (await user.GetJson($"/api/v1/drafts/{draftId}")).Str("state"));
        // And with the status flipped, the next try tells the person to connect again before anything else.
        Assert.Equal(HttpStatusCode.BadRequest, (await user.PostAsJsonAsync($"/api/v1/drafts/{draftId}/send-via-mailbox", new { version, provider = "Google" })).StatusCode);
    }

    [Fact]
    public async Task A_provider_refusal_is_a_failure_and_the_draft_can_be_tried_again()
    {
        var fake = new FakeTransport { NextResult = new(false, null, string.Empty, "The mail provider did not accept the message (HTTP 403). Nothing was sent.") };
        await using var app = App(fake);
        PostgresApiFactoryServices.Current = app.Services;
        var user = PostgresApiFactory.ClientFor(app, "mail-refused@example.test");
        await ConnectAsync(app, user);
        var draftId = await ApprovedDraftAsync(user, "ops@northwind.example");
        var version = (await user.GetJson($"/api/v1/drafts/{draftId}")).Int("version");

        var refused = await user.PostAsJsonAsync($"/api/v1/drafts/{draftId}/send-via-mailbox", new { version, provider = "Google" });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("Nothing was sent", await refused.Content.ReadAsStringAsync());
        Assert.NotEqual("Sent", (await user.GetJson($"/api/v1/drafts/{draftId}")).Str("state"));

        fake.NextResult = new(true, "msg-2", "Accepted by Gmail (message msg-2). Accepted is not a delivery confirmation.", null);
        var retry = await (await user.PostAsJsonAsync($"/api/v1/drafts/{draftId}/send-via-mailbox", new { version, provider = "Google" })).Json(HttpStatusCode.OK);
        Assert.Equal("Succeeded", retry.Str("state"));
        Assert.Single(fake.Sent);
    }

    [Fact]
    public async Task A_suppressed_recipient_is_refused_before_anything_is_sent()
    {
        var fake = new FakeTransport();
        await using var app = App(fake);
        PostgresApiFactoryServices.Current = app.Services;
        var user = PostgresApiFactory.ClientFor(app, "mail-suppressed@example.test");
        await ConnectAsync(app, user);
        var draftId = await ApprovedDraftAsync(user, "stop@northwind.example");
        var version = (await user.GetJson($"/api/v1/drafts/{draftId}")).Int("version");
        await (await user.PostAsJsonAsync("/api/v1/suppressions", new { recipient = "stop@northwind.example", reason = "asked" })).Json(HttpStatusCode.Created);

        var blocked = await user.PostAsJsonAsync($"/api/v1/drafts/{draftId}/send-via-mailbox", new { version, provider = "Google" });
        Assert.Equal(HttpStatusCode.BadRequest, blocked.StatusCode);
        Assert.Contains("suppression", await blocked.Content.ReadAsStringAsync());
        Assert.Empty(fake.Sent);
    }

    [Fact]
    public async Task One_person_cannot_send_through_or_see_another_persons_mailbox()
    {
        var fake = new FakeTransport();
        await using var app = App(fake);
        PostgresApiFactoryServices.Current = app.Services;
        var owner = PostgresApiFactory.ClientFor(app, "mail-owner@example.test");
        var other = PostgresApiFactory.ClientFor(app, "mail-other@example.test");
        await ConnectAsync(app, owner);
        var draftId = await ApprovedDraftAsync(owner, "ops@northwind.example");

        Assert.DoesNotContain((await other.GetJson("/api/v1/mailboxes")).EnumerateArray(), m => m.GetProperty("connected").GetBoolean());
        Assert.Equal(HttpStatusCode.BadRequest, (await other.PostAsJsonAsync($"/api/v1/drafts/{draftId}/send-via-mailbox", new { version = 1, provider = "Google" })).StatusCode);
        Assert.Empty(fake.Sent);
    }

    [Fact]
    public async Task Email_with_an_app_password_is_verified_by_a_test_message_stored_encrypted_and_sends_an_approved_draft_once()
    {
        var fake = new FakeTransport();
        await using var app = App(fake);
        PostgresApiFactoryServices.Current = app.Services;
        var user = PostgresApiFactory.ClientFor(app, "mail-smtp@example.test");

        // A wrong password never connects.
        fake.NextResult = new(false, null, string.Empty, "Your email provider refused the login. Check the app password and connect again.", TokenRejected: true);
        var refused = await user.PostAsJsonAsync("/api/v1/mailboxes/smtp", new { preset = "gmail", username = "asha@gmail.test", password = "wrong" });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.False((await user.GetJson("/api/v1/mailboxes")).EnumerateArray().Single(m => m.Str("provider") == "Smtp").GetProperty("connected").GetBoolean());

        fake.NextResult = new(true, null, "Accepted by smtp.gmail.com for sending from your own email. Accepted is not a delivery confirmation.", null);
        var connected = await (await user.PostAsJsonAsync("/api/v1/mailboxes/smtp", new { preset = "gmail", username = "asha@gmail.test", password = "abcd efgh ijkl mnop" })).Json(HttpStatusCode.OK);
        Assert.Equal("asha@gmail.test", connected.Str("emailAddress"));
        var test = Assert.Single(fake.Sent);
        Assert.Equal("asha@gmail.test", test.To);
        fake.Sent.Clear();

        var listText = await (await user.GetAsync("/api/v1/mailboxes")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("abcd efgh", listText);
        Assert.DoesNotContain("password", listText, StringComparison.OrdinalIgnoreCase);

        var draftId = await ApprovedDraftAsync(user, "ops@northwind.example");
        var version = (await user.GetJson($"/api/v1/drafts/{draftId}")).Int("version");
        var execution = await (await user.PostAsJsonAsync($"/api/v1/drafts/{draftId}/send-via-mailbox", new { version, provider = "Smtp" })).Json(HttpStatusCode.OK);
        Assert.Equal("Succeeded", execution.Str("state"));
        Assert.Equal("Smtp", execution.Str("provider"));
        Assert.Single(fake.Sent);
        Assert.Equal(HttpStatusCode.Conflict, (await user.PostAsJsonAsync($"/api/v1/drafts/{draftId}/send-via-mailbox", new { version, provider = "Smtp" })).StatusCode);
        Assert.Single(fake.Sent);
    }

    [Theory]
    [InlineData("custom", "localhost", 587)]
    [InlineData("custom", "127.0.0.1", 587)]
    [InlineData("custom", "smtp.example.com", 25)]
    [InlineData("custom", "bad host!", 587)]
    [InlineData("nonsense", null, null)]
    public async Task A_custom_mail_server_must_be_public_on_a_mail_port_and_a_preset_must_exist(string preset, string? host, int? port)
    {
        var fake = new FakeTransport();
        await using var app = App(fake);
        var user = PostgresApiFactory.ClientFor(app, $"mail-bad-{Guid.NewGuid():N}@example.test");
        var response = await user.PostAsJsonAsync("/api/v1/mailboxes/smtp", new { preset, host, port, username = "asha@example.test", password = "secret" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(fake.Sent);
    }

    [Fact]
    public async Task Replies_to_an_email_sent_with_an_app_password_are_recorded_once_and_stop_the_follow_up()
    {
        var fake = new FakeTransport();
        var reader = new FakeReader();
        await using var app = App(fake, reader: reader);
        PostgresApiFactoryServices.Current = app.Services;
        var user = PostgresApiFactory.ClientFor(app, "mail-replies@example.test");

        // Nothing to read until the email is connected with an app password.
        var notConnected = await user.PostAsync("/api/v1/mailboxes/smtp/check-replies", null);
        Assert.Equal(HttpStatusCode.Conflict, notConnected.StatusCode);
        Assert.Contains("Connect your email", await notConnected.Content.ReadAsStringAsync());

        await (await user.PostAsJsonAsync("/api/v1/mailboxes/smtp", new { preset = "gmail", username = "asha@gmail.test", password = "abcd efgh ijkl mnop" })).Json(HttpStatusCode.OK);
        var draftId = await ApprovedDraftAsync(user, "ops@northwind.example");
        var draft = await user.GetJson($"/api/v1/drafts/{draftId}");
        await (await user.PostAsJsonAsync($"/api/v1/drafts/{draftId}/send-via-mailbox", new { version = draft.Int("version"), provider = "Smtp" })).Json(HttpStatusCode.OK);
        var opportunityId = draft.GetProperty("opportunityId").GetGuid();
        Assert.Equal("Open", Assert.Single((await user.GetJson($"/api/v1/opportunities/{opportunityId}/next-actions")).EnumerateArray()).Str("state"));

        var at = DateTime.UtcNow.AddMinutes(1);
        reader.Next = new([
            new MailHeader("Ops@Northwind.example", "Re: Dynamics 365 delivery", at, "<r1@northwind.example>", null, null),
            new MailHeader("ops@northwind.example", "Automatic reply: Dynamics 365 delivery", at, "<r2@northwind.example>", null, null, AutoReply: true),
            new MailHeader("news@elsewhere.example", "Weekly news", at, "<n1@elsewhere.example>", null, null),
        ]);
        var result = await (await user.PostAsync("/api/v1/mailboxes/smtp/check-replies", null)).Json(HttpStatusCode.OK);
        Assert.Equal(3, result.Int("checked"));
        Assert.Equal(1, result.Int("replies"));
        var item = Assert.Single(result.GetProperty("items").EnumerateArray());
        Assert.Equal(opportunityId, item.GetProperty("opportunityId").GetGuid());
        Assert.Equal("Ops@Northwind.example", item.Str("from"));
        Assert.Equal("Re: Dynamics 365 delivery", item.Str("subject"));

        Assert.Equal("Responded", (await user.GetJson($"/api/v1/opportunities/{opportunityId}")).Str("status"));
        Assert.Equal("Cancelled", Assert.Single((await user.GetJson($"/api/v1/opportunities/{opportunityId}/next-actions")).EnumerateArray()).Str("state"));
        var activities = (await user.GetJson($"/api/v1/opportunities/{opportunityId}/activities")).EnumerateArray().ToList();
        Assert.Single(activities, a => a.Str("kind") == "Replied" && a.Str("detail").StartsWith("Reply received from Ops@Northwind.example on ", StringComparison.Ordinal));

        // A second check finds the same message and records nothing new.
        var again = await (await user.PostAsync("/api/v1/mailboxes/smtp/check-replies", null)).Json(HttpStatusCode.OK);
        Assert.Equal(0, again.Int("replies"));
        Assert.Single((await user.GetJson($"/api/v1/opportunities/{opportunityId}/activities")).EnumerateArray(), a => a.Str("kind") == "Replied");

        // Another person's check never reads this mailbox.
        var other = PostgresApiFactory.ClientFor(app, "mail-replies-other@example.test");
        Assert.Equal(HttpStatusCode.Conflict, (await other.PostAsync("/api/v1/mailboxes/smtp/check-replies", null)).StatusCode);
        Assert.Equal(2, reader.Calls);
    }

    [Fact]
    public async Task A_refused_login_while_checking_replies_asks_to_reconnect()
    {
        var fake = new FakeTransport();
        var reader = new FakeReader();
        await using var app = App(fake, reader: reader);
        PostgresApiFactoryServices.Current = app.Services;
        var user = PostgresApiFactory.ClientFor(app, "mail-replies-refused@example.test");
        await (await user.PostAsJsonAsync("/api/v1/mailboxes/smtp", new { preset = "gmail", username = "asha@gmail.test", password = "abcd efgh ijkl mnop" })).Json(HttpStatusCode.OK);
        var draftId = await ApprovedDraftAsync(user, "ops@northwind.example");
        var version = (await user.GetJson($"/api/v1/drafts/{draftId}")).Int("version");
        await (await user.PostAsJsonAsync($"/api/v1/drafts/{draftId}/send-via-mailbox", new { version, provider = "Smtp" })).Json(HttpStatusCode.OK);

        reader.Next = MailboxReadResult.Failed("Your email provider refused the login for reading replies. Check that IMAP is turned on for this mailbox and that the app password is still valid, then connect your email again.", loginRefused: true);
        var refused = await user.PostAsync("/api/v1/mailboxes/smtp/check-replies", null);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("Your email provider refused the login", await refused.Content.ReadAsStringAsync());
        Assert.Equal("NeedsReconnect", (await user.GetJson("/api/v1/mailboxes")).EnumerateArray().Single(m => m.Str("provider") == "Smtp").Str("status"));
    }
}

/// <summary>The factory's research runner needs the service provider of the app the test built; tests in this class run one at a time per fixture.</summary>
internal static class PostgresApiFactoryServices
{
    public static IServiceProvider? Current { get; set; }

}
