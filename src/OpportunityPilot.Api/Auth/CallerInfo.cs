using OpportunityPilot.Application.Access;

namespace OpportunityPilot.Api.Auth;

/// <summary>The caller's email and guest flag, read only from the validated token's claims (never from the request body).</summary>
public sealed class CallerInfo(IHttpContextAccessor accessor) : ICallerInfo
{
    public string? Email
    {
        get
        {
            var email = accessor.HttpContext?.User.FindFirst("email")?.Value?.Trim();
            return string.IsNullOrEmpty(email) || !email.Contains('@') ? null : email;
        }
    }

    public bool IsGuest => accessor.HttpContext?.User.HasClaim("guest", "true") ?? false;
}
