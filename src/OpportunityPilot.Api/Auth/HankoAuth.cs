using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using OpportunityPilot.Application.Auth;
using OpportunityPilot.Application.Configuration;

namespace OpportunityPilot.Api.Auth;

/// <summary>
/// Hanko (hanko.io) session tokens: an RS256-signed JWT whose "sub" is the Hanko user id and whose "aud" is the passkey
/// relying-party id (the web app's domain). Accepted next to Supabase tokens when Auth:HankoApiUrl is set.
/// </summary>
public static class HankoAuth
{
    public const string SchemeName = "Hanko";
    public const string ProviderClaim = "auth_provider";
    public const string HankoSubjectClaim = "hanko_sub";

    /// <summary>Fixed namespace for owner ids derived from Hanko user ids. Never change it: owners would lose their data.</summary>
    private static readonly Guid OwnerNamespace = new("eea1a69b-4fc4-40f0-af93-0df0ce6ca4a9");

    /// <summary>Asymmetric only: a shared-secret (HS*) or unsigned token is never a Hanko token.</summary>
    private static readonly string[] Algorithms =
        [SecurityAlgorithms.RsaSha256, SecurityAlgorithms.EcdsaSha256, "EdDSA"];

    public sealed record Settings(string ApiUrl, IReadOnlyList<string> Audiences, string? Issuer)
    {
        public string JwksUrl => $"{ApiUrl}/.well-known/jwks.json";
    }

    /// <summary>The usable Hanko settings, or null with the reason when the URL is set but not https.</summary>
    public static Settings? Read(AuthOptions options, out string? problem)
    {
        problem = null;
        var url = options.HankoApiUrl?.Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(url)) return null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            problem = "Auth__HankoApiUrl must be an https URL, so Hanko sign-in is off.";
            return null;
        }

        // Hanko puts the app's domain in "aud"; accept the setting written as a host or as a full URL, with or without a slash.
        var audiences = (options.HankoAudience ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .SelectMany(AudienceForms).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var issuer = string.IsNullOrWhiteSpace(options.HankoIssuer) ? null : options.HankoIssuer.Trim();
        return new Settings(url, audiences, issuer);
    }

    /// <summary>
    /// The owner id of a Hanko user: a name-based UUID (RFC 9562 version 8, SHA-256) of "hanko:{user id}". Stable for a
    /// user, and never equal to a Supabase user id or a guest id, so one person's data never mixes across sign-in systems.
    /// </summary>
    public static Guid OwnerIdFor(string hankoUserId)
    {
        Span<byte> buffer = stackalloc byte[16];
        OwnerNamespace.TryWriteBytes(buffer, bigEndian: true, out _);
        var hash = SHA256.HashData([.. buffer, .. Encoding.UTF8.GetBytes("hanko:" + hankoUserId)]);
        hash[6] = (byte)((hash[6] & 0x0F) | 0x80);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash.AsSpan(0, 16), bigEndian: true);
    }

    public static TokenValidationParameters ValidationParameters(Settings settings, JwksCache jwks) => new()
    {
        ValidateIssuer = true,
        IssuerValidator = (issuer, _, _) => ValidateIssuer(settings, issuer),
        ValidateAudience = settings.Audiences.Count > 0,
        ValidAudiences = settings.Audiences,
        ValidateLifetime = true,
        RequireExpirationTime = true,
        ValidateIssuerSigningKey = true,
        RequireSignedTokens = true,
        ValidAlgorithms = Algorithms,
        ClockSkew = TimeSpan.FromMinutes(1),
        IssuerSigningKeyResolver = (_, _, kid, _) => jwks.GetKeys(kid)
    };

    private static string ValidateIssuer(Settings settings, string? issuer)
    {
        if (settings.Issuer is not null)
            return string.Equals(issuer, settings.Issuer, StringComparison.Ordinal)
                ? issuer!
                : throw new SecurityTokenInvalidIssuerException("The token issuer is not the configured Hanko issuer.");

        // The signing keys come only from this project's JWKS, so the signature already proves the token is this project's.
        // Hanko Cloud may put the app's URL (not the API URL) in "iss"; requiring a match here only caused false sign-outs.
        return string.IsNullOrEmpty(issuer) ? settings.ApiUrl : issuer;
    }

    /// <summary>"app.example.com", "https://app.example.com" and "https://app.example.com/" all name the same audience.</summary>
    public static IEnumerable<string> AudienceForms(string value)
    {
        var v = value.Trim().TrimEnd('/');
        var host = Uri.TryCreate(v, UriKind.Absolute, out var u) && u.Scheme is "https" or "http" ? u.Authority : v;
        return [host, $"https://{host}", $"https://{host}/"];
    }

    /// <summary>
    /// The request identity for a validated Hanko token: "sub" becomes the derived owner id, the Hanko user id is kept
    /// as "hanko_sub". Null when the token has no subject.
    /// </summary>
    /// <summary>
    /// The user's email from a Hanko session token, when the project includes it: either a plain string or an object
    /// {"address": ..., "is_verified": ...}. An address marked unverified is ignored. Used only as a label and to recognise the admin.
    /// </summary>
    public static string? EmailFrom(ClaimsPrincipal? validated)
    {
        var raw = validated?.FindFirst("email")?.Value?.Trim();
        if (string.IsNullOrEmpty(raw)) return null;
        if (!raw.StartsWith('{')) return raw.Contains('@') && raw.Length <= 256 ? raw : null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (root.TryGetProperty("is_verified", out var v) && v.ValueKind == System.Text.Json.JsonValueKind.False) return null;
            var address = root.TryGetProperty("address", out var a) && a.ValueKind == System.Text.Json.JsonValueKind.String ? a.GetString()?.Trim() : null;
            return address is { Length: > 0 and <= 256 } && address.Contains('@') ? address : null;
        }
        catch (System.Text.Json.JsonException) { return null; }
    }

    public static ClaimsPrincipal? ToOwnerPrincipal(ClaimsPrincipal? validated)
    {
        var hankoUserId = validated?.FindFirst("sub")?.Value;
        if (string.IsNullOrWhiteSpace(hankoUserId) || hankoUserId.Length > 255) return null;

        var claims = new List<Claim>
        {
            new("sub", OwnerIdFor(hankoUserId).ToString()),
            new(HankoSubjectClaim, hankoUserId),
            new(ProviderClaim, "hanko")
        };
        if (EmailFrom(validated) is { } email) claims.Add(new Claim("email", email));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName, "sub", null));
    }

    /// <summary>
    /// Whether this scheme should look at the bearer token at all: not a guest token, and not a token issued by the
    /// configured Supabase project (that scheme handles it). Everything else is validated, and rejected if not Hanko's.
    /// </summary>
    public static bool ShouldTry(string? token, string? supabaseIssuer)
    {
        if (string.IsNullOrWhiteSpace(token) || token.StartsWith(GuestSessionService.TokenPrefix, StringComparison.Ordinal))
            return false;
        if (supabaseIssuer is null) return true;
        try
        {
            return !string.Equals(new JsonWebToken(token).Issuer, supabaseIssuer, StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
            return false; // not a JWT
        }
    }
}
