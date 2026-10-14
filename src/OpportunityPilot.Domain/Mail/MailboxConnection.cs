using OpportunityPilot.Domain.Common;

namespace OpportunityPilot.Domain.Mail;

/// <summary>Microsoft and Google connect with a sign-in (OAuth); Smtp connects with the person's own address and an app password.</summary>
public enum MailProvider { Microsoft, Google, Smtp }

public enum MailboxStatus { Connected, NeedsReconnect }

/// <summary>
/// A person's own mailbox (Outlook or Gmail) that they connected so approved messages can be sent from it. The refresh token is the
/// key to that mailbox, so it is stored only encrypted and never returned by the API, logged or exported. One connection per
/// provider per owner; disconnecting deletes it.
/// </summary>
public sealed class MailboxConnection : IOwned
{
    public const int MaxEmailLength = 320;
    public const int MaxTokenLength = 8_000;

    private MailboxConnection() { }

    public MailboxConnection(Guid ownerId, MailProvider provider, string emailAddress, string encryptedRefreshToken, DateTime utcNow)
    {
        if (ownerId == Guid.Empty) throw new ArgumentException("Owner is required.", nameof(ownerId));
        if (!Enum.IsDefined(provider)) throw new ArgumentOutOfRangeException(nameof(provider));
        Id = Guid.NewGuid();
        OwnerId = ownerId;
        Provider = provider;
        EmailAddress = Guard.Required(emailAddress, MaxEmailLength, nameof(emailAddress));
        EncryptedRefreshToken = Guard.Required(encryptedRefreshToken, MaxTokenLength, nameof(encryptedRefreshToken));
        Status = MailboxStatus.Connected;
        ConnectedAt = utcNow;
    }

    public Guid Id { get; private set; }
    public Guid OwnerId { get; private set; }
    public MailProvider Provider { get; private set; }
    public string EmailAddress { get; private set; } = string.Empty;
    public string EncryptedRefreshToken { get; private set; } = string.Empty;
    public MailboxStatus Status { get; private set; }
    public DateTime ConnectedAt { get; private set; }
    public DateTime? LastUsedAt { get; private set; }

    /// <summary>The same person connecting again (for example after revoking access) replaces the stored token.</summary>
    public void Reconnect(string emailAddress, string encryptedRefreshToken, DateTime utcNow)
    {
        EmailAddress = Guard.Required(emailAddress, MaxEmailLength, nameof(emailAddress));
        EncryptedRefreshToken = Guard.Required(encryptedRefreshToken, MaxTokenLength, nameof(encryptedRefreshToken));
        Status = MailboxStatus.Connected;
        ConnectedAt = utcNow;
    }

    public void MarkUsed(DateTime utcNow) => LastUsedAt = utcNow;

    /// <summary>The provider refused the token (revoked or expired): the person must connect again; nothing is sent meanwhile.</summary>
    public void MarkNeedsReconnect() => Status = MailboxStatus.NeedsReconnect;
}
