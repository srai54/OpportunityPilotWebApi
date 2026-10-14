using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using OpportunityPilot.Api.Auth;
using OpportunityPilot.Application.Configuration;

namespace OpportunityPilot.UnitTests;

/// <summary>Hanko session tokens checked against a locally generated key set served by a stub handler (no network).</summary>
public class HankoAuthTests
{
    private const string ApiUrl = "https://test-project.hanko.io";
    private const string HankoUser = "5e7b8c2a-1d34-4f6e-9a0b-112233445566";
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    private static readonly RsaSecurityKey KeyA = NewKey("key-a");
    private static readonly RsaSecurityKey KeyB = NewKey("key-b");

    private sealed class JwksHandler(Func<string> body) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            Assert.Equal($"{ApiUrl}/.well-known/jwks.json", request.RequestUri!.ToString());
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body(), Encoding.UTF8, "application/json") };
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(Send(request, ct));
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static RsaSecurityKey NewKey(string kid) => new(RSA.Create(2048)) { KeyId = kid };

    private static string Jwks(params RsaSecurityKey[] keys) =>
        "{\"keys\":[" + string.Join(",", keys.Select(k =>
        {
            var p = k.Rsa.ExportParameters(false);
            return $"{{\"kty\":\"RSA\",\"use\":\"sig\",\"alg\":\"RS256\",\"kid\":\"{k.KeyId}\"," +
                   $"\"n\":\"{Base64UrlEncoder.Encode(p.Modulus)}\",\"e\":\"{Base64UrlEncoder.Encode(p.Exponent)}\"}}";
        })) + "]}";

    private static string Token(SecurityKey key, string? issuer = null, string audience = "app.example.test",
        DateTime? expires = null, string subject = HankoUser, string algorithm = SecurityAlgorithms.RsaSha256)
    {
        var expiry = expires ?? DateTime.UtcNow.AddMinutes(10);
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Subject = new ClaimsIdentity([new Claim("sub", subject), new Claim("session_id", Guid.NewGuid().ToString())]),
            IssuedAt = expiry.AddMinutes(-30),
            NotBefore = expiry.AddMinutes(-30),
            Expires = expiry,
            SigningCredentials = new SigningCredentials(key, algorithm)
        });
    }

    private static HankoAuth.Settings Settings(string? audience = null, string? issuer = null) =>
        HankoAuth.Read(new AuthOptions { HankoApiUrl = ApiUrl + "/", HankoAudience = audience, HankoIssuer = issuer }, out _)!;

    private static (HankoJwks Jwks, JwksHandler Handler) Keys(Func<string> body, TimeProvider? clock = null)
    {
        var handler = new JwksHandler(body);
        return (new HankoJwks(new HttpClient(handler), $"{ApiUrl}/.well-known/jwks.json", NullLogger<HankoJwks>.Instance, clock), handler);
    }

    private static async Task<TokenValidationResult> Validate(string token, HankoAuth.Settings settings, HankoJwks jwks) =>
        await new JsonWebTokenHandler().ValidateTokenAsync(token, HankoAuth.ValidationParameters(settings, jwks));

    [Fact]
    public void Unset_url_means_hanko_is_off_without_a_setup_problem()
    {
        Assert.Null(HankoAuth.Read(new AuthOptions(), out var problem));
        Assert.Null(problem);
        Assert.Null(HankoAuth.Read(new AuthOptions { HankoApiUrl = "  " }, out problem));
        Assert.Null(problem);
    }

    [Theory]
    [InlineData("http://test-project.hanko.io")]
    [InlineData("test-project.hanko.io")]
    public void A_url_that_is_not_https_turns_hanko_off_and_says_why(string url)
    {
        Assert.Null(HankoAuth.Read(new AuthOptions { HankoApiUrl = url }, out var problem));
        Assert.Equal("Auth__HankoApiUrl must be an https URL, so Hanko sign-in is off.", problem);
    }

    [Fact]
    public void Settings_trim_the_url_and_split_the_audience_list()
    {
        var settings = Settings(" app.example.test , localhost ");
        Assert.Equal(ApiUrl, settings.ApiUrl);
        Assert.Equal($"{ApiUrl}/.well-known/jwks.json", settings.JwksUrl);
        Assert.Equal(["app.example.test", "localhost"], settings.Audiences);
    }

    [Fact]
    public void Owner_ids_are_stable_per_user_and_never_the_raw_hanko_id()
    {
        var owner = HankoAuth.OwnerIdFor(HankoUser);

        Assert.Equal(owner, HankoAuth.OwnerIdFor(HankoUser));
        Assert.NotEqual(Guid.Parse(HankoUser), owner);
        Assert.NotEqual(owner, HankoAuth.OwnerIdFor("5e7b8c2a-1d34-4f6e-9a0b-112233445567"));
        Assert.Equal(8, owner.Version);
    }

    [Fact]
    public async Task A_valid_token_signed_by_the_project_key_becomes_the_namespaced_owner()
    {
        var (jwks, _) = Keys(() => Jwks(KeyA));

        var result = await Validate(Token(KeyA), Settings("app.example.test"), jwks);

        Assert.True(result.IsValid, result.Exception?.Message);
        var principal = HankoAuth.ToOwnerPrincipal(new ClaimsPrincipal(result.ClaimsIdentity));
        Assert.NotNull(principal);
        Assert.Equal(HankoAuth.OwnerIdFor(HankoUser).ToString(), principal.FindFirst("sub")!.Value);
        Assert.Equal(HankoUser, principal.FindFirst(HankoAuth.HankoSubjectClaim)!.Value);
        Assert.Equal("hanko", principal.FindFirst(HankoAuth.ProviderClaim)!.Value);
        Assert.True(principal.Identity!.IsAuthenticated);
    }

    [Fact]
    public async Task The_api_url_is_accepted_as_issuer()
    {
        var (jwks, _) = Keys(() => Jwks(KeyA));
        Assert.True((await Validate(Token(KeyA, issuer: ApiUrl), Settings(), jwks)).IsValid);
    }

    [Fact]
    public async Task An_expired_token_is_rejected()
    {
        var (jwks, _) = Keys(() => Jwks(KeyA));

        var result = await Validate(Token(KeyA, expires: DateTime.UtcNow.AddMinutes(-5)), Settings(), jwks);

        Assert.False(result.IsValid);
        Assert.IsType<SecurityTokenExpiredException>(result.Exception);
    }

    [Theory]
    [InlineData("https://other-project.hanko.io")]
    [InlineData("https://abc.supabase.co/auth/v1")]
    public async Task A_token_from_another_issuer_is_rejected(string issuer)
    {
        var (jwks, _) = Keys(() => Jwks(KeyA));

        var result = await Validate(Token(KeyA, issuer: issuer), Settings(), jwks);

        Assert.False(result.IsValid);
        Assert.IsType<SecurityTokenInvalidIssuerException>(result.Exception);
    }

    [Fact]
    public async Task A_configured_issuer_must_be_present_and_exact()
    {
        var (jwks, _) = Keys(() => Jwks(KeyA));
        var settings = Settings(issuer: "https://auth.example.test");

        Assert.True((await Validate(Token(KeyA, issuer: "https://auth.example.test"), settings, jwks)).IsValid);
        Assert.False((await Validate(Token(KeyA), settings, jwks)).IsValid);
        Assert.False((await Validate(Token(KeyA, issuer: ApiUrl), settings, jwks)).IsValid);
    }

    [Fact]
    public async Task A_token_signed_with_an_unknown_key_is_rejected()
    {
        var (jwks, handler) = Keys(() => Jwks(KeyA));

        var result = await Validate(Token(KeyB), Settings(), jwks);

        Assert.False(result.IsValid);
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task A_token_with_a_known_kid_but_a_forged_signature_is_rejected()
    {
        var (jwks, _) = Keys(() => Jwks(KeyA));
        var forger = new RsaSecurityKey(RSA.Create(2048)) { KeyId = KeyA.KeyId };

        Assert.False((await Validate(Token(forger), Settings(), jwks)).IsValid);
    }

    [Fact]
    public async Task A_shared_secret_token_is_never_accepted()
    {
        var (jwks, _) = Keys(() => Jwks(KeyA));
        var secret = new SymmetricSecurityKey(Encoding.UTF8.GetBytes("test-only-shared-secret-that-is-long-enough-123456")) { KeyId = "key-a" };

        Assert.False((await Validate(Token(secret, algorithm: SecurityAlgorithms.HmacSha256), Settings(), jwks)).IsValid);
    }

    [Fact]
    public async Task The_audience_is_checked_only_when_configured()
    {
        var (jwks, _) = Keys(() => Jwks(KeyA));
        var token = Token(KeyA, audience: "someone-else.example.test");

        var strict = await Validate(token, Settings("app.example.test"), jwks);
        Assert.False(strict.IsValid);
        Assert.IsType<SecurityTokenInvalidAudienceException>(strict.Exception);
        Assert.True((await Validate(token, Settings(), jwks)).IsValid);
    }

    [Fact]
    public async Task A_rotated_key_is_fetched_once_the_refresh_interval_has_passed()
    {
        var clock = new ManualClock(T0);
        var published = Jwks(KeyA);
        var (jwks, handler) = Keys(() => published, clock);
        Assert.True((await Validate(Token(KeyA), Settings(), jwks)).IsValid);

        published = Jwks(KeyA, KeyB);
        clock.Now = T0.AddSeconds(10);
        Assert.False((await Validate(Token(KeyB), Settings(), jwks)).IsValid); // unknown kid, but refresh is rate-limited
        Assert.Equal(1, handler.Requests);

        clock.Now = T0.AddSeconds(31);
        Assert.True((await Validate(Token(KeyB), Settings(), jwks)).IsValid);
        Assert.Equal(2, handler.Requests);

        Assert.True((await Validate(Token(KeyA), Settings(), jwks)).IsValid); // cached: no extra request
        Assert.Equal(2, handler.Requests);
    }

    [Fact]
    public void A_principal_without_a_subject_is_refused()
    {
        Assert.Null(HankoAuth.ToOwnerPrincipal(new ClaimsPrincipal(new ClaimsIdentity([new Claim("email", "a@example.test")], "jwt"))));
        Assert.Null(HankoAuth.ToOwnerPrincipal(null));
    }

    [Fact]
    public void Guest_and_supabase_tokens_are_left_to_their_own_schemes()
    {
        const string supabaseIssuer = "https://abc.supabase.co/auth/v1";

        Assert.False(HankoAuth.ShouldTry(OpportunityPilot.Application.Auth.GuestSessionService.TokenPrefix + new string('a', 40), supabaseIssuer));
        Assert.False(HankoAuth.ShouldTry(null, supabaseIssuer));
        Assert.False(HankoAuth.ShouldTry("not-a-jwt", supabaseIssuer));
        Assert.False(HankoAuth.ShouldTry(Token(KeyA, issuer: supabaseIssuer), supabaseIssuer));
        Assert.True(HankoAuth.ShouldTry(Token(KeyA), supabaseIssuer));
        Assert.True(HankoAuth.ShouldTry(Token(KeyA, issuer: supabaseIssuer), null));
    }
}
