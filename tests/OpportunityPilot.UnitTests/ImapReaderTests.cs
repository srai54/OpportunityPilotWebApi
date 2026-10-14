using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpportunityPilot.Application.Mail;
using OpportunityPilot.Infrastructure.Mail;

namespace OpportunityPilot.UnitTests;

/// <summary>The real IMAP path against a tiny in-process TLS server: login quoting, literals, read-only commands and honest refusals.</summary>
public class ImapReaderTests
{
    private static readonly Lazy<X509Certificate2> Certificate = new(() =>
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        // Re-imported so the private key is usable by the platform TLS stack (Windows refuses an ephemeral key).
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pkcs12), null);
    });

    /// <summary>One message the fake server holds: its UID, arrival time and raw header block.</summary>
    private sealed record Stored(uint Uid, string InternalDate, string Headers, bool UidAfterLiteral = false);

    /// <summary>Speaks just enough IMAP over TLS. Records every command; refuses the login when asked; can stay silent to test the timeout.</summary>
    private sealed class FakeImapServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public List<string> Commands { get; } = [];
        public List<Stored> Messages { get; } = [];
        public IEnumerable<uint>? SearchResult { get; set; }
        public bool RefuseLogin { get; init; }
        public bool Silent { get; init; }

        public FakeImapServer() { _listener.Start(); _ = Task.Run(Serve); }

        private async Task Serve()
        {
            try
            {
                using var client = await _listener.AcceptTcpClientAsync();
                await using var ssl = new SslStream(client.GetStream());
                await ssl.AuthenticateAsServerAsync(Certificate.Value);
                if (Silent) { await Task.Delay(TimeSpan.FromSeconds(10)); return; }
                var reader = new StreamReader(ssl, Encoding.ASCII);
                async Task Say(string text) { await ssl.WriteAsync(Encoding.UTF8.GetBytes(text)); await ssl.FlushAsync(); }
                await Say("* OK [CAPABILITY IMAP4rev1] fake ready\r\n");
                string? line;
                while ((line = await reader.ReadLineAsync()) is not null)
                {
                    lock (Commands) Commands.Add(line);
                    var parts = line.Split(' ', 3);
                    var (tag, verb) = (parts[0], parts.Length > 1 ? parts[1].ToUpperInvariant() : "");
                    var rest = parts.Length > 2 ? parts[2] : "";
                    switch (verb)
                    {
                        case "LOGIN":
                            await Say(RefuseLogin ? $"{tag} NO [AUTHENTICATIONFAILED] Invalid credentials (Failure)\r\n" : $"{tag} OK LOGIN completed\r\n");
                            break;
                        case "EXAMINE":
                            await Say($"* {Messages.Count} EXISTS\r\n* 0 RECENT\r\n{tag} OK [READ-ONLY] EXAMINE completed\r\n");
                            break;
                        case "UID" when rest.StartsWith("SEARCH", StringComparison.OrdinalIgnoreCase):
                            await Say($"* SEARCH {string.Join(' ', SearchResult ?? Messages.Select(m => m.Uid))}\r\n{tag} OK SEARCH completed\r\n");
                            break;
                        case "UID" when rest.StartsWith("FETCH", StringComparison.OrdinalIgnoreCase):
                            var reply = new StringBuilder();
                            var seq = 1;
                            reply.Append("* 9 FETCH (FLAGS (\\Seen))\r\n"); // an unsolicited update the client must ignore
                            foreach (var m in Messages)
                            {
                                var bytes = Encoding.UTF8.GetByteCount(m.Headers);
                                reply.Append(m.UidAfterLiteral
                                    ? $"* {seq++} FETCH (INTERNALDATE \"{m.InternalDate}\" BODY[HEADER.FIELDS ({ImapClient.HeaderFields})] {{{bytes}}}\r\n{m.Headers} UID {m.Uid})\r\n"
                                    : $"* {seq++} FETCH (UID {m.Uid} INTERNALDATE \"{m.InternalDate}\" BODY[HEADER.FIELDS ({ImapClient.HeaderFields})] {{{bytes}}}\r\n{m.Headers})\r\n");
                            }
                            await Say(reply + $"{tag} OK FETCH completed\r\n");
                            break;
                        case "LOGOUT":
                            await Say($"* BYE logging out\r\n{tag} OK LOGOUT completed\r\n");
                            return;
                        default:
                            await Say($"{tag} BAD unknown command\r\n");
                            break;
                    }
                }
            }
            catch (Exception) { /* the client closed the connection */ }
        }

        public void Dispose() => _listener.Stop();
    }

    private static ImapMailboxReader Reader(int port, TimeSpan? timeout = null) =>
        new(Options.Create(new MailOptions { AllowPrivateSmtpHosts = true }), NullLogger<ImapMailboxReader>.Instance)
        {
            Port = port, CertificateCheck = (_, _, _, _) => true, Timeout = timeout ?? TimeSpan.FromSeconds(20),
        };

    private static string Login(string password = "app-password", string host = "127.0.0.1") =>
        JsonSerializer.Serialize(new SmtpCredentials(host, 587, "asha@example.test", password));

    private static readonly DateTime Since = new(2026, 10, 1, 8, 30, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Headers_are_read_with_literals_decoded_and_the_mailbox_is_only_examined_never_changed()
    {
        using var server = new FakeImapServer();
        server.Messages.Add(new(101, "13-Oct-2026 09:15:02 +0530",
            "From: \"Ops, Northwind\" <Ops@Northwind.example>\r\nSubject: =?UTF-8?B?UmU6IETDvHNzZWxkb3Jm?=\r\n =?UTF-8?Q?_delivery?=\r\nMessage-ID: <abc@northwind.example>\r\nIn-Reply-To: <sent-1@example.test>\r\n\r\n"));
        server.Messages.Add(new(102, " 2-Oct-2026 10:00:00 +0000",
            "From: hr@contoso.example\r\nSubject: Automatic reply: Backend role\r\nAuto-Submitted: auto-replied\r\n\r\n", UidAfterLiteral: true));

        var result = await Reader(server.Port).ReadHeadersSinceAsync(Login("p\"a\\ss word"), Since, CancellationToken.None);

        Assert.Null(result.Failure);
        Assert.Equal(2, result.Messages.Count);
        var reply = result.Messages[0];
        Assert.Equal("ops@northwind.example", reply.FromAddress);
        Assert.Equal("Re: Düsseldorf delivery", reply.Subject);
        Assert.Equal(new DateTime(2026, 10, 13, 3, 45, 2, DateTimeKind.Utc), reply.ReceivedAt);
        Assert.Equal("<abc@northwind.example>", reply.MessageId);
        Assert.Equal("<sent-1@example.test>", reply.InReplyTo);
        Assert.False(reply.AutoReply);
        var auto = result.Messages[1];
        Assert.Equal("hr@contoso.example", auto.FromAddress);
        Assert.True(auto.AutoReply);

        string[] commands;
        lock (server.Commands) commands = [.. server.Commands];
        // The password is quoted with its quote and backslash escaped, so it cannot end the string early.
        Assert.EndsWith("LOGIN \"asha@example.test\" \"p\\\"a\\\\ss word\"", commands[0]);
        Assert.EndsWith("EXAMINE INBOX", commands[1]);
        Assert.EndsWith("UID SEARCH SINCE 1-Oct-2026", commands[2]);
        Assert.Contains("UID FETCH 101:102 (UID INTERNALDATE BODY.PEEK[HEADER.FIELDS (", commands[3]);
        Assert.EndsWith("LOGOUT", commands[4]);
        Assert.DoesNotContain(commands, c => c.Contains(" SELECT ", StringComparison.OrdinalIgnoreCase) || c.Contains(" STORE ", StringComparison.OrdinalIgnoreCase)
            || c.Contains("EXPUNGE", StringComparison.OrdinalIgnoreCase) || c.Contains("BODY[", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task At_most_the_newest_500_messages_are_fetched()
    {
        using var server = new FakeImapServer { SearchResult = Enumerable.Range(1, 650).Select(i => (uint)i) };
        var result = await Reader(server.Port).ReadHeadersSinceAsync(Login(), Since, CancellationToken.None);
        Assert.Null(result.Failure);
        string fetch;
        lock (server.Commands) fetch = server.Commands.Single(c => c.Contains("UID FETCH"));
        Assert.Contains("UID FETCH 151:650 (", fetch);
    }

    [Fact]
    public async Task A_refused_login_says_so_and_nothing_else_is_asked()
    {
        using var server = new FakeImapServer { RefuseLogin = true };
        var result = await Reader(server.Port).ReadHeadersSinceAsync(Login(), Since, CancellationToken.None);
        Assert.True(result.LoginRefused);
        Assert.StartsWith("Your email provider refused the login", result.Failure);
        Assert.Contains("app password", result.Failure);
        Assert.Empty(result.Messages);
        lock (server.Commands) Assert.DoesNotContain(server.Commands, c => c.Contains("EXAMINE") || c.Contains("FETCH"));
    }

    [Fact]
    public void Outlook_and_microsoft_365_refusals_explain_that_microsoft_turned_imap_app_passwords_off()
    {
        Assert.Contains("Microsoft has turned off", ImapMailboxReader.RefusedMessage("smtp-mail.outlook.com"));
        Assert.Contains("Microsoft has turned off", ImapMailboxReader.RefusedMessage("smtp.office365.com"));
        Assert.DoesNotContain("Microsoft", ImapMailboxReader.RefusedMessage("smtp.gmail.com"));
    }

    [Theory]
    [InlineData("app\r\npassword")]
    [InlineData("app\npassword")]
    [InlineData("pass\0word")]
    public async Task A_login_with_a_line_break_is_refused_before_any_connection(string password)
    {
        using var server = new FakeImapServer();
        var result = await Reader(server.Port).ReadHeadersSinceAsync(Login(password), Since, CancellationToken.None);
        Assert.True(result.LoginRefused);
        Assert.NotNull(result.Failure);
        lock (server.Commands) Assert.Empty(server.Commands);
    }

    [Fact]
    public async Task A_private_mail_server_is_refused_unless_the_test_option_allows_it()
    {
        var reader = new ImapMailboxReader(Options.Create(new MailOptions()), NullLogger<ImapMailboxReader>.Instance);
        var result = await reader.ReadHeadersSinceAsync(Login(host: "smtp.localhost"), Since, CancellationToken.None);
        Assert.False(result.LoginRefused);
        Assert.Contains("imap.localhost", result.Failure);
    }

    [Fact]
    public async Task A_server_that_never_answers_times_out_with_a_plain_reason()
    {
        using var server = new FakeImapServer { Silent = true };
        var result = await Reader(server.Port, TimeSpan.FromSeconds(1)).ReadHeadersSinceAsync(Login(), Since, CancellationToken.None);
        Assert.False(result.LoginRefused);
        Assert.Contains("took too long", result.Failure);
    }

    [Theory]
    [InlineData("smtp.gmail.com", "imap.gmail.com")]
    [InlineData("smtp-mail.outlook.com", "outlook.office365.com")]
    [InlineData("smtp.office365.com", "outlook.office365.com")]
    [InlineData("smtp.mail.yahoo.com", "imap.mail.yahoo.com")]
    [InlineData("smtp.zoho.in", "imap.zoho.in")]
    [InlineData("SMTP.Example.com", "imap.example.com")]
    [InlineData("mail.example.com", "mail.example.com")]
    public void The_imap_server_follows_from_the_smtp_server(string smtp, string imap) => Assert.Equal(imap, MailHosts.ImapHostFor(smtp));

    [Fact]
    public void Header_text_helpers_read_dates_addresses_and_set_ranges()
    {
        Assert.Equal(new DateTime(2026, 10, 13, 3, 45, 2, DateTimeKind.Utc), MailHeaderText.ParseDate("Mon, 13 Oct 2026 09:15:02 +0530 (IST)"));
        Assert.Equal(new DateTime(2026, 10, 13, 9, 15, 0, DateTimeKind.Utc), MailHeaderText.ParseDate("13 Oct 2026 09:15 GMT"));
        Assert.Null(MailHeaderText.ParseDate("yesterday"));
        Assert.Equal("a@b.example", MailHeaderText.Address("A <A@B.example>"));
        Assert.Equal("a@b.example", MailHeaderText.Address("a@b.example (Asha)"));
        Assert.Null(MailHeaderText.Address("undisclosed-recipients:;"));
        Assert.Equal("1:3,7,9:10", ImapClient.UidSet([1, 2, 3, 7, 9, 10]));
        Assert.Null(ImapClient.Quote("bad\rvalue"));
        Assert.Equal("\"a\\\"b\"", ImapClient.Quote("a\"b"));
    }
}
