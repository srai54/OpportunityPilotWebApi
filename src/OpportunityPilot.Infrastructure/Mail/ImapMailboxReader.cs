using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpportunityPilot.Application.Mail;

namespace OpportunityPilot.Infrastructure.Mail;

/// <summary>
/// Reads reply headers from the person's own inbox over IMAP (implicit TLS, port 993) with the same app password that sends. The IMAP
/// server comes from the stored SMTP server (<see cref="MailHosts.ImapHostFor"/>) and must resolve to public addresses only. Only the
/// host and an outcome are logged; never the address, the password or a subject.
/// </summary>
public sealed class ImapMailboxReader(IOptions<MailOptions> options, ILogger<ImapMailboxReader> logger) : IMailboxReader
{
    /// <summary>Tests only: the port of a local fake server. Production always uses 993.</summary>
    public int Port { get; init; } = 993;

    /// <summary>Tests only: accept a self-signed certificate. Null (production) means the normal certificate check.</summary>
    public RemoteCertificateValidationCallback? CertificateCheck { get; init; }

    /// <summary>The whole read (connect, login, search, fetch, logout) must finish within this time.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(20);

    public async Task<MailboxReadResult> ReadHeadersSinceAsync(string credentialsJson, DateTime sinceUtc, CancellationToken ct)
    {
        SmtpCredentials? c;
        try { c = JsonSerializer.Deserialize<SmtpCredentials>(credentialsJson); } catch (JsonException) { c = null; }
        if (c is null || string.IsNullOrWhiteSpace(c.Host))
            return MailboxReadResult.Failed("The saved email login could not be read. Connect your email again.", loginRefused: true);
        if (ImapClient.Quote(c.Username) is null || ImapClient.Quote(c.Password) is null)
            return MailboxReadResult.Failed("The saved login has a character (a line break or a non-English letter) that cannot be sent to the mail server for reading. Connect your email again with the app password.", loginRefused: true);

        var host = MailHosts.ImapHostFor(c.Host);
        var addresses = await MailHosts.PublicAddressesAsync(host, options.Value.AllowPrivateSmtpHosts, ct);
        if (addresses is null)
            return MailboxReadResult.Failed($"The mail server for reading replies ({host}) could not be found or is not a public address.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        try
        {
            var headers = await ImapClient.ReadHeadersAsync(addresses, host, Port, c.Username, c.Password, sinceUtc, IMailboxReader.MaxMessages, CertificateCheck, timeout.Token);
            logger.LogInformation("IMAP read from {Host} returned {Count} headers", host, headers.Count);
            return new MailboxReadResult(headers);
        }
        catch (ImapException ex) when (ex.LoginRefused)
        {
            logger.LogInformation("IMAP login to {Host} was refused", host);
            return MailboxReadResult.Failed(RefusedMessage(c.Host), loginRefused: true);
        }
        catch (ImapException ex)
        {
            logger.LogInformation("IMAP read from {Host} stopped: {Reason}", host, ex.Message);
            return MailboxReadResult.Failed($"The mail server ({host}) answered in a way this app could not read. Nothing was changed; try again later.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogInformation("IMAP read from {Host} timed out", host);
            return MailboxReadResult.Failed($"The mail server ({host}) took too long to answer. Try again later.");
        }
        catch (Exception ex) when (ex is SocketException or IOException or AuthenticationException)
        {
            logger.LogInformation("IMAP connection to {Host} failed: {Type}", host, ex.GetType().Name);
            return MailboxReadResult.Failed($"The mail server ({host}) could not be reached for reading on port 993. Try again later.");
        }
    }

    /// <summary>
    /// Microsoft has switched off app-password (basic) sign-in to IMAP for Outlook.com and most Microsoft 365 accounts, so a refusal
    /// there is usually not a wrong password; the message says so instead of sending the person to re-type it.
    /// </summary>
    public static string RefusedMessage(string smtpHost) => MailHosts.IsMicrosoft(smtpHost)
        ? "Your email provider refused the login for reading replies. Microsoft has turned off app-password sign-in to IMAP for Outlook.com and most Microsoft 365 accounts, so replies cannot be checked with this connection. Sending is not affected; mark replies on the lead by hand."
        : "Your email provider refused the login for reading replies. Check that IMAP is turned on for this mailbox and that the app password is still valid, then connect your email again.";
}
