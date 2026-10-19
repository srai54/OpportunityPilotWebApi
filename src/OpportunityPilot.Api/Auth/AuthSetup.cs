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
        string? supabaseProblem = null;
        if (supabaseConfigured && !baseUrl!.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            supabaseConfigured = false;
            supabaseProblem = "Auth__SupabaseUrl must be an https URL, so sign-in is guest-only.";
        }

        var hanko = HankoAuth.Read(auth, out var hankoProblem);
        var accounts = supabaseConfigured || hanko is not null;
        if (!devBypass)
        {
            if (!accounts) setup.AuthMissing(supabaseProblem ?? "Auth__SupabaseUrl is not set, so sign-in is guest-only.");
            else if (supabaseProblem is not null) setup.ConfigProblem("Auth__SupabaseUrl must be an https URL, so Supabase sign-in is off.");
            if (hankoProblem is not null) setup.ConfigProblem(hankoProblem);
        }
        var providers = new List<string>();
        if (hanko is not null) providers.Add("hanko");
        if (supabaseConfigured) providers.Add("supabase");
        setup.SignInProviders = providers;

        // Guests use opaque, hashed database sessions: always in demo mode, and next to real accounts unless
        // Auth:AllowGuests is false. Never with the local dev bypass.
        var guests = new GuestAccess(Enabled: (!accounts || auth.AllowGuests) && !devBypass);
        setup.GuestsEnabled = guests.Enabled;
        builder.Services.AddSingleton(guests);

        var schemes = new List<string>();
        var authBuilder = builder.Services.AddAuthentication(
            supabaseConfigured ? JwtBearerDefaults.AuthenticationScheme
            : hanko is not null ? HankoAuth.SchemeName
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
                            if (!Guid.TryParse(subject, out _)) { context.Fail("The token subject is invalid."); return Task.CompletedTask; }
                            // Two-factor is enforced on the server, not only in the browser: a Supabase session that has not passed its second
                            // factor (aal1) is refused. Hanko tokens are validated by their own scheme below.
                            if (!SupabaseAal.IsTwoFactor(context.Principal?.FindFirst("aal")?.Value))
                                context.Fail("Two-factor sign-in is required.");
                            return Task.CompletedTask;
                        }
                    };
                });
            schemes.Add(JwtBearerDefaults.AuthenticationScheme);
        }

        if (hanko is not null)
        {
            // A second, independent bearer scheme. Supabase validation above is unchanged; a Hanko token can only pass
            // here, with the Hanko project's keys, and becomes a namespaced owner id (HankoAuth.OwnerIdFor).
            var supabaseIssuer = supabaseConfigured ? $"{baseUrl}/auth/v1" : null;
            builder.Services.AddSingleton(sp => new HankoJwks(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(HankoJwks)),
                hanko.JwksUrl,
                sp.GetRequiredService<ILogger<HankoJwks>>()));
            builder.Services.AddHttpClient(nameof(HankoJwks), c => c.Timeout = TimeSpan.FromSeconds(10));

            authBuilder.AddJwtBearer(HankoAuth.SchemeName, _ => { });
            builder.Services.AddOptions<JwtBearerOptions>(HankoAuth.SchemeName)
                .Configure<HankoJwks>((o, jwks) =>
                {
                    o.MapInboundClaims = false;
                    o.TokenValidationParameters = HankoAuth.ValidationParameters(hanko, jwks);
                    o.Events = new JwtBearerEvents
                    {
                        OnMessageReceived = context =>
                        {
                            var header = context.Request.Headers.Authorization.ToString();
                            var token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                                ? header["Bearer ".Length..].Trim()
                                : null;
                            if (HankoAuth.ShouldTry(token, supabaseIssuer)) context.Token = token;
                            else context.NoResult();
                            return Task.CompletedTask;
                        },
                        OnTokenValidated = context =>
                        {
                            var principal = HankoAuth.ToOwnerPrincipal(context.Principal);
                            if (principal is null) context.Fail("The token subject is invalid.");
                            else context.Principal = principal;
                            return Task.CompletedTask;
                        }
                    };
                });
            schemes.Add(HankoAuth.SchemeName);
        }

        if (devBypass)
        {
            authBuilder.AddScheme<AuthenticationSchemeOptions, DevBypassAuthenticationHandler>(DevBypassAuthenticationHandler.SchemeName, null);
            schemes.Add(DevBypassAuthenticationHandler.SchemeName);
        }

        // Registered in every mode but deliberately left out of `schemes`: an agent key must never satisfy the
        // fallback policy, only endpoints marked [Authorize(AuthenticationSchemes = AgentKey)].
        authBuilder.AddScheme<AuthenticationSchemeOptions, AgentKeyAuthenticationHandler>(AgentKeyAuthenticationHandler.SchemeName, null);

        // The rate limiter runs before authorization, when only the default scheme has authenticated. These are the user schemes it
        // tries so Hanko and guest callers get their own bucket instead of sharing their IP's (see UseRateLimitIdentity).
        builder.Services.AddSingleton(new UserSchemes(schemes.ToArray()));

        var authz = builder.Services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder(schemes.ToArray()).RequireAuthenticatedUser().Build());
        // Paid features (AI, voice, live job boards, sending, connectors) need a signed-in account; a guest session can browse and use
        // the free parts. D1, 2026-10-16.
        authz.AddPolicy(Policies.NotGuest, p => p.AddAuthenticationSchemes(schemes.ToArray()).RequireAuthenticatedUser()
            .RequireAssertion(c => !c.User.HasClaim("guest", "true")));
        builder.Services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationMiddlewareResultHandler, SignInRequiredResultHandler>();
        foreach (var scope in new[] { "Research", "Shortlist", "Applications", "People" })
            authz.AddPolicy($"agent:{scope}", p => p.AddAuthenticationSchemes(AgentKeyAuthenticationHandler.SchemeName)
                .RequireAuthenticatedUser().RequireClaim(AgentKeyAuthenticationHandler.ScopeClaim, scope));

        builder.Services.AddHttpClient(nameof(SupabaseJwks), c => c.Timeout = TimeSpan.FromSeconds(10));
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<ICurrentUser, CurrentUser>();
    }
}

/// <summary>Supabase's authenticator assurance level: only "aal2" (password or link plus a second factor) is accepted.</summary>
public static class SupabaseAal
{
    public static bool IsTwoFactor(string? aal) => string.Equals(aal, "aal2", StringComparison.Ordinal);
}

/// <summary>The sign-in schemes a person (not an agent key) can use, in the fallback policy's order.</summary>
public sealed record UserSchemes(IReadOnlyList<string> Names);

public static class RateLimitIdentity
{
    /// <summary>
    /// Before the rate limiter: when the default scheme found no user, try the other user schemes (Hanko, guest…) so the limiter
    /// partitions by the signed-in owner, not by IP. Authorization still re-authenticates with its own policy schemes and replaces
    /// the user, so this only changes which bucket a request counts against, never what it may access. Handlers cache their result
    /// per request, so nothing is validated twice.
    /// </summary>
    public static IApplicationBuilder UseRateLimitIdentity(this IApplicationBuilder app) => app.Use(async (ctx, next) =>
    {
        if (ctx.User.Identity?.IsAuthenticated != true)
            foreach (var scheme in ctx.RequestServices.GetRequiredService<UserSchemes>().Names)
            {
                var result = await ctx.AuthenticateAsync(scheme);
                if (result.Succeeded && result.Principal is not null) { ctx.User = result.Principal; break; }
            }
        await next(ctx);
    });
}

public static class Policies
{
    /// <summary>Signed-in users only: guests are refused with "Sign in to use this".</summary>
    public const string NotGuest = "not-guest";
}

/// <summary>A guest refused by <see cref="Policies.NotGuest"/> gets a plain 403 problem the app shows as "Sign in to use this".</summary>
public sealed class SignInRequiredResultHandler : Microsoft.AspNetCore.Authorization.IAuthorizationMiddlewareResultHandler
{
    private readonly Microsoft.AspNetCore.Authorization.Policy.AuthorizationMiddlewareResultHandler fallback = new();

    public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, Microsoft.AspNetCore.Authorization.Policy.PolicyAuthorizationResult result)
    {
        if (result.Forbidden && context.User.HasClaim("guest", "true"))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new
            {
                type = "https://tools.ietf.org/html/rfc9110#section-15.5.4",
                title = "Sign in to use this",
                status = 403,
                detail = "Voice, AI, live job boards and sending need a signed-in account. Guest mode covers everything else.",
                code = "sign-in-required",
            }, context.RequestAborted);
            return;
        }
        await fallback.HandleAsync(next, context, policy, result);
    }
}
