using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace OpportunityPilot.IntegrationTests;

/// <summary>Production-environment startup behaviour. No Docker needed: none of these reach a real database.</summary>
public class StartupGuardTests
{
    private sealed class ProductionFactory(Dictionary<string, string> settings, Action<IServiceCollection>? services = null)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            foreach (var (key, value) in settings) builder.UseSetting(key, value);
            if (services is not null) builder.ConfigureTestServices(services);
        }
    }

    private const string HankoAlice = "5e7b8c2a-1d34-4f6e-9a0b-112233445566";
    private const string HankoBob = "5e7b8c2a-1d34-4f6e-9a0b-112233445567";

    /// <summary>Serves a locally generated key set in place of {HankoApiUrl}/.well-known/jwks.json (no network).</summary>
    private sealed class HankoJwksStub(RsaSecurityKey key) : HttpMessageHandler
    {
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.ToString() != "https://test-project.hanko.io/.well-known/jwks.json")
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            var p = key.Rsa.ExportParameters(false);
            var body = JsonSerializer.Serialize(new
            {
                keys = new[]
                {
                    new { kty = "RSA", use = "sig", alg = "RS256", kid = key.KeyId, n = Base64UrlEncoder.Encode(p.Modulus), e = Base64UrlEncoder.Encode(p.Exponent) }
                }
            });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(Send(request, ct));
    }

    private static string HankoToken(SecurityKey key, string subject, DateTime? expires = null, string audience = "app.example.test")
    {
        var expiry = expires ?? DateTime.UtcNow.AddMinutes(10);
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Audience = audience,
            Subject = new ClaimsIdentity([new Claim("sub", subject), new Claim("session_id", Guid.NewGuid().ToString())]),
            IssuedAt = expiry.AddMinutes(-30),
            NotBefore = expiry.AddMinutes(-30),
            Expires = expiry,
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256)
        });
    }

    private static ProductionFactory WithHanko(RsaSecurityKey key, Dictionary<string, string> extra)
    {
        var settings = new Dictionary<string, string>
        {
            ["ConnectionStrings:Main"] = "",
            ["Auth:HankoApiUrl"] = "https://test-project.hanko.io"
        };
        foreach (var (k, v) in extra) settings[k] = v;
        return new ProductionFactory(settings,
            s => s.AddHttpClient("HankoJwks").ConfigurePrimaryHttpMessageHandler(() => new HankoJwksStub(key)));
    }

    private static HttpClient Bearer(ProductionFactory factory, string token)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private record GuestSession(string Token, DateTime ExpiresAt);

    private static ProductionFactory DemoMode() => new(new() { ["ConnectionStrings:Main"] = "", ["Auth:SupabaseUrl"] = "" });

    private static async Task<HttpClient> GuestClient(ProductionFactory factory)
    {
        var client = factory.CreateClient();
        var session = await (await client.PostAsync("/api/v1/auth/guest", null)).Content.ReadFromJsonAsync<GuestSession>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session!.Token);
        return client;
    }

    private static string LegacySupabaseToken(string secret, string subject = "11111111-1111-1111-1111-111111111111") =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "https://example.supabase.co/auth/v1",
            Audience = "authenticated",
            Subject = new ClaimsIdentity([new Claim("sub", subject)]),
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)),
                SecurityAlgorithms.HmacSha256)
        });

    [Fact]
    public void Dev_bypass_cannot_be_enabled_outside_development()
    {
        using var factory = new ProductionFactory(new()
        {
            ["ConnectionStrings:Main"] = "Host=unused;Database=unused",
            ["Auth:DevBypass"] = "true",
            ["Auth:SupabaseUrl"] = "https://example.supabase.co"
        });
        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("DevBypass", ex.ToString());
    }

    [Fact]
    public async Task With_no_configuration_the_app_works_in_demo_mode()
    {
        using var factory = DemoMode();
        var anonymous = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/health/ready")).StatusCode);

        var caps = JsonDocument.Parse(await anonymous.GetStringAsync("/api/v1/capabilities")).RootElement;
        Assert.True(caps.GetProperty("guestSignIn").GetBoolean());
        Assert.True(caps.GetProperty("temporaryStorage").GetBoolean());
        Assert.Equal(2, caps.GetProperty("setupRequired").GetArrayLength());

        var guest = await GuestClient(factory);
        var created = await guest.PostAsJsonAsync("/api/v1/profiles",
            new { type = "Candidate", name = "Demo profile", data = new { offer = "x" }, confirmed = false });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Contains("\"profiles\":1", await guest.GetStringAsync("/api/v1/overview"));
    }

    [Fact]
    public async Task Guests_are_isolated_and_unsigned_or_forged_credentials_are_rejected()
    {
        using var factory = DemoMode();
        var alice = await GuestClient(factory);
        var bob = await GuestClient(factory);

        await alice.PostAsJsonAsync("/api/v1/profiles", new { type = "Services", name = "Alice", data = new { }, confirmed = false });
        Assert.Contains("\"profiles\":0", await bob.GetStringAsync("/api/v1/overview"));

        var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/profiles")).StatusCode);

        var forged = new HttpRequestMessage(HttpMethod.Get, "/api/v1/profiles");
        var token = alice.DefaultRequestHeaders.Authorization!.Parameter!;
        forged.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token[..^4] + "AAAA");
        forged.Headers.Add("X-Dev-User", "alice");
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.SendAsync(forged)).StatusCode);
    }

    [Fact]
    public async Task Agent_keys_work_in_demo_mode_and_guest_tokens_cannot_report()
    {
        using var factory = DemoMode();
        var guest = await GuestClient(factory);
        var created = await guest.PostAsJsonAsync("/api/v1/agent-keys", new { name = "Demo laptop" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var key = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("key").GetString()!;

        var report = new
        {
            items = new[]
            {
                new
                {
                    platform = "Naukri", externalJobId = "n-1", jobUrl = "https://www.naukri.com/job-listings-n-1",
                    title = "Engineer", company = "Acme", status = "Applied", occurredAt = DateTime.UtcNow
                }
            }
        };
        Assert.Equal(HttpStatusCode.Unauthorized, (await guest.PostAsJsonAsync("/api/v1/applications/report", report)).StatusCode);

        var agent = factory.CreateClient();
        agent.DefaultRequestHeaders.Add("X-Agent-Key", key);
        Assert.Equal(HttpStatusCode.OK, (await agent.PostAsJsonAsync("/api/v1/applications/report", report)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await agent.GetAsync("/api/v1/profiles")).StatusCode);

        Assert.Contains("\"applied\":1", await guest.GetStringAsync("/api/v1/overview"));
    }

    [Fact]
    public async Task Research_runs_in_demo_mode_through_the_real_background_processor()
    {
        using var factory = DemoMode();
        var guest = await GuestClient(factory);
        var profile = await guest.PostAsJsonAsync("/api/v1/profiles",
            new { type = "Candidate", name = "Demo candidate", data = new { }, confirmed = true });
        var profileId = JsonDocument.Parse(await profile.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        var campaign = await guest.PostAsJsonAsync("/api/v1/campaigns", new
        {
            profileId, mode = "Job", name = "Demo search", goal = "",
            criteria = new { requiredSkills = new[] { "C#" }, locations = new[] { "Pune" } }
        });
        Assert.Equal(HttpStatusCode.Created, campaign.StatusCode);
        var campaignId = JsonDocument.Parse(await campaign.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        var source = await guest.PostAsJsonAsync($"/api/v1/campaigns/{campaignId}/sources",
            new { kind = "Paste", text = "Backend Engineer\nCompany: Acme\nLocation: Pune\nC# and SQL.\n---\nDesigner\nCompany: Globex\nLocation: Pune\nFigma." });
        Assert.Equal(HttpStatusCode.Created, source.StatusCode);

        var queued = await guest.PostAsync($"/api/v1/campaigns/{campaignId}/research", null);
        Assert.Equal(HttpStatusCode.Accepted, queued.StatusCode);
        var jobId = JsonDocument.Parse(await queued.Content.ReadAsStringAsync()).RootElement.GetProperty("jobId").GetGuid();

        string? state = null;
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var job = JsonDocument.Parse(await guest.GetStringAsync($"/api/v1/research-jobs/{jobId}")).RootElement;
            state = job.GetProperty("state").GetString();
            if (state is not ("Queued" or "Running")) break;
            await Task.Delay(250);
        }

        Assert.Equal("Completed", state);
        var page = JsonDocument.Parse(await guest.GetStringAsync($"/api/v1/campaigns/{campaignId}/opportunities")).RootElement;
        Assert.Equal(2, page.GetProperty("total").GetInt32());
        Assert.Contains(page.GetProperty("items").EnumerateArray(),
            i => i.GetProperty("title").GetString() == "Backend Engineer" && i.GetProperty("outcome").GetString() == "Qualified");
        Assert.Contains("\"campaigns\":1", await guest.GetStringAsync("/api/v1/overview"));
    }

    [Fact]
    public async Task Guests_stay_available_next_to_accounts_by_default()
    {
        using var factory = new ProductionFactory(new()
        {
            ["ConnectionStrings:Main"] = "",
            ["Auth:SupabaseUrl"] = "https://example.supabase.co"
        });
        var guest = await GuestClient(factory);

        Assert.Equal(HttpStatusCode.OK, (await guest.GetAsync("/api/v1/overview")).StatusCode);
        var caps = JsonDocument.Parse(await guest.GetStringAsync("/api/v1/capabilities")).RootElement;
        Assert.True(caps.GetProperty("guestSignIn").GetBoolean());
    }

    [Fact]
    public async Task Guest_sign_in_is_off_when_accounts_are_on_and_guests_are_disallowed()
    {
        using var factory = new ProductionFactory(new()
        {
            ["ConnectionStrings:Main"] = "",
            ["Auth:SupabaseUrl"] = "https://example.supabase.co",
            ["Auth:AllowGuests"] = "false"
        });
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/api/v1/auth/guest", null)).StatusCode);
        var caps = JsonDocument.Parse(await client.GetStringAsync("/api/v1/capabilities")).RootElement;
        Assert.False(caps.GetProperty("guestSignIn").GetBoolean());
        Assert.True(caps.GetProperty("temporaryStorage").GetBoolean());
    }

    [Fact]
    public async Task Legacy_hs256_tokens_require_the_explicit_secret_and_a_guid_subject()
    {
        const string secret = "test-only-legacy-secret-that-is-long-enough-1234567890";
        var token = LegacySupabaseToken(secret);

        using var withoutLegacy = new ProductionFactory(new()
        {
            ["ConnectionStrings:Main"] = "",
            ["Auth:SupabaseUrl"] = "https://example.supabase.co"
        });
        var rejected = withoutLegacy.CreateClient();
        rejected.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.Unauthorized, (await rejected.GetAsync("/api/v1/overview")).StatusCode);

        using var withLegacy = new ProductionFactory(new()
        {
            ["ConnectionStrings:Main"] = "",
            ["Auth:SupabaseUrl"] = "https://example.supabase.co",
            ["Auth:LegacyJwtSecret"] = secret
        });
        var accepted = withLegacy.CreateClient();
        accepted.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.OK, (await accepted.GetAsync("/api/v1/overview")).StatusCode);

        var badSubject = withLegacy.CreateClient();
        badSubject.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", LegacySupabaseToken(secret, "not-a-guid"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await badSubject.GetAsync("/api/v1/overview")).StatusCode);
    }

    [Fact]
    public async Task Without_a_hanko_url_capabilities_list_supabase_only_and_hanko_tokens_are_rejected()
    {
        using var factory = new ProductionFactory(new()
        {
            ["ConnectionStrings:Main"] = "",
            ["Auth:SupabaseUrl"] = "https://example.supabase.co"
        });
        var client = factory.CreateClient();

        var caps = JsonDocument.Parse(await client.GetStringAsync("/api/v1/capabilities")).RootElement;
        Assert.Equal(["supabase"], caps.GetProperty("signInProviders").EnumerateArray().Select(e => e.GetString()));

        using var rsa = RSA.Create(2048);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", HankoToken(new RsaSecurityKey(rsa) { KeyId = "hanko-key-1" }, HankoAlice));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/overview")).StatusCode);
    }

    [Fact]
    public async Task Hanko_tokens_sign_in_as_separate_owners_next_to_supabase_and_guests()
    {
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa) { KeyId = "hanko-key-1" };
        using var factory = WithHanko(key, new() { ["Auth:SupabaseUrl"] = "https://example.supabase.co", ["Auth:HankoAudience"] = "app.example.test" });
        var anonymous = factory.CreateClient();

        var caps = JsonDocument.Parse(await anonymous.GetStringAsync("/api/v1/capabilities")).RootElement;
        Assert.Equal(["hanko", "supabase"], caps.GetProperty("signInProviders").EnumerateArray().Select(e => e.GetString()));
        Assert.True(caps.GetProperty("guestSignIn").GetBoolean());

        var alice = Bearer(factory, HankoToken(key, HankoAlice));
        var bob = Bearer(factory, HankoToken(key, HankoBob));
        var created = await alice.PostAsJsonAsync("/api/v1/profiles",
            new { type = "Candidate", name = "Hanko profile", data = new { }, confirmed = false });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Contains("\"profiles\":1", await alice.GetStringAsync("/api/v1/overview"));
        Assert.Contains("\"profiles\":0", await bob.GetStringAsync("/api/v1/overview"));

        var guest = await GuestClient(factory);
        Assert.Equal(HttpStatusCode.OK, (await guest.GetAsync("/api/v1/overview")).StatusCode);

        var expired = Bearer(factory, HankoToken(key, HankoAlice, DateTime.UtcNow.AddMinutes(-5)));
        Assert.Equal(HttpStatusCode.Unauthorized, (await expired.GetAsync("/api/v1/overview")).StatusCode);

        var otherAudience = Bearer(factory, HankoToken(key, HankoAlice, audience: "someone-else.example.test"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await otherAudience.GetAsync("/api/v1/overview")).StatusCode);

        using var otherRsa = RSA.Create(2048);
        var forged = Bearer(factory, HankoToken(new RsaSecurityKey(otherRsa) { KeyId = "hanko-key-1" }, HankoAlice));
        Assert.Equal(HttpStatusCode.Unauthorized, (await forged.GetAsync("/api/v1/overview")).StatusCode);
    }

    [Fact]
    public async Task Hanko_alone_counts_as_accounts_so_guests_can_be_switched_off()
    {
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa) { KeyId = "hanko-key-1" };
        using var factory = WithHanko(key, new() { ["Auth:SupabaseUrl"] = "", ["Auth:AllowGuests"] = "false" });
        var anonymous = factory.CreateClient();

        var caps = JsonDocument.Parse(await anonymous.GetStringAsync("/api/v1/capabilities")).RootElement;
        Assert.Equal(["hanko"], caps.GetProperty("signInProviders").EnumerateArray().Select(e => e.GetString()));
        Assert.False(caps.GetProperty("guestSignIn").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.PostAsync("/api/v1/auth/guest", null)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await Bearer(factory, HankoToken(key, HankoAlice)).GetAsync("/api/v1/overview")).StatusCode);
    }
}
