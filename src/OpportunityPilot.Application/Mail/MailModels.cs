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
    /// <summary>
    /// Used when <c>Mail__TokenKey</c> is not set, so sending with an app password works with no extra setting: a key is derived from this server
    /// secret (the database connection string). If that secret changes, stored mailbox logins stop opening and the person reconnects.
    /// </summary>
    public string? FallbackSecret { get; set; }
    /// <summary>Tests only: allow an SMTP server on a private address. Never set in production.</summary>
    public bool AllowPrivateSmtpHosts { get; set; }
    public MailProviderOptions Microsoft { get; set; } = new();
    public MailProviderOptions Google { get; set; } = new();

    public bool HasKey => TryKey(out _);
    public bool TryKey(out byte[] key)
    {
        key = [];
        if (string.IsNullOrWhiteSpace(TokenKey))
        {
            if (string.IsNullOrWhiteSpace(FallbackSecret)) return false;
            key = SHA256.HashData(Encoding.UTF8.GetBytes("opportunitypilot-mail-v1:" + FallbackSecret));
            return true;
        }
        try { key = Convert.FromBase64String(TokenKey); } catch (FormatException) { return false; }
        return key.Length == 32;
    }

    public MailProviderOptions For(MailProvider p) => p == MailProvider.Microsoft ? Microsoft : Google;
    public bool Ready(MailProvider p) => p == MailProvider.Smtp ? HasKey : HasKey && For(p).Configured && !string.IsNullOrWhiteSpace(PublicApiBase);
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

/// <summary>The login for sending through SMTP. Stored only encrypted; never returned, logged or exported.</summary>
public sealed record SmtpCredentials(string Host, int Port, string Username, string Password, bool Ssl = true);

/// <param name="Preset">gmail, outlook, office365, yahoo or custom (then Host is required).</param>
public sealed record ConnectSmtpRequest(string Preset, string? Host, int? Port, string Username, string Password);

public static class SmtpPresets
{
    public static readonly IReadOnlyDictionary<string, (string Host, int Port, string Name)> All = new Dictionary<string, (string, int, string)>(StringComparer.OrdinalIgnoreCase)
    {
        ["gmail"] = ("smtp.gmail.com", 587, "Gmail"),
        ["outlook"] = ("smtp-mail.outlook.com", 587, "Outlook.com"),
        ["office365"] = ("smtp.office365.com", 587, "Microsoft 365"),
        ["yahoo"] = ("smtp.mail.yahoo.com", 587, "Yahoo"),
    };

    /// <summary>Ports a custom server may use. 465 (implicit TLS) is not supported by the built-in client.</summary>
    public static readonly int[] AllowedPorts = [587, 2525];
}

/// <summary>Where replies are read from: the IMAP server that belongs to the stored SMTP server, and the public-address check for custom servers.</summary>
public static class MailHosts
{
    private static readonly IReadOnlyDictionary<string, string> KnownImap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["smtp.gmail.com"] = "imap.gmail.com",
        ["smtp-mail.outlook.com"] = "outlook.office365.com",
        ["smtp.office365.com"] = "outlook.office365.com",
        ["smtp.mail.yahoo.com"] = "imap.mail.yahoo.com",
    };

    /// <summary>The IMAP host for a stored SMTP host: the preset's own server, else <c>smtp.</c> replaced by <c>imap.</c> (a host without that prefix is used as is).</summary>
    public static string ImapHostFor(string smtpHost)
    {
        var host = (smtpHost ?? string.Empty).Trim().ToLowerInvariant();
        if (KnownImap.TryGetValue(host, out var known)) return known;
        return host.StartsWith("smtp.", StringComparison.Ordinal) ? "imap." + host["smtp.".Length..] : host;
    }

    /// <summary>Outlook.com and Microsoft 365: Microsoft has turned off app-password (basic) sign-in to IMAP for most of these accounts.</summary>
    public static bool IsMicrosoft(string smtpHost) =>
        ImapHostFor(smtpHost) == "outlook.office365.com" || (smtpHost ?? string.Empty).EndsWith(".outlook.com", StringComparison.OrdinalIgnoreCase)
        || (smtpHost ?? string.Empty).EndsWith(".office365.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The host's addresses when every one of them is public, else null (also when it does not resolve). Connecting to exactly these
    /// addresses (not the name again) means a second DNS answer cannot point the connection at this server's own network.
    /// </summary>
    public static async Task<System.Net.IPAddress[]?> PublicAddressesAsync(string host, bool allowPrivate, CancellationToken ct)
    {
        try
        {
            var addresses = System.Net.IPAddress.TryParse(host, out var literal) ? [literal] : await System.Net.Dns.GetHostAddressesAsync(host, ct);
            if (addresses.Length == 0) return null;
            return allowPrivate || addresses.All(a => !IsPrivate(a)) ? addresses : null;
        }
        catch (System.Net.Sockets.SocketException) { return null; }
        catch (ArgumentException) { return null; }
    }

    public static bool IsPrivate(System.Net.IPAddress a)
    {
        if (System.Net.IPAddress.IsLoopback(a) || a.IsIPv6LinkLocal || a.IsIPv6SiteLocal || a.IsIPv6Multicast) return true;
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        if (a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            var v6 = a.GetAddressBytes();
            // Only global unicast (2000::/3) is public. That also rejects ::, ::1, IPv4-compatible and NAT64 (64:ff9b::/96),
            // unique-local fc00::/7, link-local fe80::/10 and multicast. Inside 2000::/3: 6to4 (2002::/16) and Teredo
            // (2001::/32) can carry a private IPv4, and 2001:db8::/32 is documentation only.
            if ((v6[0] & 0xE0) != 0x20) return true;
            if (v6[0] == 0x20 && v6[1] == 0x02) return true;
            if (v6[0] == 0x20 && v6[1] == 0x01 && (v6[2] == 0x0d && v6[3] == 0xb8 || v6[2] == 0 && v6[3] == 0)) return true;
            return false;
        }
        var b = a.GetAddressBytes();
        return b[0] is 10 or 0 or 127 || b[0] == 169 && b[1] == 254 || b[0] == 172 && b[1] is >= 16 and <= 31 || b[0] == 192 && b[1] == 168 || b[0] == 100 && b[1] is >= 64 and <= 127 || b[0] >= 224
            || b[0] == 192 && b[1] == 0 && b[2] == 0 || b[0] == 198 && b[1] is 18 or 19;
    }
}

/// <summary>One message's headers as read from the inbox. Only these fields are read; never the body.</summary>
/// <param name="ReceivedAt">UTC; the server's arrival time (IMAP INTERNALDATE), else the Date header.</param>
/// <param name="AutoReply">An out-of-office or other automatic answer (Auto-Submitted, or a subject like "Automatic reply:").</param>
public sealed record MailHeader(string FromAddress, string? Subject, DateTime ReceivedAt, string? MessageId, string? InReplyTo, string? References, bool AutoReply = false);

/// <summary>What reading the inbox gave. <paramref name="Failure"/> is a safe sentence for the person; null when the read worked.</summary>
/// <param name="LoginRefused">The server refused the stored login (or it could not be read).</param>
public sealed record MailboxReadResult(IReadOnlyList<MailHeader> Messages, bool LoginRefused = false, string? Failure = null)
{
    public static MailboxReadResult Failed(string failure, bool loginRefused = false) => new([], loginRefused, failure);
}

/// <summary>Reads message headers from the person's own inbox over IMAP with the stored app password. Read-only: nothing is marked read, moved or deleted.</summary>
public interface IMailboxReader
{
    public const int MaxMessages = 500;

    /// <summary>Headers of INBOX messages that arrived on or after the UTC day of <paramref name="sinceUtc"/>, newest at most <see cref="MaxMessages"/>.</summary>
    /// <param name="credentialsJson">The decrypted <see cref="SmtpCredentials"/>.</param>
    Task<MailboxReadResult> ReadHeadersSinceAsync(string credentialsJson, DateTime sinceUtc, CancellationToken ct);
}
