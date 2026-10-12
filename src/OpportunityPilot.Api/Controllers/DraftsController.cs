using Microsoft.AspNetCore.Mvc;
using OpportunityPilot.Application.Drafts;

namespace OpportunityPilot.Api.Controllers;

[ApiController]
[Route("api/v1")]
public sealed class DraftsController(DraftService drafts) : ControllerBase
{
    [HttpPost("opportunities/{opportunityId:guid}/drafts")]
    public async Task<ActionResult<DraftDto>> Create(Guid opportunityId, CreateDraftRequest request, CancellationToken ct)
    {
        var created = await drafts.CreateAsync(opportunityId, request, ct);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpGet("opportunities/{opportunityId:guid}/drafts")]
    public async Task<ActionResult<IReadOnlyList<DraftDto>>> List(Guid opportunityId, CancellationToken ct) =>
        Ok(await drafts.ListForOpportunityAsync(opportunityId, ct));

    [HttpGet("drafts/{id:guid}")]
    public async Task<ActionResult<DraftDto>> Get(Guid id, CancellationToken ct) => await drafts.GetAsync(id, ct);

    [HttpGet("drafts")]
    public async Task<ActionResult<DraftPageDto>> ListAll([FromQuery] OpportunityPilot.Domain.Drafts.DraftState? state,
        [FromQuery] OpportunityPilot.Domain.Drafts.DraftChannel? channel, [FromQuery] Guid? campaignId,
        [FromQuery] int take = 50, [FromQuery] int skip = 0, CancellationToken ct = default) =>
        await drafts.ListAsync(state, channel, campaignId, take, skip, ct);

    [HttpPut("drafts/{id:guid}")]
    public async Task<ActionResult<DraftDto>> Update(Guid id, UpdateDraftRequest request, CancellationToken ct) =>
        await drafts.UpdateAsync(id, request, ct);

    [HttpPost("drafts/{id:guid}/approve")]
    public async Task<ActionResult<DraftDto>> Approve(Guid id, ApproveDraftRequest request, CancellationToken ct) =>
        await drafts.ApproveAsync(id, request, ct);

    [HttpPost("drafts/batch-approve")]
    public async Task<ActionResult<IReadOnlyList<BatchApproveDraftResult>>> BatchApprove(BatchApproveDraftRequest request, CancellationToken ct) =>
        Ok(await drafts.BatchApproveAsync(request, ct));

    [HttpPost("drafts/{id:guid}/revoke-approval")]
    public async Task<ActionResult<DraftDto>> Revoke(Guid id, CancellationToken ct) => await drafts.RevokeAsync(id, ct);

    /// <summary>Turns a sent draft into a new editable version ("edit and send again").</summary>
    [HttpPost("drafts/{id:guid}/reopen")]
    public async Task<ActionResult<DraftDto>> Reopen(Guid id, CancellationToken ct) => await drafts.ReopenAsync(id, ct);

    [HttpDelete("drafts/{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await drafts.DeleteAsync(id, ct);
        return NoContent();
    }
}
