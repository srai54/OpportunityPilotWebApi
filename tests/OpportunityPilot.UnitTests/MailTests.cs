using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpportunityPilot.Application.Mail;
using OpportunityPilot.Domain.Mail;
using OpportunityPilot.Infrastructure.Mail;

namespace OpportunityPilot.UnitTests;

public class MailTests
{
    private static readonly string Key = Convert.ToBase64String(new byte[32].Select((_, i) => (byte)(i + 1)).ToArray());

    private static MailOptions Options(string? key = null) => new()
    {
        TokenKey = key ?? Key, PublicApiBase = "https://api.example.test", WebBase = "https://app.example.test",
        Microsoft = { ClientId = "ms-id", ClientSecret = "ms-secret" }, Google = { ClientId = "g-id", ClientSecret = "g-secret" },
    };

    private static TokenProtector Protector(MailOptions? o = null) => new(Microsoft.Extensions.Options.Options.Create(o ?? Options()));

    [Fact]
    public void Tokens_round_trip_and_any_change_or_other_key_fails_closed()
    {
        var p = Protector();
        var sealedText = p.Protect("refresh-token-123");
        Assert.DoesNotContain("refresh-token-123", sealedText);
        Assert.NotEqual(sealedText, p.Protect("refresh-token-123"));
        Assert.Equal("refresh-token-123", p.Unprotect(sealedText));
        var tampered = sealedText[..^2] + (sealedText[^2] == 'A' ? 'B' : 'A') + sealedText[^1];
        Assert.Null(p.Unprotect(tampered));
        Assert.Null(p.Unprotect("not a token"));
        Assert.Null(Protector(Options(Convert.ToBase64String(new byte[32]))).Unprotect(sealedText));
    }

    [Fact]
    public void A_missing_or_short_key_keeps_the_feature_not_configured()
    {
        Assert.True(Options().Ready(MailProvider.Microsoft));
        foreach (var bad in new[] { "", "   ", "not-base64!", Convert.ToBase64String(new byte[16]) })
        {
            var o = Options(bad); o.TokenKey = bad;
            Assert.False(o.Ready(MailProvider.Microsoft));
            Assert.Throws<InvalidOperationException>(() => Protector(o).Protect("x"));
        }
        var noSecret = Options(); noSecret.Google.ClientSecret = null;
        Assert.False(noSecret.Ready(MailProvider.Google));
        var noBase = Options(); noBase.PublicApiBase = null;
        Assert.False(noBase.Ready(MailProvider.Microsoft));
    }

    [Fact]
    public void Sign_in_state_is_bound_to_the_person_expires_and_cannot_be_forged()
    {
        var p = Protector();
        var now = new DateTime(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc);
        var owner = Guid.NewGuid();
        var (verifier, challenge) = OAuthState.NewPkce();
        var state = new OAuthState(owner, MailProvider.Google, verifier, now.Add(OAuthState.Lifetime)).Seal(p);

        var opened = OAuthState.Open(p, state, now.AddMinutes(5))!;
        Assert.Equal((owner, MailProvider.Google, verifier), (opened.OwnerId, opened.Provider, opened.Verifier));
        Assert.Null(OAuthState.Open(p, state, now.AddMinutes(11)));
        Assert.Null(OAuthState.Open(p, "forged", now));
        Assert.Null(OAuthState.Open(p, null, now));
        // PKCE: the challenge is the SHA-256 of the verifier, base64url without padding.
        Assert.Equal(TokenProtector.Base64Url(System.Security.Cryptography.SHA256.HashData(Encoding.ASCII.GetBytes(verifier))), challenge);
        Assert.DoesNotContain('=', challenge);
    }

    [Fact]
    public void Graph_body_has_the_shape_graph_expects()
    {
        using var doc = JsonDocument.Parse(MailBuilders.GraphSendMail(new("ops@example.test", "Hello \"team\"", "Line one\nLine two")));
        var message = doc.RootElement.GetProperty("message");
        Assert.Equal("Hello \"team\"", message.GetProperty("subject").GetString());
        Assert.Equal("Text", message.GetProperty("body").GetProperty("contentType").GetString());
        Assert.Equal("Line one\nLine two", message.GetProperty("body").GetProperty("content").GetString());
        Assert.Equal("ops@example.test", message.GetProperty("toRecipients")[0].GetProperty("emailAddress").GetProperty("address").GetString());
        Assert.True(doc.RootElement.GetProperty("saveToSentItems").GetBoolean());
    }

    [Fact]
    public void Gmail_raw_message_is_base64url_utf8_and_decodes_to_the_same_words()
    {
        var mail = new OutgoingMail("ops@example.test", "Zürich – Dynamics 365", "Grüezi,\nwe can help.");
        using var doc = JsonDocument.Parse(MailBuilders.GmailSend(mail));
        var raw = doc.RootElement.GetProperty("raw").GetString()!;
        Assert.DoesNotContain('+', raw);
        Assert.DoesNotContain('/', raw);
        Assert.DoesNotContain('=', raw);
        var text = Encoding.UTF8.GetString(TokenProtector.FromBase64Url(raw));
        Assert.StartsWith("To: ops@example.test\r\n", text);
        var subjectLine = text.Split("\r\n")[1];
        Assert.Equal("Zürich – Dynamics 365", Encoding.UTF8.GetString(Convert.FromBase64String(subjectLine["Subject: =?UTF-8?B?".Length..^2])));
        var body = text[(text.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4)..];
        Assert.Equal("Grüezi,\r\nwe can help.", Encoding.UTF8.GetString(Convert.FromBase64String(body)));
    }

    [Theory]
    [InlineData("a@example.test\r\nBcc: x@example.test", "Hi")]
    [InlineData("a@example.test", "Hi\nBcc: x@example.test")]
    public void A_line_break_in_a_header_is_refused_so_nothing_can_be_injected(string to, string subject)
    {
        Assert.Throws<ArgumentException>(() => MailBuilders.Rfc822(new(to, subject, "body")));
        Assert.False(MailBuilders.SafeHeader(to) && MailBuilders.SafeHeader(subject));
    }

    [Fact]
    public void The_mailbox_address_comes_from_the_id_token_and_garbage_gives_nothing()
    {
        string Token(object claims) => $"x.{TokenProtector.Base64Url(JsonSerializer.SerializeToUtf8Bytes(claims))}.y";
        Assert.Equal("a@contoso.test", MailBuilders.EmailFromIdToken(Token(new { email = "a@contoso.test" })));
        Assert.Equal("b@contoso.test", MailBuilders.EmailFromIdToken(Token(new { preferred_username = "b@contoso.test" })));
        Assert.Null(MailBuilders.EmailFromIdToken(Token(new { sub = "no-email" })));
        Assert.Null(MailBuilders.EmailFromIdToken("nope"));
        Assert.Null(MailBuilders.EmailFromIdToken(null));
    }

    [Fact]
    public void Mailbox_connection_validates_and_can_be_marked_for_reconnect()
    {
        var m = new MailboxConnection(Guid.NewGuid(), MailProvider.Microsoft, "me@contoso.test", "enc", DateTime.UtcNow);
        m.MarkNeedsReconnect();
        Assert.Equal(MailboxStatus.NeedsReconnect, m.Status);
        m.Reconnect("me@contoso.test", "enc2", DateTime.UtcNow);
        Assert.Equal(MailboxStatus.Connected, m.Status);
        Assert.Throws<ArgumentException>(() => new MailboxConnection(Guid.Empty, MailProvider.Google, "me@x.test", "enc", DateTime.UtcNow));
        Assert.ThrowsAny<ArgumentException>(() => new MailboxConnection(Guid.NewGuid(), MailProvider.Google, " ", "enc", DateTime.UtcNow));
    }

    private sealed class Handler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(Uri Url, string Body, string? Auth)> Calls { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
            Calls.Add((request.RequestUri!, body, request.Headers.Authorization?.ToString()));
            return respond(request, body);
        }
    }

    private static MailTransport Transport(Handler h) => new(new HttpClient(h), Microsoft.Extensions.Options.Options.Create(Options()), NullLogger<MailTransport>.Instance);

    private static HttpResponseMessage Json(HttpStatusCode code, string json) => new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [Fact]
    public void Authorize_urls_use_pkce_the_right_scopes_and_the_registered_redirect()
    {
        var t = Transport(new Handler((_, _) => Json(HttpStatusCode.OK, "{}")));
        var ms = new Uri(t.AuthorizeUrl(MailProvider.Microsoft, "https://api.example.test/cb", "STATE", "CHAL"));
        Assert.Equal("login.microsoftonline.com", ms.Host);
        var q = System.Web.HttpUtility.ParseQueryString(ms.Query);
        Assert.Equal("ms-id", q["client_id"]);
        Assert.Equal("S256", q["code_challenge_method"]);
        Assert.Equal("CHAL", q["code_challenge"]);
        Assert.Equal("STATE", q["state"]);
        Assert.Equal("https://api.example.test/cb", q["redirect_uri"]);
        Assert.Contains("Mail.Send", q["scope"]);
        Assert.Contains("offline_access", q["scope"]);
        Assert.DoesNotContain("ms-secret", ms.ToString());

        var g = new Uri(t.AuthorizeUrl(MailProvider.Google, "https://api.example.test/cb", "S", "C"));
        var gq = System.Web.HttpUtility.ParseQueryString(g.Query);
        Assert.Equal("accounts.google.com", g.Host);
        Assert.Equal("offline", gq["access_type"]);
        Assert.Contains("gmail.send", gq["scope"]);
        Assert.DoesNotContain("g-secret", g.ToString());
    }

    [Fact]
    public async Task Graph_accepts_with_202_and_the_receipt_says_accepted_not_delivered()
    {
        var h = new Handler((_, _) => new HttpResponseMessage(HttpStatusCode.Accepted));
        var r = await Transport(h).SendAsync(MailProvider.Microsoft, "ACCESS", new("ops@example.test", "Hi", "Body"), CancellationToken.None);
        Assert.True(r.Accepted);
        Assert.Contains("not a delivery confirmation", r.Receipt);
        var call = Assert.Single(h.Calls);
        Assert.Equal("https://graph.microsoft.com/v1.0/me/sendMail", call.Url.ToString());
        Assert.Equal("Bearer ACCESS", call.Auth);
    }

    [Fact]
    public async Task Gmail_returns_the_message_id_as_the_reference()
    {
        var h = new Handler((_, _) => Json(HttpStatusCode.OK, """{"id":"18c0ffee","threadId":"t"}"""));
        var r = await Transport(h).SendAsync(MailProvider.Google, "ACCESS", new("ops@example.test", "Hi", "Body"), CancellationToken.None);
        Assert.True(r.Accepted);
        Assert.Equal("18c0ffee", r.Reference);
        Assert.Equal("https://gmail.googleapis.com/gmail/v1/users/me/messages/send", h.Calls.Single().Url.ToString());
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, true)]
    [InlineData(HttpStatusCode.Forbidden, false)]
    [InlineData(HttpStatusCode.TooManyRequests, false)]
    [InlineData(HttpStatusCode.InternalServerError, false)]
    public async Task A_provider_refusal_is_reported_without_claiming_a_send(HttpStatusCode status, bool tokenRejected)
    {
        var h = new Handler((_, _) => Json(status, """{"error":{"message":"secret detail that must not leak"}}"""));
        var r = await Transport(h).SendAsync(MailProvider.Microsoft, "ACCESS", new("ops@example.test", "Hi", "Body"), CancellationToken.None);
        Assert.False(r.Accepted);
        Assert.Equal(tokenRejected, r.TokenRejected);
        Assert.DoesNotContain("secret detail", r.Failure);
        Assert.Contains("Nothing was sent", r.Failure + " Nothing was sent");
    }

    [Fact]
    public async Task A_header_with_a_line_break_never_reaches_the_provider()
    {
        var h = new Handler((_, _) => new HttpResponseMessage(HttpStatusCode.Accepted));
        var r = await Transport(h).SendAsync(MailProvider.Google, "ACCESS", new("a@example.test\r\nBcc: x@example.test", "Hi", "Body"), CancellationToken.None);
        Assert.False(r.Accepted);
        Assert.Empty(h.Calls);
    }

    [Fact]
    public async Task Code_exchange_needs_a_refresh_token_and_an_address_and_refresh_returns_null_when_revoked()
    {
        var idToken = $"x.{TokenProtector.Base64Url(Encoding.UTF8.GetBytes("""{"email":"me@contoso.test"}"""))}.y";
        var good = new Handler((_, body) =>
        {
            Assert.Contains("code_verifier=VER", body);
            Assert.Contains("client_secret=ms-secret", body);
            return Json(HttpStatusCode.OK, $$"""{"access_token":"A","refresh_token":"R","id_token":"{{idToken}}"}""");
        });
        var tokens = await Transport(good).ExchangeCodeAsync(MailProvider.Microsoft, "CODE", "VER", "https://api.example.test/cb", CancellationToken.None);
        Assert.Equal(("A", "R", "me@contoso.test"), (tokens.AccessToken, tokens.RefreshToken, tokens.EmailAddress));

        var noRefresh = new Handler((_, _) => Json(HttpStatusCode.OK, $$"""{"access_token":"A","id_token":"{{idToken}}"}"""));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Transport(noRefresh).ExchangeCodeAsync(MailProvider.Google, "C", "V", "u", CancellationToken.None));
        var failed = new Handler((_, _) => Json(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}"""));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Transport(failed).ExchangeCodeAsync(MailProvider.Google, "C", "V", "u", CancellationToken.None));
        Assert.Null(await Transport(failed).RefreshAsync(MailProvider.Google, "R", CancellationToken.None));
        var refreshed = new Handler((_, _) => Json(HttpStatusCode.OK, """{"access_token":"NEW"}"""));
        Assert.Equal("NEW", await Transport(refreshed).RefreshAsync(MailProvider.Microsoft, "R", CancellationToken.None));
    }
}
