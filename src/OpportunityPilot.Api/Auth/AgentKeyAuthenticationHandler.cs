using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using OpportunityPilot.Application.Agents;

namespace OpportunityPilot.Api.Auth;

/// <summary>
/// The local desktop agent authenticates with a personal key in the X-Agent-Key header. This scheme is not part
/// of the fallback policy, so a key only works on endpoints that explicitly allow the local-agent scheme.
/// </summary>
public sealed class AgentKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "AgentKey";
    public const string Header = "X-Agent-Key";
    public const string ScopeClaim = "agent_scope";

    /// <summary>Authorization policy names, one per scope: an agent endpoint names the one it needs.</summary>
    public const string ResearchPolicy = "agent:Research";
    public const string ShortlistPolicy = "agent:Shortlist";
    public const string ApplicationsPolicy = "agent:Applications";
    public const string PeoplePolicy = "agent:People";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var presented = Request.Headers[Header].ToString().Trim();
        if (string.IsNullOrEmpty(presented)) return AuthenticateResult.NoResult();

        var keys = Context.RequestServices.GetRequiredService<AgentKeyService>();
        var key = await keys.IdentifyAsync(presented, Context.RequestAborted);
        if (key is null) return AuthenticateResult.Fail("Agent key is unknown or revoked.");

        var claims = new List<Claim> { new("sub", key.OwnerId.ToString()), new("agent", "true") };
        claims.AddRange(AgentKeyService.ScopeNames(key.Scopes).Select(s => new Claim(ScopeClaim, s)));
        var identity = new ClaimsIdentity(claims, SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }
}
