using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpportunityPilot.Application.Staffing;

namespace OpportunityPilot.Api.Controllers;

/// <summary>The candidate's private progress page. No sign-in: the unguessable link is the key, and an unknown or revoked one is simply "not found".</summary>
[ApiController]
[AllowAnonymous]
[Route("api/v1/public")]
[Microsoft.AspNetCore.RateLimiting.EnableRateLimiting(Hosting.RateLimits.PublicProgress)]
public sealed class PublicProgressController(ProgressViewService view) : ControllerBase
{
    [HttpGet("progress/{token}")]
    public async Task<ActionResult<ProgressViewDto>> Progress(string token, CancellationToken ct) => Ok(await view.ViewAsync(token, ct));

    /// <summary>The candidate answers a role the recruiter suggested: Interested or NotForMe.</summary>
    [HttpPost("progress/{token}/roles/{id:guid}")]
    public async Task<ActionResult<ProgressRoleDto>> AnswerRole(string token, Guid id, RoleAnswerRequest request, CancellationToken ct) => Ok(await view.AnswerRoleAsync(token, id, request, ct));
}
