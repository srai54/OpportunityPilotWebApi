using OpportunityPilot.Domain.Common;

namespace OpportunityPilot.Domain.Ai;

/// <summary>
/// A person's own Anthropic API key (2026-10-26), so the app's AI runs on Claude billed to their (or their company's) Anthropic account.
/// Stored only encrypted; the last four characters are kept to show which key is connected. Never returned, logged or exported.
/// </summary>
public sealed class AiCredential
{
    public const int MaxEncryptedLength = 1_024;
    public const int MaxModelLength = 64;
    public const string DefaultModel = "claude-opus-5-5";
    public static readonly string[] Models = ["claude-opus-5-5", "claude-sonnet-5-5", "claude-haiku-5-5"];

    private AiCredential() { }

    public AiCredential(Guid ownerId, string encryptedKey, string keyHint, string? model, DateTime utcNow)
    {
        if (ownerId == Guid.Empty) throw new ArgumentException("Owner is required.", nameof(ownerId));
        OwnerId = ownerId;
        Replace(encryptedKey, keyHint, utcNow);
        SetModel(model);
        CreatedAt = utcNow;
    }

    public Guid OwnerId { get; private set; }
    public string EncryptedKey { get; private set; } = string.Empty;
    public string KeyHint { get; private set; } = string.Empty;
    public string Model { get; private set; } = DefaultModel;
    /// <summary>Lets candidates on this person's progress links tailor their resume with this key (rate limited). Off by default.</summary>
    public bool ShareWithCandidates { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime CheckedAt { get; private set; }

    public void Replace(string encryptedKey, string keyHint, DateTime utcNow)
    {
        EncryptedKey = Guard.Required(encryptedKey, MaxEncryptedLength, nameof(encryptedKey));
        KeyHint = Guard.Truncate(keyHint ?? string.Empty, 8);
        CheckedAt = utcNow;
    }

    public void SetModel(string? model) =>
        Model = string.IsNullOrWhiteSpace(model) ? DefaultModel
            : Models.Contains(model.Trim()) ? model.Trim() : throw new ArgumentOutOfRangeException(nameof(model), "Unknown model.");

    public void SetSharing(bool share) => ShareWithCandidates = share;
}
