using System.Net;
using System.Net.Http.Headers;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpportunityPilot.Application.Mail;
using OpportunityPilot.Domain.Mail;

namespace OpportunityPilot.Infrastructure.Mail;

/// <summary>
/// Talks to Microsoft (identity platform and Graph) and Google (OAuth and Gmail). Tokens, codes and message text are never logged;
/// only the provider and an HTTP status are. A 2xx from the send call means "accepted", never "delivered".
/// </summary>
public sealed class MailTransport(HttpClient http, IOptions<MailOptions> options, ILogger<MailTransport> logger) : IMailTransport
{
    private const string MicrosoftScope = "offline_access openid email https://graph.microsoft.com/Mail.Send";
    private const string GoogleScope = "openid email https://www.googleapis.com/auth/gmail.send";

    public string AuthorizeUrl(MailProvider provider, string redirectUri, string state, string challenge)
    {
        var o = options.Value;
        var p = new Dictionary<string, string>
        {
            ["client_id"] = o.For(provider).ClientId ?? string.Empty, ["response_type"] = "code", ["redirect_uri"] = redirectUri,
            ["state"] = state, ["code_challenge"] = challenge, ["code_challenge_method"] = "S256",
            ["scope"] = provider == MailProvider.Microsoft ? MicrosoftScope : GoogleScope,
        };
        if (provider == MailProvider.Microsoft) { p["response_mode"] = "query"; p["prompt"] = "select_account"; }
        else { p["access_type"] = "offline"; p["prompt"] = "consent"; }
        var baseUrl = provider == MailProvider.Microsoft
            ? $"https://login.microsoftonline.com/{Uri.EscapeDataString(o.Microsoft.Tenant)}/oauth2/v2.0/authorize"
            : "https://accounts.google.com/o/oauth2/v2/auth";
        return baseUrl + "?" + string.Join("&", p.Select(x => $"{x.Key}={Uri.EscapeDataString(x.Value)}"));
    }

    private string TokenUrl(MailProvider provider) => provider == MailProvider.Microsoft
        ? $"https://login.microsoftonline.com/{Uri.EscapeDataString(options.Value.Microsoft.Tenant)}/oauth2/v2.0/token"
        : "https://oauth2.googleapis.com/token";

    private async Task<(HttpStatusCode Status, JsonDocument? Body)> PostFormAsync(MailProvider provider, Dictionary<string, string> form, CancellationToken ct)
    {
        var o = options.Value.For(provider);
        form["client_id"] = o.ClientId ?? string.Empty;
        form["client_secret"] = o.ClientSecret ?? string.Empty;
        using var response = await http.PostAsync(TokenUrl(provider), new FormUrlEncodedContent(form), ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        logger.LogInformation("{Provider} token endpoint answered HTTP {Status}", provider, (int)response.StatusCode);
        try { return (response.StatusCode, JsonDocument.Parse(text)); } catch (JsonException) { return (response.StatusCode, null); }
    }

    public async Task<MailTokens> ExchangeCodeAsync(MailProvider provider, string code, string verifier, string redirectUri, CancellationToken ct)
    {
        var (status, body) = await PostFormAsync(provider, new()
        {
            ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = redirectUri, ["code_verifier"] = verifier,
            ["scope"] = provider == MailProvider.Microsoft ? MicrosoftScope : GoogleScope,
        }, ct);
        using (body)
        {
            if (status != HttpStatusCode.OK || body is null) throw new InvalidOperationException("The sign-in could not be completed. Start again.");
            var root = body.RootElement;
            var access = root.TryGetProperty("access_token", out var a) ? a.GetString() : null;
            var refresh = root.TryGetProperty("refresh_token", out var r) ? r.GetString() : null;
            var email = MailBuilders.EmailFromIdToken(root.TryGetProperty("id_token", out var i) ? i.GetString() : null);
            if (string.IsNullOrEmpty(access) || string.IsNullOrEmpty(refresh) || email is null)
                throw new InvalidOperationException("The provider did not give the access needed to send mail. Connect again and accept the permissions.");
            return new MailTokens(access, refresh, email);
        }
    }

    public async Task<string?> RefreshAsync(MailProvider provider, string refreshToken, CancellationToken ct)
    {
        var form = new Dictionary<string, string> { ["grant_type"] = "refresh_token", ["refresh_token"] = refreshToken };
        if (provider == MailProvider.Microsoft) form["scope"] = MicrosoftScope;
        var (status, body) = await PostFormAsync(provider, form, ct);
        using (body)
        {
            if (status != HttpStatusCode.OK || body is null) return null;
            return body.RootElement.TryGetProperty("access_token", out var a) ? a.GetString() : null;
        }
    }

    /// <summary>
    /// Sends through the person's own SMTP server with their app password (STARTTLS). For this provider <paramref name="credentialsJson"/> is the
    /// decrypted <see cref="SmtpCredentials"/>. The password, the address and the message text are never logged; a 2xx means "accepted".
    /// </summary>
    private async Task<MailSendResult> SendSmtpAsync(string credentialsJson, OutgoingMail mail, CancellationToken ct)
    {
        SmtpCredentials? c;
        try { c = JsonSerializer.Deserialize<SmtpCredentials>(credentialsJson); } catch (JsonException) { c = null; }
        if (c is null) return new(false, null, string.Empty, "The saved email login could not be read. Connect your email again.", TokenRejected: true);
        try
        {
            using var client = new SmtpClient(c.Host, c.Port) { EnableSsl = c.Ssl, Credentials = new NetworkCredential(c.Username, c.Password), Timeout = 20000 };
            using var message = new MailMessage(c.Username, mail.To, mail.Subject, mail.Body) { BodyEncoding = Encoding.UTF8, SubjectEncoding = Encoding.UTF8 };
            await client.SendMailAsync(message, ct);
            logger.LogInformation("SMTP send accepted by {Host}", c.Host);
            return new(true, null, $"Accepted by {c.Host} for sending from your own email (it is in your Sent folder if the provider keeps one). Accepted is not a delivery confirmation.", null);
        }
        catch (SmtpException ex)
        {
            logger.LogInformation("SMTP send to {Host} failed: {Status}", c.Host, ex.StatusCode);
            var refused = ex.Message.Contains("Authentication", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("5.7.", StringComparison.Ordinal);
            return refused
                ? new(false, null, string.Empty, "Your email provider refused the login. Check the app password and connect again.", TokenRejected: true)
                : new(false, null, string.Empty, $"The email server did not accept the message ({ex.StatusCode}). Nothing was sent.");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is IOException or InvalidOperationException or OperationCanceledException)
        {
            logger.LogInformation("SMTP send to {Host} did not complete: {Type}", c.Host, ex.GetType().Name);
            return new(false, null, string.Empty, "The email server could not be reached or took too long. Check your Sent folder before trying again.");
        }
    }

    public async Task<MailSendResult> SendAsync(MailProvider provider, string accessToken, OutgoingMail mail, CancellationToken ct)
    {
        if (!MailBuilders.SafeHeader(mail.To) || !MailBuilders.SafeHeader(mail.Subject))
            return new(false, null, string.Empty, "The recipient or subject contains a line break, so it was not sent.");
        if (provider == MailProvider.Smtp) return await SendSmtpAsync(accessToken, mail, ct);
        var microsoft = provider == MailProvider.Microsoft;
        using var message = new HttpRequestMessage(HttpMethod.Post, microsoft ? "https://graph.microsoft.com/v1.0/me/sendMail" : "https://gmail.googleapis.com/gmail/v1/users/me/messages/send")
        {
            Content = new StringContent(microsoft ? MailBuilders.GraphSendMail(mail) : MailBuilders.GmailSend(mail), Encoding.UTF8, "application/json"),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await http.SendAsync(message, ct);
        logger.LogInformation("{Provider} send answered HTTP {Status}", provider, (int)response.StatusCode);
        if (response.StatusCode is HttpStatusCode.Unauthorized)
            return new(false, null, string.Empty, "The mailbox access was refused. Connect the mailbox again.", TokenRejected: true);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            return new(false, null, string.Empty, "The mail provider asked us to slow down. Nothing was sent; try again in a few minutes.");
        if (!response.IsSuccessStatusCode)
            return new(false, null, string.Empty, $"The mail provider did not accept the message (HTTP {(int)response.StatusCode}). Nothing was sent.");
        if (microsoft)
            return new(true, null, "Accepted by Microsoft Graph for sending from your Outlook (saved in Sent Items). Accepted is not a delivery confirmation.", null);
        var json = await response.Content.ReadAsStringAsync(ct);
        string? id = null;
        try { using var doc = JsonDocument.Parse(json); id = doc.RootElement.TryGetProperty("id", out var v) ? v.GetString() : null; } catch (JsonException) { }
        return new(true, id, $"Accepted by Gmail{(id is null ? string.Empty : $" (message {id})")}. Accepted is not a delivery confirmation.", null);
    }
}
