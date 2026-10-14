using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using OpportunityPilot.Application.Abstractions;
using OpportunityPilot.Application.Configuration;

namespace OpportunityPilot.Api.Auth;

public static class AuthSetup
{
    private static readonly string[] ModernSupabaseAlgorithms =
        [SecurityAlgorithms.RsaSha256, SecurityAlgorithms.EcdsaSha256, "EdDSA"];

    public static void AddOpportunityPilotAuth(this WebApplicationBuilder builder, SetupState setup)
    {
        var auth = builder.Configuration.GetSection(AuthOptions.Section).Get<AuthOptions>() ?? new AuthOptions();
        var devBypass = auth.DevBypass;

        // A security guard, not a setup gap: this must stop the process.
        if (devBypass && !builder.Environment.IsDevelopment())
            throw new InvalidOperationException("Auth:DevBypass is only allowed in the Development environment.");

        var baseUrl = auth.SupabaseUrl?.Trim().TrimEnd('/');
        var supabaseConfigured = !string.IsNullOrEmpty(baseUrl);
        if (supabaseConfigured && !baseUrl!.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            supabaseConfigured = false;
            if (!devBypass) setup.AuthMissing("Auth__SupabaseUrl must be an https URL, so sign-in is guest-only.");
        }
        else if (!supabaseConfigured && !devBypass)
        {
            setup.AuthMissing("Auth__SupabaseUrl is not set, so sign-in is guest-only.");
        }

        // Guests use opaque, hashed database sessions: always in demo mode, and next to real accounts unless
        // Auth:AllowGuests is false. Never with the local dev bypass.
        var guests = new GuestAccess(Enabled: (!supabaseConfigured || auth.AllowGuests) && !devBypass);
        setup.GuestsEnabled = guests.Enabled;
        builder.Services.AddSingleton(guests);

        var schemes = new List<string>();
        var authBuilder = builder.Services.AddAuthentication(
            supabaseConfigured ? JwtBearerDefaults.AuthenticationScheme
            : devBypass ? DevBypassAuthenticationHandler.SchemeName
            : GuestAuthenticationHandler.SchemeName);

        if (guests.Enabled)
        {
            authBuilder.AddScheme<AuthenticationSchemeOptions, GuestAuthenticationHandler>(
                GuestAuthenticationHandler.SchemeName, null);
            schemes.Add(GuestAuthenticationHandler.SchemeName);
        }

        if (supabaseConfigured)
        {

            builder.Services.AddSingleton(sp => new SupabaseJwks(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(SupabaseJwks)),
                $"{baseUrl}/auth/v1/.well-known/jwks.json",
                sp.GetRequiredService<ILogger<SupabaseJwks>>()));

            authBuilder.AddJwtBearer();
            builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
                .Configure<SupabaseJwks>((o, jwks) =>
                {
                    o.MapInboundClaims = false;
                    o.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidIssuer = $"{baseUrl}/auth/v1",
                        ValidAudience = auth.Audience,
                        ValidateIssuer = true,
                        ValidateAudience = true,
                        ValidateLifetime = true,
                        ValidateIssuerSigningKey = true,
                        RequireSignedTokens = true,
                        RequireExpirationTime = true,
                        ClockSkew = TimeSpan.FromMinutes(1),
                        ValidAlgorithms = string.IsNullOrWhiteSpace(auth.LegacyJwtSecret)
                            ? ModernSupabaseAlgorithms
                            : [.. ModernSupabaseAlgorithms, SecurityAlgorithms.HmacSha256],
                        IssuerSigningKeyResolver = (_, token, kid, _) =>
                        {
                            var algorithm = (token as JsonWebToken)?.Alg;
                            if (!string.Equals(algorithm, SecurityAlgorithms.HmacSha256, StringComparison.Ordinal))
                                return jwks.GetKeys(kid);

                            return string.IsNullOrWhiteSpace(auth.LegacyJwtSecret)
                                ? []
                                : [new SymmetricSecurityKey(Encoding.UTF8.GetBytes(auth.LegacyJwtSecret))];
                        }
                    };
                    o.Events = new JwtBearerEvents
                    {
                        OnTokenValidated = context =>
                        {
                            var subject = context.Principal?.FindFirst("sub")?.Value;
                            if (!Guid.TryParse(subject, out _)) context.Fail("The token subject is invalid.");
                            return Task.CompletedTask;
                        }
                    };
                });
            schemes.Add(JwtBearerDefaults.AuthenticationScheme);
        }

        if (devBypass)
        {
            authBuilder.AddScheme<AuthenticationSchemeOptions, DevBypassAuthenticationHandler>(DevBypassAuthenticationHandler.SchemeName, null);
            schemes.Add(DevBypassAuthenticationHandler.SchemeName);
        }

        // Registered in every mode but deliberately left out of `schemes`: an agent key must never satisfy the
        // fallback policy, only endpoints marked [Authorize(AuthenticationSchemes = AgentKey)].
        authBuilder.AddScheme<AuthenticationSchemeOptions, AgentKeyAuthenticationHandler>(AgentKeyAuthenticationHandler.SchemeName, null);

        var authz = builder.Services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder(schemes.ToArray()).RequireAuthenticatedUser().Build());
        foreach (var scope in new[] { "Research", "Shortlist", "Applications", "People" })
            authz.AddPolicy($"agent:{scope}", p => p.AddAuthenticationSchemes(AgentKeyAuthenticationHandler.SchemeName)
                .RequireAuthenticatedUser().RequireClaim(AgentKeyAuthenticationHandler.ScopeClaim, scope));

        builder.Services.AddHttpClient(nameof(SupabaseJwks), c => c.Timeout = TimeSpan.FromSeconds(10));
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<ICurrentUser, CurrentUser>();
    }
}
