using Microsoft.IdentityModel.Tokens;

namespace OpportunityPilot.Api.Auth;

/// <summary>
/// Caches a JSON Web Key Set fetched from <paramref name="jwksUrl"/>.
/// Refreshes every 10 minutes, or sooner when a token arrives with an unknown key id (rate-limited to once per 30s).
/// </summary>
public class JwksCache(HttpClient http, string jwksUrl, string provider, ILogger logger, TimeProvider? clock = null)
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan MinRefreshInterval = TimeSpan.FromSeconds(30);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private IList<SecurityKey> _keys = [];
    private DateTimeOffset _fetchedAt = DateTimeOffset.MinValue;

    public IEnumerable<SecurityKey> GetKeys(string? kid)
    {
        lock (_gate)
        {
            var age = _clock.GetUtcNow() - _fetchedAt;
            var unknownKid = kid is not null && _keys.All(k => k.KeyId != kid);
            if (age > MaxAge || (unknownKid && age > MinRefreshInterval)) Refresh();
            return kid is null ? _keys : _keys.Where(k => k.KeyId == kid);
        }
    }

    private void Refresh()
    {
        try
        {
            using var response = http.Send(new HttpRequestMessage(HttpMethod.Get, jwksUrl));
            response.EnsureSuccessStatusCode();
            using var reader = new StreamReader(response.Content.ReadAsStream());
            _keys = new JsonWebKeySet(reader.ReadToEnd()).GetSigningKeys();
            _fetchedAt = _clock.GetUtcNow();
        }
        catch (Exception ex)
        {
            // Keep the previous keys; tokens signed by an unknown key will fail validation.
            logger.LogWarning(ex, "Could not refresh {Provider} JWKS", provider);
            _fetchedAt = _clock.GetUtcNow() - MaxAge + MinRefreshInterval;
        }
    }
}

/// <summary>The Supabase project's keys ({SupabaseUrl}/auth/v1/.well-known/jwks.json).</summary>
public sealed class SupabaseJwks(HttpClient http, string jwksUrl, ILogger<SupabaseJwks> logger)
    : JwksCache(http, jwksUrl, "Supabase", logger);

/// <summary>The Hanko project's keys ({HankoApiUrl}/.well-known/jwks.json).</summary>
public sealed class HankoJwks(HttpClient http, string jwksUrl, ILogger<HankoJwks> logger, TimeProvider? clock = null)
    : JwksCache(http, jwksUrl, "Hanko", logger, clock);
