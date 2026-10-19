using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpportunityPilot.Application.Ai;

namespace OpportunityPilot.Api.Controllers;

[ApiController]
[Route("api/v1")]
public sealed class AiController(AiService ai) : ControllerBase
{
    [Authorize(Policy = OpportunityPilot.Api.Auth.Policies.NotGuest)]
    [HttpPost("goal-previews")]
    public async Task<ActionResult<GoalPreviewDto>> Preview(GoalPreviewRequest request, CancellationToken ct) => await ai.PreviewGoalAsync(request, ct);

    [HttpGet("ai/status")]
    public async Task<ActionResult<AiStatusDto>> Status(CancellationToken ct) => await ai.StatusAsync(ct);
}
