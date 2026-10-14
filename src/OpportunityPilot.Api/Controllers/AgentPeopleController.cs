using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpportunityPilot.Api.Auth;
using OpportunityPilot.Application.Circle;
using OpportunityPilot.Application.Outreach;

namespace OpportunityPilot.Api.Controllers;

/// <summary>
/// The desktop agent's people endpoints (agent key with the People scope): map the JD circle, and send approved LinkedIn notes from
/// the person's own browser through the provider gateway. The API itself never contacts LinkedIn.
/// </summary>
[ApiController]
[Route("api/v1/agent")]
[Authorize(Policy = AgentKeyAuthenticationHandler.PeoplePolicy)]
public sealed class AgentPeopleController(CircleService circle, LinkedInOutboxService outbox) : ControllerBase
{
    /// <summary>Leads the person asked the agent to map, oldest request first (max 20).</summary>
    [HttpGet("circle-requests")]
    public async Task<ActionResult<IReadOnlyList<AgentCircleRequestDto>>> CircleRequests(CancellationToken ct) =>
        Ok(await circle.PendingForAgentAsync(ct));

    [HttpPost("circle")]
    [RequestSizeLimit(512 * 1024)]
    public async Task<ActionResult<AgentCircleResult>> Circle(AgentCircleRequest request, CancellationToken ct) =>
        await circle.DeliverAsync(request, ct);

    [HttpGet("linkedin-outbox")]
    public async Task<ActionResult<IReadOnlyList<LinkedInOutboxItem>>> Outbox(CancellationToken ct) =>
        Ok(await outbox.ListAsync(ct));

    [HttpPost("linkedin-outbox/{draftId:guid}/start")]
    public async Task<ActionResult<ProviderExecutionDto>> Start(Guid draftId, StartExecutionRequest request, CancellationToken ct) =>
        await outbox.StartAsync(draftId, request, ct);

    [HttpPost("linkedin-outbox/{draftId:guid}/confirm")]
    public async Task<ActionResult<ProviderExecutionDto>> Confirm(Guid draftId, AgentLinkedInConfirmRequest request, CancellationToken ct) =>
        await outbox.ConfirmAsync(draftId, request, ct);

    [HttpPost("linkedin-outbox/{draftId:guid}/fail")]
    public async Task<ActionResult<ProviderExecutionDto>> Fail(Guid draftId, FailExecutionRequest request, CancellationToken ct) =>
        await outbox.FailAsync(draftId, request, ct);
}
