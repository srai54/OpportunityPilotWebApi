using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpportunityPilot.Application.Staffing;

namespace OpportunityPilot.Api.Controllers;

/// <summary>The candidate's private progress page. No sign-in: the unguessable link is the key, and an unknown or revoked one is simply "not found".</summary>
[ApiController]
[AllowAnonymous]
[Route("api/v1/public")]
[Microsoft.AspNetCore.RateLimiting.EnableRateLimiting(Hosting.RateLimits.PublicProgress)]
public sealed class PublicProgressController(ProgressViewService view, ResumeService resumes) : ControllerBase
{
    [HttpGet("progress/{token}")]
    public async Task<ActionResult<ProgressViewDto>> Progress(string token, CancellationToken ct) => Ok(await view.ViewAsync(token, ct));

    /// <summary>The candidate answers a role the recruiter suggested: Interested or NotForMe.</summary>
    /// <summary>Resume builder: the roles the candidate may write for, with the job description and their saved resume.</summary>
    [HttpGet("progress/{token}/resumes")]
    public async Task<ActionResult<ResumeWorkspaceDto>> Resumes(string token, CancellationToken ct) => Ok(await resumes.WorkspaceAsync(token, ct));

    [HttpPut("progress/{token}/resumes/{dealId:guid}")]
    public async Task<ActionResult<ResumeDto>> SaveResume(string token, Guid dealId, SaveResumeRequest request, CancellationToken ct) => Ok(await resumes.SaveAsync(token, dealId, request, ct));

    [HttpPost("progress/{token}/resumes/{dealId:guid}/submit")]
    public async Task<ActionResult<ResumeDto>> SubmitResume(string token, Guid dealId, CancellationToken ct) => Ok(await resumes.SubmitAsync(token, dealId, ct));

    [HttpPost("progress/{token}/roles/{id:guid}")]
    public async Task<ActionResult<ProgressRoleDto>> AnswerRole(string token, Guid id, RoleAnswerRequest request, CancellationToken ct) => Ok(await view.AnswerRoleAsync(token, id, request, ct));
}
