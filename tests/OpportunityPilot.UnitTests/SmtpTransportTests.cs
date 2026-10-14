using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpportunityPilot.Application.Mail;
using OpportunityPilot.Domain.Mail;
using OpportunityPilot.Infrastructure.Mail;

namespace OpportunityPilot.UnitTests;

/// <summary>The real SMTP path against a tiny in-process mail server: what the server receives is exactly what the person approved, and refusals are reported honestly.</summary>
public class SmtpTransportTests
{
    /// <summary>Speaks just enough SMTP (EHLO, AUTH PLAIN, MAIL, RCPT, DATA, QUIT). Accepts one login; anything else is refused with 535.</summary>
    private sealed class FakeSmtpServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public string? From, To, Data;
        public bool AuthSeen;
        public bool RefuseLogin;

        public FakeSmtpServer() { _listener.Start(); _ = Task.Run(Serve); }

        private async Task Serve()
        {
            try
            {
                using var client = await _listener.AcceptTcpClientAsync();
                var stream = client.GetStream();
                var reader = new StreamReader(stream, Encoding.ASCII);
                async Task Say(string line) => await stream.WriteAsync(Encoding.ASCII.GetBytes(line + "\r\n"));
                await Say("220 fake ESMTP");
                string? line;
                while ((line = await reader.ReadLineAsync()) is not null)
                {
                    if (line.StartsWith("EHLO", StringComparison.OrdinalIgnoreCase)) { await Say("250-fake"); await Say("250 AUTH PLAIN LOGIN"); }
                    else if (line.StartsWith("AUTH", StringComparison.OrdinalIgnoreCase)) { AuthSeen = true; await Say(RefuseLogin ? "535 5.7.8 Authentication failed" : "235 2.7.0 ok"); }
                    else if (line.StartsWith("MAIL FROM", StringComparison.OrdinalIgnoreCase)) { From = line; await Say(RefuseLogin ? "530 5.7.0 Authentication required" : "250 ok"); }
                    else if (line.StartsWith("RCPT TO", StringComparison.OrdinalIgnoreCase)) { To = line; await Say("250 ok"); }
                    else if (line.StartsWith("DATA", StringComparison.OrdinalIgnoreCase))
                    {
                        await Say("354 go");
                        var data = new StringBuilder();
                        while ((line = await reader.ReadLineAsync()) is not null && line != ".") data.AppendLine(line);
                        Data = data.ToString();
                        await Say("250 queued");
                    }
                    else if (line.StartsWith("QUIT", StringComparison.OrdinalIgnoreCase)) { await Say("221 bye"); break; }
                    else await Say("250 ok");
                }
            }
            catch (Exception) { /* the client closed the connection */ }
        }

        public void Dispose() => _listener.Stop();
    }

    private static MailTransport Transport() => new(new HttpClient(), Options.Create(new MailOptions()), NullLogger<MailTransport>.Instance);

    private static string Login(int port, bool ssl = false) => JsonSerializer.Serialize(new SmtpCredentials("127.0.0.1", port, "asha@example.test", "app-password", ssl));

    [Fact]
    public async Task An_approved_message_reaches_the_mail_server_exactly_as_written()
    {
        using var server = new FakeSmtpServer();
        var result = await Transport().SendAsync(MailProvider.Smtp, Login(server.Port), new OutgoingMail("ops@northwind.example", "Dynamics 365 delivery", "Hello Northwind team."), CancellationToken.None);
        Assert.True(result.Accepted, result.Failure);
        Assert.Contains("not a delivery confirmation", result.Receipt);
        Assert.True(server.AuthSeen);
        Assert.Contains("asha@example.test", server.From);
        Assert.Contains("ops@northwind.example", server.To);
        Assert.Contains("Subject: Dynamics 365 delivery", server.Data);
        // The body travels base64-encoded (UTF-8); decode what follows the headers.
        var lines = server.Data!.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var encoded = string.Concat(lines.Skip(lines.IndexOf(string.Empty) + 1));
        Assert.Equal("Hello Northwind team.", Encoding.UTF8.GetString(Convert.FromBase64String(encoded)).Trim());
    }

    [Fact]
    public async Task A_refused_login_is_reported_as_a_login_problem_and_nothing_is_sent()
    {
        using var server = new FakeSmtpServer { RefuseLogin = true };
        var result = await Transport().SendAsync(MailProvider.Smtp, Login(server.Port), new OutgoingMail("ops@northwind.example", "Hi", "Body"), CancellationToken.None);
        Assert.False(result.Accepted);
        Assert.True(result.TokenRejected);
        Assert.Contains("app password", result.Failure);
        Assert.Null(server.Data);
    }

    [Fact]
    public async Task A_server_that_is_not_there_says_so_instead_of_claiming_a_send()
    {
        var port = new Func<int>(() => { var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); var p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p; })();
        var result = await Transport().SendAsync(MailProvider.Smtp, Login(port), new OutgoingMail("ops@northwind.example", "Hi", "Body"), CancellationToken.None);
        Assert.False(result.Accepted);
        Assert.False(string.IsNullOrWhiteSpace(result.Failure));
    }

    [Fact]
    public async Task A_line_break_in_the_subject_is_refused_before_any_connection()
    {
        var result = await Transport().SendAsync(MailProvider.Smtp, Login(1), new OutgoingMail("ops@northwind.example", "Hi\r\nBcc: x@y.test", "Body"), CancellationToken.None);
        Assert.False(result.Accepted);
    }

    [Fact]
    public void Without_a_mail_token_key_a_key_is_derived_from_the_server_secret_and_with_neither_nothing_is_ready()
    {
        Assert.False(new MailOptions().Ready(MailProvider.Smtp));
        var derived = new MailOptions { FallbackSecret = "Host=db;Password=x" };
        Assert.True(derived.Ready(MailProvider.Smtp));
        Assert.False(derived.Ready(MailProvider.Google));
    }
}
