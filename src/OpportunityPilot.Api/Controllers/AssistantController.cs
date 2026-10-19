using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpportunityPilot.Application.Assistant;

namespace OpportunityPilot.Api.Controllers;

/// <summary>The voice assistant: an answer about the person's own numbers and at most one proposed action, confirmed in the app.</summary>
[Authorize(Policy = OpportunityPilot.Api.Auth.Policies.NotGuest)]
[ApiController]
[Route("api/v1/assistant")]
public sealed class AssistantController(IAssistantService assistant) : ControllerBase
{
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting(OpportunityPilot.Api.Hosting.RateLimits.Assistant)]
    [HttpPost]
    public async Task<ActionResult<AssistantReplyDto>> Ask(AssistantRequest request, CancellationToken ct) => await assistant.AskAsync(request, ct);
}
