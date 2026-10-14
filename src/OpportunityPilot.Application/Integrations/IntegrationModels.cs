using System.Text.Json;
using System.Text.Json.Serialization;
using OpportunityPilot.Domain.Integrations;

namespace OpportunityPilot.Application.Integrations;

/// <param name="Connected">False rows describe a service that can be connected; secrets are never part of this.</param>
/// <param name="Events">Webhook kinds only: which events post a message.</param>
public sealed record IntegrationDto(
    IntegrationKind Kind, bool Connected, string? Label, IReadOnlyList<string> Events, DateTime? ConnectedAt, DateTime? LastUsedAt, string? LastError,
    string? FeedUrl);

/// <param name="Url">Slack, Teams or Webhook: the incoming-webhook address.</param>
/// <param name="Token">HubSpot private-app token, Notion integration secret, Airtable personal access token, or the Hunter / Apollo API key.</param>
/// <param name="Target">Notion: the database link or id. Airtable: the base link or id.</param>
/// <param name="Table">Airtable: the table name or id (the first table when empty).</param>
public sealed record ConnectIntegrationRequest(string? Url, string? Token, string? Target, string? Table, IReadOnlyList<string>? Events);

public sealed record UpdateIntegrationEventsRequest(IReadOnlyList<string> Events);

public sealed record IntegrationTestResult(bool Ok, string Message);

public sealed record PushLeadsRequest(IReadOnlyList<Guid> OpportunityIds);

/// <param name="Outcome">Added, AlreadyThere or Failed.</param>
/// <param name="Link">Where the record is in the other service, when it said.</param>
public sealed record PushLeadResult(Guid OpportunityId, string Outcome, string? Reason, string? Link);

/// <summary>The events a webhook connection can be told about.</summary>
public static class IntegrationEvents
{
    public const string Sent = "sent", Leads = "leads", Digest = "digest", Replies = "replies";
    public static readonly string[] All = [Sent, Leads, Digest, Replies];

    public static string Normalise(IEnumerable<string>? events) =>
        string.Join(',', (events ?? All).Select(e => e.Trim().ToLowerInvariant()).Where(All.Contains).Distinct().OrderBy(e => Array.IndexOf(All, e)));

    public static IReadOnlyList<string> Parse(string? stored) =>
        string.IsNullOrEmpty(stored) ? [] : stored.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

public static class IntegrationKinds
{
    public static bool IsWebhook(IntegrationKind k) => k is IntegrationKind.Slack or IntegrationKind.Teams or IntegrationKind.Webhook;
    public static bool IsLeadTarget(IntegrationKind k) => k is IntegrationKind.HubSpot or IntegrationKind.Notion or IntegrationKind.Airtable;
    public static bool IsPeopleSource(IntegrationKind k) => k is IntegrationKind.Hunter or IntegrationKind.Apollo;

    public static string Name(IntegrationKind k) => k switch
    {
        IntegrationKind.Slack => "Slack",
        IntegrationKind.Teams => "Microsoft Teams",
        IntegrationKind.Webhook => "Webhook",
        IntegrationKind.HubSpot => "HubSpot",
        IntegrationKind.Notion => "Notion",
        IntegrationKind.Airtable => "Airtable",
        IntegrationKind.Hunter => "Hunter",
        IntegrationKind.Apollo => "Apollo",
        _ => "Calendar feed"
    };
}

/// <summary>One HTTP request to a connected service. The secret travels only here, never into logs or errors shown to people.</summary>
public sealed record ConnectorRequest(HttpMethod Method, string Url, string? JsonBody = null, string? Bearer = null, IReadOnlyDictionary<string, string>? Headers = null);

/// <param name="Status">0 when the service could not be reached.</param>
public sealed record ConnectorResponse(int Status, string Body)
{
    /// <summary>The body of a status-0 reply when the address is on a private network and was never contacted.</summary>
    public const string Blocked = "blocked:private-address";
    public bool Ok => Status is >= 200 and < 300;
    public JsonElement Json()
    {
        try { return JsonDocument.Parse(string.IsNullOrWhiteSpace(Body) ? "{}" : Body).RootElement.Clone(); }
        catch (JsonException) { return JsonDocument.Parse("{}").RootElement.Clone(); }
    }
}

/// <summary>
/// Sends requests to connected services. The implementation follows no redirects, times out quickly and connects only to public
/// internet addresses, so a pasted URL cannot reach this server's own network.
/// </summary>
public interface IConnectorHttp
{
    Task<ConnectorResponse> SendAsync(ConnectorRequest request, CancellationToken ct);
}

/// <summary>What is sent about a lead to a CRM or database. Built only from what the app already holds for that lead.</summary>
public sealed record LeadRecord(
    Guid Id, string Name, string Title, string? Website, string? Domain, string? Location, int Score, string Status, string? Link, string? Email, string Notes);

public sealed record IntegrationOptions
{
    public const string SectionName = "Integrations";
    /// <summary>The hour (UTC) the daily summary goes out. 3 UTC is 08:30 in India and 04:00 in London.</summary>
    public int DigestHourUtc { get; set; } = 3;
    /// <summary>Public base of this API, used to build calendar feed links. Falls back to the request's own address.</summary>
    public string? PublicApiBase { get; set; }
}

internal static class ConnectorJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    public static string Write(object value) => JsonSerializer.Serialize(value, Options);
}
