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

        var audiences = (options.HankoAudience ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
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

        // Hanko adds "iss" only when the project sets one. No issuer, or the API URL itself, is this project.
        if (string.IsNullOrEmpty(issuer)) return settings.ApiUrl;
        return string.Equals(issuer.TrimEnd('/'), settings.ApiUrl, StringComparison.OrdinalIgnoreCase)
            ? issuer
            : throw new SecurityTokenInvalidIssuerException("The token issuer is not this Hanko project.");
    }

    /// <summary>
    /// The request identity for a validated Hanko token: "sub" becomes the derived owner id, the Hanko user id is kept
    /// as "hanko_sub". Null when the token has no subject.
    /// </summary>
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
