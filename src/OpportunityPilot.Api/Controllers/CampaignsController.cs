using Microsoft.AspNetCore.Mvc;
using OpportunityPilot.Application.Campaigns;

namespace OpportunityPilot.Api.Controllers;

[ApiController]
[Route("api/v1/campaigns")]
public sealed class CampaignsController(ICampaignService campaigns) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<CampaignSummaryDto>>> List(CancellationToken ct) =>
        Ok(await campaigns.ListAsync(ct));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<CampaignDto>> Get(Guid id, CancellationToken ct) =>
        await campaigns.GetAsync(id, ct);

    /// <summary>Any <c>OpportunityMode</c>; an unknown mode returns 400.</summary>
    [HttpPost]
    public async Task<ActionResult<CampaignDto>> Create(CreateCampaignRequest request, CancellationToken ct)
    {
        var created = await campaigns.CreateAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<CampaignDto>> Update(Guid id, UpdateCampaignRequest request, CancellationToken ct) =>
        await campaigns.UpdateAsync(id, request, ct);

    /// <summary>Deletes the campaign with its leads, drafts and schedules. 404 when it is not the caller's.</summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct) { await campaigns.DeleteAsync(id, ct); return NoContent(); }
}
