using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpportunityPilot.Application.Research;

namespace OpportunityPilot.Infrastructure.Research;

/// <summary>Bounds simultaneous outbound fetches across the whole process (Research:Concurrency, ceiling 4).</summary>
public sealed class FetchConcurrency(IOptions<ResearchOptions> options)
{
    public SemaphoreSlim Gate { get; } = new(options.Value.EffectiveConcurrency, options.Value.EffectiveConcurrency);
}

/// <summary>
/// SSRF-safe fetching (plan §11). The URL is checked up front, DNS is resolved by the connect callback itself and
/// every resolved address must be public, and the socket connects to the address that was checked — so DNS
/// rebinding between check and connect cannot reach a private host. Redirects are followed by hand (max 5),
/// each hop checked again. Bodies are capped after decompression; only text-like content types are read.
/// </summary>
public sealed class SafeFetcher(
    HttpClient http,
    IFetchAddressPolicy policy,
    IHostEnvironment environment,
    IOptions<ResearchOptions> options,
    FetchConcurrency concurrency,
    ILogger<SafeFetcher> logger) : IWebFetcher
{
    public const string ClientName = "SafeFetcher";
    public const int MaxRedirects = 5;
    public const int MaxRetries = 3;
    public const string UserAgent = "OpportunityPilot/1.0 (research on sources supplied by the user)";

    public static readonly IReadOnlySet<string> AllowedContentTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "text/html", "application/xhtml+xml", "text/plain", "application/rss+xml", "application/atom+xml", "application/xml", "text/xml"
    };

    /// <summary>Only for the job-board API sources (<see cref="FetchJsonAsync"/>); never for user-supplied pages or feeds.</summary>
    public static readonly IReadOnlySet<string> JsonContentTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "application/json" };

    private sealed record Reading(IReadOnlySet<string> Types, string Accept, string Rejected);

    private static readonly Reading Pages = new(AllowedContentTypes,
        "text/html,application/xhtml+xml,application/rss+xml,application/atom+xml,application/xml;q=0.9,text/plain;q=0.8",
        "only HTML, plain text and RSS/Atom feeds are.");

    private static readonly Reading Json = new(JsonContentTypes, "application/json", "only JSON is read from job-board APIs.");

    private const string Blocked = "The address is private, local or reserved, so it is never fetched.";

    private readonly ResearchOptions _limits = options.Value;
    private bool AllowHttp => environment.IsDevelopment();

    /// <summary>The handler the named client uses. <paramref name="resolve"/> replaces DNS in tests (e.g. to simulate rebinding).</summary>
    public static SocketsHttpHandler CreateHandler(IFetchAddressPolicy policy, Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null) => new()
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All,
        UseProxy = false,          // a proxy would connect on our behalf, bypassing the address check below
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(1),
        MaxResponseHeadersLength = 64,
        ConnectCallback = (context, ct) => ConnectAsync(context, policy, resolve ?? Dns.GetHostAddressesAsync, ct)
    };

    public string? CheckUrl(string url)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri)) return "Enter an absolute URL, e.g. https://example.com/page.";
        return CheckShape(uri);
    }

    public Task<FetchResult> FetchAsync(string url, CancellationToken ct, int? maxBytes = null) => FetchAsync(url, Pages, ct, null, maxBytes);

    public Task<FetchResult> FetchJsonAsync(string url, CancellationToken ct, int? maxBytes = null) => FetchAsync(url, Json, ct, null, maxBytes);

    public Task<FetchResult> PostJsonAsync(string url, string jsonBody, CancellationToken ct) => FetchAsync(url, Json, ct, jsonBody);

    private async Task<FetchResult> FetchAsync(string url, Reading reading, CancellationToken ct, string? body = null, int? maxBytes = null)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri)) return FetchResult.Fail("The URL is not a valid absolute URL.");

        var requests = 0;
        await concurrency.Gate.WaitAsync(ct);
        try
        {
            for (var hop = 0; hop <= MaxRedirects; hop++)
            {
                if (CheckDestination(uri) is { } reason) return FetchResult.Fail(reason, requests);

                Attempt attempt = default!;
                for (var retry = 0; ; retry++)
                {
                    attempt = await AttemptAsync(uri, reading, ct, body, maxBytes);
                    requests++;
                    if (!attempt.Transient || retry >= MaxRetries) break;
                    // Exponential backoff with jitter: 0.5 s, 1 s, 2 s (+ up to 250 ms).
                    await Task.Delay(TimeSpan.FromMilliseconds(500 * Math.Pow(2, retry) + Random.Shared.Next(250)), ct);
                }

                switch (attempt)
                {
                    case { Redirect: not null } when body is not null:
                        return FetchResult.Fail("The search answered with a redirect, which is not followed for a search request.", requests);
                    case { Redirect: { } next }:
                        uri = next;
                        continue;
                    case { Failure: { } failure }:
                        return FetchResult.Fail(failure, requests, attempt.Status);
                    default:
                        return new FetchResult(true, attempt.Content, attempt.ContentType, uri.ToString(), null, requests);
                }
            }
            return FetchResult.Fail($"The page redirected more than {MaxRedirects} times.", requests);
        }
        finally
        {
            concurrency.Gate.Release();
        }
    }

    private sealed record Attempt(string? Content, string? ContentType, Uri? Redirect, string? Failure, bool Transient, int? Status = null);

    private async Task<Attempt> AttemptAsync(Uri uri, Reading reading, CancellationToken ct, string? body = null, int? maxBytes = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_limits.EffectiveTimeout);
        try
        {
            using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, uri);
            if (body is not null) request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            request.Headers.UserAgent.ParseAdd(UserAgent);
            request.Headers.Accept.ParseAdd(reading.Accept);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);

            var status = (int)response.StatusCode;
            if (status is >= 300 and < 400)
            {
                var location = response.Headers.Location;
                if (location is null) return new(null, null, null, $"The site answered a redirect ({status}) without a destination.", false);
                return new(null, null, location.IsAbsoluteUri ? location : new Uri(uri, location), null, false);
            }
            if (!response.IsSuccessStatusCode)
            {
                var transient = status is 408 or 429 or >= 500;
                var hint = status is 401 or 403 ? " The page may need a login." : "";
                return new(null, null, null, $"The site answered {status}.{hint}", transient, status);
            }

            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType is null || !reading.Types.Contains(mediaType))
                return new(null, null, null, $"Content type {mediaType ?? "(none)"} is not read; {reading.Rejected}", false);

            var max = maxBytes is { } large ? Math.Clamp(large, _limits.EffectiveBytes, ResearchOptions.LargeFeedBytes) : _limits.EffectiveBytes;
            if (response.Content.Headers.ContentLength > max)
                return new(null, null, null, $"The page is larger than {max / 1024} KB.", false);

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            var (bytes, tooLarge) = await ReadCappedAsync(stream, max, timeout.Token);
            if (tooLarge) return new(null, null, null, $"The page is larger than {max / 1024} KB.", false);

            return new(Decode(bytes, response.Content.Headers.ContentType?.CharSet), mediaType.ToLowerInvariant(), null, null, false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(null, null, null, $"The site did not answer within {(int)_limits.EffectiveTimeout.TotalSeconds} seconds.", true);
        }
        catch (HttpRequestException ex) when (Find<BlockedDestinationException>(ex) is { } blocked)
        {
            return new(null, null, null, blocked.Message, false);
        }
        catch (HttpRequestException ex) when (Find<SocketException>(ex) is { SocketErrorCode: SocketError.HostNotFound or SocketError.NoData })
        {
            return new(null, null, null, "The host name could not be found.", false);
        }
        catch (HttpRequestException ex)
        {
            logger.LogInformation("Fetch of {Host} failed: {Error}", uri.Host, ex.HttpRequestError);
            return new(null, null, null, "The site could not be reached.", true);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            // Truncated or corrupt (e.g. bad gzip) body.
            logger.LogInformation("Fetch of {Host} returned an unreadable body: {Type}", uri.Host, ex.GetType().Name);
            return new(null, null, null, "The site sent a response that could not be read.", true);
        }
    }

    /// <summary>Shape rules that need no DNS: absolute http(s), no credentials, allowed port, no local-only host names.</summary>
    private string? CheckShape(Uri uri)
    {
        if (uri.Scheme == Uri.UriSchemeHttp && !AllowHttp) return "Only https URLs can be fetched.";
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) return "Only https URLs can be fetched.";
        if (!string.IsNullOrEmpty(uri.UserInfo)) return "URLs containing a username or password are not fetched.";
        if (!policy.IsPortAllowed(uri.Port)) return "Only the standard ports (80 and 443) are allowed.";
        if (string.IsNullOrEmpty(uri.Host)) return "The URL has no host.";
        return null;
    }

    /// <summary>Shape rules plus literal-IP and local host-name checks; runs for the first URL and every redirect hop.</summary>
    private string? CheckDestination(Uri uri)
    {
        if (CheckShape(uri) is { } reason) return reason;
        var host = uri.IdnHost.TrimEnd('.');
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
            return Blocked;
        if (IPAddress.TryParse(uri.HostNameType == UriHostNameType.IPv6 ? host.Trim('[', ']') : host, out var literal) && !policy.IsAllowed(literal))
            return Blocked;
        return null;
    }

    private static async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context, IFetchAddressPolicy policy, Func<string, CancellationToken, Task<IPAddress[]>> resolve, CancellationToken ct)
    {
        var endpoint = context.DnsEndPoint;
        if (!policy.IsPortAllowed(endpoint.Port)) throw new BlockedDestinationException("Only the standard ports (80 and 443) are allowed.");

        var addresses = IPAddress.TryParse(endpoint.Host.Trim('[', ']'), out var literal)
            ? [literal]
            : await resolve(endpoint.Host, ct);
        if (addresses.Length == 0) throw new SocketException((int)SocketError.HostNotFound);
        // Refuse if any answer is private: a mixed answer is a classic way to slip an internal address through.
        if (addresses.Any(a => !policy.IsAllowed(a))) throw new BlockedDestinationException(Blocked);

        SocketException? last = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, endpoint.Port), ct);
                // Belt and braces: the peer we actually reached must also be allowed.
                if (socket.RemoteEndPoint is not IPEndPoint remote || !policy.IsAllowed(remote.Address))
                    throw new BlockedDestinationException(Blocked);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException ex)
            {
                socket.Dispose();
                last = ex;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
        throw last ?? new SocketException((int)SocketError.HostUnreachable);
    }

    private static async Task<(byte[] Bytes, bool TooLarge)> ReadCappedAsync(Stream stream, int max, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, ct);
            if (read == 0) return (buffer.ToArray(), false);
            if (buffer.Length + read > max) return ([], true);
            buffer.Write(chunk, 0, read);
        }
    }

    private static string Decode(byte[] bytes, string? charset)
    {
        Encoding encoding = Encoding.UTF8;
        if (!string.IsNullOrWhiteSpace(charset))
        {
            try { encoding = Encoding.GetEncoding(charset.Trim('"', '\'', ' ')); }
            catch (ArgumentException) { encoding = Encoding.UTF8; }
        }
        // Honour a byte-order mark over the header; strip it either way.
        if (bytes is [0xEF, 0xBB, 0xBF, ..]) return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        return encoding.GetString(bytes);
    }

    private static T? Find<T>(Exception ex) where T : Exception
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
            if (e is T match) return match;
        return null;
    }
}

/// <summary>Raised inside the connect callback; surfaces as a safe failure reason, never as an error.</summary>
public sealed class BlockedDestinationException(string message) : Exception(message);
