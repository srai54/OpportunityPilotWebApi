using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using OpportunityPilot.Application.Integrations;
using OpportunityPilot.Infrastructure.Research;

namespace OpportunityPilot.Infrastructure.Integrations;

/// <summary>The connection was refused before it was opened: the address is on a private network.</summary>
public sealed class BlockedAddressException() : HttpRequestException("The address is not on the public internet.");

/// <summary>
/// Requests to connected services. No redirects, a short timeout, at most 1 MB read back, and the connection is opened only to a public
/// internet address (checked after DNS, on the address actually used), so a pasted webhook cannot reach this server's own network.
/// </summary>
public sealed class ConnectorHttp(HttpClient http) : IConnectorHttp
{
    public const string ClientName = "connectors";
    private const int MaxRead = 1_000_000;

    public static SocketsHttpHandler Handler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectCallback = async (context, ct) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
            var target = addresses.FirstOrDefault(AddressClassifier.IsPublic)
                ?? throw new BlockedAddressException();
            if (addresses.Any(a => !AddressClassifier.IsPublic(a))) throw new BlockedAddressException();
            var socket = new Socket(target.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(target, context.DnsEndPoint.Port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch { socket.Dispose(); throw; }
        }
    };

    public async Task<ConnectorResponse> SendAsync(ConnectorRequest r, CancellationToken ct)
    {
        if (!Uri.TryCreate(r.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return new(0, "");
        using var message = new HttpRequestMessage(r.Method, uri);
        if (r.Bearer is not null) message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", r.Bearer);
        foreach (var (name, value) in r.Headers ?? new Dictionary<string, string>()) message.Headers.TryAddWithoutValidation(name, value);
        message.Headers.UserAgent.ParseAdd("OpportunityPilot/1.0");
        if (r.Form is not null) message.Content = new FormUrlEncodedContent(r.Form);
        else if (r.JsonBody is not null) message.Content = new StringContent(r.JsonBody, Encoding.UTF8, "application/json");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            var buffer = new byte[MaxRead];
            var read = 0;
            int n;
            while (read < MaxRead && (n = await stream.ReadAsync(buffer.AsMemory(read, MaxRead - read), timeout.Token)) > 0) read += n;
            return new((int)response.StatusCode, Encoding.UTF8.GetString(buffer, 0, read));
        }
        catch (HttpRequestException ex) when (ex is BlockedAddressException || ex.InnerException is BlockedAddressException) { return new(0, ConnectorResponse.Blocked); }
        catch (HttpRequestException) { return new(0, ""); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(0, ""); }
    }
}
