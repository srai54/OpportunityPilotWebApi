using OpportunityPilot.Domain.Common;

namespace OpportunityPilot.Domain.Integrations;

/// <summary>
/// Slack, Teams and Webhook post messages to a URL the person pasted. HubSpot, Notion and Airtable receive leads with a token the person
/// created in that service. CalendarFeed is a private link a calendar app subscribes to. Hunter and Apollo look up people at a company
/// with the person's own API key (third-party data, never stored).
/// </summary>
public enum IntegrationKind { Slack, Teams, Webhook, HubSpot, Notion, Airtable, CalendarFeed, Hunter, Apollo }

/// <summary>
/// A service the person connected. The secret (webhook URL, access token or feed key) opens that service, so it is stored only encrypted
/// and never returned by the API, logged or exported. One connection per kind per owner; disconnecting deletes it.
/// </summary>
public sealed class IntegrationConnection : IOwned
{
    public const int MaxLabelLength = 200;
    public const int MaxSecretLength = 4_000;
    public const int MaxSettingsLength = 4_000;
    public const int MaxEventsLength = 200;
    public const int MaxErrorLength = 500;

    private IntegrationConnection() { }

    public IntegrationConnection(Guid ownerId, IntegrationKind kind, string label, string encryptedSecret, string settingsJson, string events, DateTime utcNow)
    {
        if (ownerId == Guid.Empty) throw new ArgumentException("Owner is required.", nameof(ownerId));
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        Id = Guid.NewGuid();
        OwnerId = ownerId;
        Kind = kind;
        Apply(label, encryptedSecret, settingsJson, events, utcNow);
    }

    public Guid Id { get; private set; }
    public Guid OwnerId { get; private set; }
    public IntegrationKind Kind { get; private set; }
    public string Label { get; private set; } = string.Empty;
    public string EncryptedSecret { get; private set; } = string.Empty;
    public string SettingsJson { get; private set; } = "{}";
    /// <summary>Comma-separated event names this connection is told about (webhooks only), for example "sent,leads,digest".</summary>
    public string Events { get; private set; } = string.Empty;
    public DateTime ConnectedAt { get; private set; }
    public DateTime? LastUsedAt { get; private set; }
    public string? LastError { get; private set; }
    /// <summary>The UTC day the daily summary last went out, so it goes at most once a day.</summary>
    public DateOnly? LastDigestOn { get; private set; }

    /// <summary>The same person connecting again replaces what was stored.</summary>
    public void Reconnect(string label, string encryptedSecret, string settingsJson, string events, DateTime utcNow) =>
        Apply(label, encryptedSecret, settingsJson, events, utcNow);

    /// <summary>A refreshed sign-in: the new access token and settings (with the new refresh token) replace the old ones; nothing else changes.</summary>
    public void RotateSecret(string encryptedSecret, string settingsJson)
    {
        EncryptedSecret = Guard.Required(encryptedSecret, MaxSecretLength, nameof(encryptedSecret));
        SettingsJson = Guard.Required(settingsJson, MaxSettingsLength, nameof(settingsJson));
    }

    /// <summary>The person chose where leads go (a Notion database or an Airtable table) after signing in.</summary>
    public void ChangeTarget(string label, string settingsJson)
    {
        Label = Guard.Required(label, MaxLabelLength, nameof(label));
        SettingsJson = Guard.Required(settingsJson, MaxSettingsLength, nameof(settingsJson));
        LastError = null;
    }

    public void SetEvents(string events) => Events = Guard.Optional(events, MaxEventsLength, nameof(events)) ?? string.Empty;

    public void MarkUsed(DateTime utcNow) { LastUsedAt = utcNow; LastError = null; }

    public void MarkFailed(string error) => LastError = error.Length > MaxErrorLength ? error[..MaxErrorLength] : error;

    public void MarkDigestSent(DateOnly day) => LastDigestOn = day;

    private void Apply(string label, string encryptedSecret, string settingsJson, string events, DateTime utcNow)
    {
        Label = Guard.Required(label, MaxLabelLength, nameof(label));
        EncryptedSecret = Guard.Required(encryptedSecret, MaxSecretLength, nameof(encryptedSecret));
        SettingsJson = Guard.Required(settingsJson, MaxSettingsLength, nameof(settingsJson));
        Events = Guard.Optional(events, MaxEventsLength, nameof(events)) ?? string.Empty;
        ConnectedAt = utcNow;
        LastError = null;
    }
}
