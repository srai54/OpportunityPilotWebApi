using System.Text;
using System.Text.Json;

namespace OpportunityPilot.Application.Mail;

/// <summary>Builds the exact request bodies Microsoft Graph and Gmail expect. Pure, so the shapes are unit-tested.</summary>
public static class MailBuilders
{
    /// <summary>Header values may not contain line breaks (that is how header injection works).</summary>
    public static bool SafeHeader(string value) => !value.Contains('\r') && !value.Contains('\n');

    /// <summary>Microsoft Graph <c>POST /me/sendMail</c> body. The message is saved to Sent Items so the person sees it there.</summary>
    public static string GraphSendMail(OutgoingMail mail) => JsonSerializer.Serialize(new
    {
        message = new
        {
            subject = mail.Subject,
            body = new { contentType = "Text", content = mail.Body },
            toRecipients = new[] { new { emailAddress = new { address = mail.To } } },
        },
        saveToSentItems = true,
    });

    /// <summary>An RFC 2822 message with a UTF-8 subject and a base64 body, as bytes (Gmail wants it base64url-encoded in <c>raw</c>).</summary>
    public static string Rfc822(OutgoingMail mail)
    {
        if (!SafeHeader(mail.To) || !SafeHeader(mail.Subject)) throw new ArgumentException("Header values cannot contain line breaks.");
        var subject = Convert.ToBase64String(Encoding.UTF8.GetBytes(mail.Subject));
        var body = Convert.ToBase64String(Encoding.UTF8.GetBytes(mail.Body.Replace("\r\n", "\n").Replace("\n", "\r\n")), Base64FormattingOptions.InsertLineBreaks);
        return $"To: {mail.To}\r\nSubject: =?UTF-8?B?{subject}?=\r\nMIME-Version: 1.0\r\nContent-Type: text/plain; charset=UTF-8\r\nContent-Transfer-Encoding: base64\r\n\r\n{body}";
    }

    public static string GmailSend(OutgoingMail mail) => JsonSerializer.Serialize(new { raw = TokenProtector.Base64Url(Encoding.UTF8.GetBytes(Rfc822(mail))) });

    /// <summary>The email claim of an id_token that was just received from the provider's token endpoint over TLS (not a login check).</summary>
    public static string? EmailFromIdToken(string? idToken)
    {
        var parts = idToken?.Split('.');
        if (parts is not { Length: 3 }) return null;
        try
        {
            using var doc = JsonDocument.Parse(TokenProtector.FromBase64Url(parts[1]));
            foreach (var name in new[] { "email", "preferred_username", "upn" })
                if (doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString()!.Contains('@')) return v.GetString();
        }
        catch (Exception e) when (e is JsonException or FormatException) { }
        return null;
    }
}
