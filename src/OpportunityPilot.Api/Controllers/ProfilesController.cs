using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpportunityPilot.Application.Profiles;

namespace OpportunityPilot.Api.Controllers;

[ApiController]
[Route("api/v1/profiles")]
public sealed class ProfilesController(IProfileService profiles, IProfileImproveService improve) : ControllerBase
{
    /// <summary>"Improve with AI": suggestions for the person's own fields (no new facts); nothing is saved.</summary>
    [Authorize(Policy = OpportunityPilot.Api.Auth.Policies.NotGuest)]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting(OpportunityPilot.Api.Hosting.RateLimits.AiWrites)]
    [HttpPost("improve")]
    public async Task<ActionResult<ImproveProfileDto>> Improve(ImproveProfileRequest request, CancellationToken ct) => await improve.ImproveAsync(request, ct);

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ProfileSummaryDto>>> List(CancellationToken ct) =>
        Ok(await profiles.ListAsync(ct));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ProfileDto>> Get(Guid id, CancellationToken ct) =>
        await profiles.GetAsync(id, ct);

    [HttpGet("{id:guid}/versions")]
    public async Task<ActionResult<IReadOnlyList<ProfileVersionDto>>> Versions(Guid id, CancellationToken ct) =>
        Ok(await profiles.VersionsAsync(id, ct));

    [HttpPost]
    public async Task<ActionResult<ProfileDto>> Create(CreateProfileRequest request, CancellationToken ct)
    {
        var created = await profiles.CreateAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<ProfileDto>> Update(Guid id, UpdateProfileRequest request, CancellationToken ct) =>
        await profiles.UpdateAsync(id, request, ct);
}
