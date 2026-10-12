namespace OpportunityPilot.Application.Research;

/// <param name="Content">Decoded body text (HTML, XML or plain text), at most the configured byte cap.</param>
/// <param name="FailureReason">Safe, user-facing reason; never an exception message or stack trace.</param>
/// <param name="Requests">HTTP requests actually sent (redirect hops and retries included), for the run's fetch budget.</param>
/// <param name="StatusCode">The HTTP status of a failed final answer (e.g. 404), when there was one; lets a caller word its own reason.</param>
public sealed record FetchResult(bool Ok, string? Content, string? ContentType, string? FinalUrl, string? FailureReason, int Requests,
    int? StatusCode = null)
{
    public static FetchResult Fail(string reason, int requests = 0, int? statusCode = null) =>
        new(false, null, null, null, reason, requests, statusCode);
}

/// <summary>Outbound fetching with SSRF protection. Implementations never throw for network or policy failures.</summary>
public interface IWebFetcher
{
    /// <summary>
    /// Checks the shape of a URL (absolute, scheme, no credentials, port). Null when acceptable. Destination
    /// addresses are judged only at fetch time, against what DNS returns then, so a source is never trusted early.
    /// </summary>
    string? CheckUrl(string url);

    /// <summary>Reads HTML, plain text and RSS/Atom (Url and Feed sources).</summary>
    Task<FetchResult> FetchAsync(string url, CancellationToken ct, int? maxBytes = null);

    /// <summary>
    /// Reads <c>application/json</c> only, with the same address, size, timeout and redirect rules. Used solely for the
    /// documented public job-board APIs (Greenhouse, Lever, Adzuna); user-supplied Url and Feed sources never get JSON.
    /// </summary>
    Task<FetchResult> FetchJsonAsync(string url, CancellationToken ct, int? maxBytes = null);

    /// <summary>
    /// POSTs a JSON body and reads <c>application/json</c> only, with the same address, size, timeout and retry rules.
    /// Redirects are refused (a search body is never replayed elsewhere). Used solely for Workday's public job search.
    /// </summary>
    Task<FetchResult> PostJsonAsync(string url, string jsonBody, CancellationToken ct);
}

/// <param name="Text">Visible text with scripts, styles and page chrome removed; whitespace collapsed.</param>
/// <param name="Links">Absolute contact/careers/mailto links found on the page (bounded).</param>
public sealed record PageText(string? Title, string Text, IReadOnlyList<string> Links);

public sealed record FeedEntry(string Title, string? Link, string Text, DateTime? PublishedAt);

/// <param name="IsFeed">False when the document was not RSS 2.0 or Atom at all.</param>
public sealed record FeedDocument(bool IsFeed, string? Title, IReadOnlyList<FeedEntry> Entries, string? FailureReason);

/// <summary>Turns fetched markup into bounded text. Implementations must not resolve external entities or DTDs.</summary>
public interface IContentParser
{
    PageText ParseHtml(string html, string? baseUrl);

    FeedDocument ParseFeed(string xml);
}
