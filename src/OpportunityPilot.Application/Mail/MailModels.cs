using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpportunityPilot.Domain.Mail;

namespace OpportunityPilot.Application.Mail;

public sealed class MailProviderOptions
{
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    /// <summary>Microsoft only: the tenant to sign in against. "common" accepts work, school and personal accounts.</summary>
    public string Tenant { get; set; } = "common";
    public bool Configured => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
}

/// <summary>
/// Settings for sending from a person's own mailbox. Everything is empty by default, which keeps the feature "Not configured";
/// secrets are server settings (<c>Mail__Microsoft__ClientSecret</c> and so on) and never leave the server.
/// </summary>
public sealed class MailOptions
{
    public const string Section = "Mail";
    /// <summary>A 32-byte key, base64 (<c>Mail__TokenKey</c>), used to encrypt stored refresh tokens and the sign-in state.</summary>
    public string? TokenKey { get; set; }
    /// <summary>Public address of this API, used to build the redirect address registered with Microsoft and Google.</summary>
    public string? PublicApiBase { get; set; }
    /// <summary>Where the browser is sent after connecting.</summary>
    public string? WebBase { get; set; }
    public MailProviderOptions Microsoft { get; set; } = new();
    public MailProviderOptions Google { get; set; } = new();

    public bool HasKey => TryKey(out _);
    public bool TryKey(out byte[] key)
    {
        key = [];
        if (string.IsNullOrWhiteSpace(TokenKey)) return false;
        try { key = Convert.FromBase64String(TokenKey); } catch (FormatException) { return false; }
        return key.Length == 32;
    }

    public MailProviderOptions For(MailProvider p) => p == MailProvider.Microsoft ? Microsoft : Google;
    public bool Ready(MailProvider p) => HasKey && For(p).Configured && !string.IsNullOrWhiteSpace(PublicApiBase);
}

/// <summary>Authenticated encryption (AES-256-GCM) for tokens at rest and for short-lived values in a URL. Tampering fails closed.</summary>
public sealed class TokenProtector(Microsoft.Extensions.Options.IOptions<MailOptions> options)
{
    private const int NonceSize = 12, TagSize = 16;

    public string Protect(string plain)
    {
        if (!options.Value.TryKey(out var key)) throw new InvalidOperationException("Mail__TokenKey is not set.");
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var data = Encoding.UTF8.GetBytes(plain);
        var cipher = new byte[data.Length];
        var tag = new byte[TagSize];
        using var gcm = new AesGcm(key, TagSize);
        gcm.Encrypt(nonce, data, cipher, tag);
        return Base64Url(nonce.Concat(tag).Concat(cipher).ToArray());
    }

    /// <summary>The original text, or null if the value was altered, is from another key, or is not ours.</summary>
    public string? Unprotect(string protectedText)
    {
        if (!options.Value.TryKey(out var key)) return null;
        try
        {
            var all = FromBase64Url(protectedText);
            if (all.Length < NonceSize + TagSize) return null;
            var nonce = all[..NonceSize];
            var tag = all[NonceSize..(NonceSize + TagSize)];
            var cipher = all[(NonceSize + TagSize)..];
            var plain = new byte[cipher.Length];
            using var gcm = new AesGcm(key, TagSize);
            gcm.Decrypt(nonce, cipher, tag, plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception e) when (e is CryptographicException or FormatException or ArgumentException) { return null; }
    }

    public static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] FromBase64Url(string text)
    {
        var s = text.Replace('-', '+').Replace('_', '/');
        s = s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
        return Convert.FromBase64String(s);
    }
}

/// <summary>What the sign-in redirect carries back: who started it, with which provider, the PKCE verifier, and an expiry. Encrypted, so it cannot be forged or read.</summary>
public sealed record OAuthState(Guid OwnerId, MailProvider Provider, string Verifier, DateTime ExpiresAt)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    public string Seal(TokenProtector protector) => protector.Protect(JsonSerializer.Serialize(this));

    public static OAuthState? Open(TokenProtector protector, string? sealedState, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(sealedState) || protector.Unprotect(sealedState) is not { } json) return null;
        try { return JsonSerializer.Deserialize<OAuthState>(json) is { } s && s.ExpiresAt > now ? s : null; }
        catch (JsonException) { return null; }
    }

    public static (string Verifier, string Challenge) NewPkce()
    {
        var verifier = TokenProtector.Base64Url(RandomNumberGenerator.GetBytes(48));
        return (verifier, TokenProtector.Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))));
    }
}

public sealed record MailTokens(string AccessToken, string? RefreshToken, string EmailAddress);

public sealed record OutgoingMail(string To, string Subject, string Body);

/// <summary>Accepted = the provider took the message. It is not proof of delivery, and the UI says so.</summary>
public sealed record MailSendResult(bool Accepted, string? Reference, string Receipt, string? Failure, bool TokenRejected = false);

/// <summary>The provider calls (Microsoft Graph, Gmail API). One implementation talks HTTP; tests use a fake handler.</summary>
public interface IMailTransport
{
    string AuthorizeUrl(MailProvider provider, string redirectUri, string state, string challenge);
    Task<MailTokens> ExchangeCodeAsync(MailProvider provider, string code, string verifier, string redirectUri, CancellationToken ct);
    /// <summary>A new access token; null when the provider rejected the refresh token (revoked or expired).</summary>
    Task<string?> RefreshAsync(MailProvider provider, string refreshToken, CancellationToken ct);
    Task<MailSendResult> SendAsync(MailProvider provider, string accessToken, OutgoingMail mail, CancellationToken ct);
}
