using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpportunityPilot.Application.Sales;
using OpportunityPilot.Domain.Sales;

namespace OpportunityPilot.Api.Controllers;

[ApiController]
[Route("api/v1/sales")]
public sealed class SalesController(SalesService sales, LeadSearchService leads) : ControllerBase
{
    /// <summary>Companies or people from Apollo (RapidAPI) by keyword, country and company size. Paid per result: signed-in only, capped per day.</summary>
    [Authorize(Policy = OpportunityPilot.Api.Auth.Policies.NotGuest)]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting(OpportunityPilot.Api.Hosting.RateLimits.LeadSearch)]
    [HttpPost("lead-search")]
    public async Task<ActionResult<LeadSearchResultDto>> LeadSearch(LeadSearchRequest request, CancellationToken ct) =>
        Ok(await leads.SearchAsync(request, ct));

    [HttpGet("projects")]
    public async Task<ActionResult<IReadOnlyList<SalesProjectDto>>> List(
        [FromQuery] int take = 50, [FromQuery] int skip = 0, [FromQuery] SalesProjectState? state = null,
        [FromQuery] SalesProjectSource? source = null, CancellationToken ct = default) =>
        Ok(await sales.ListProjectsAsync(take, skip, state, source, ct));

    [HttpPost("projects")]
    public async Task<ActionResult<SalesProjectDto>> Create(CreateSalesProjectRequest request, CancellationToken ct)
    {
        var created = await sales.CreateProjectAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpGet("projects/{id:guid}")]
    public async Task<ActionResult<SalesProjectDto>> Get(Guid id, CancellationToken ct) => Ok(await sales.GetProjectAsync(id, ct));

    [HttpPost("projects/{id:guid}/bid")]
    public async Task<ActionResult<SalesProjectDto>> CreateBid(Guid id, CreateSalesBidRequest request, CancellationToken ct) =>
        Ok(await sales.CreateBidAsync(id, request, ct));

    [HttpPut("bids/{id:guid}")]
    public async Task<ActionResult<SalesProjectDto>> UpdateBid(Guid id, UpdateSalesBidRequest request, CancellationToken ct) =>
        Ok(await sales.UpdateBidAsync(id, request, ct));

    [HttpPost("bids/{id:guid}/approve")]
    public async Task<ActionResult<SalesProjectDto>> ApproveBid(Guid id, ApproveSalesBidRequest request, CancellationToken ct) =>
        Ok(await sales.ApproveBidAsync(id, request, ct));

    [HttpPost("bids/batch-approve")]
    public async Task<ActionResult<IReadOnlyList<BatchApproveSalesBidResult>>> BatchApprove(BatchApproveSalesBidsRequest request, CancellationToken ct) =>
        Ok(await sales.BatchApproveBidsAsync(request, ct));

    [HttpPost("bids/{id:guid}/handoff")]
    public async Task<ActionResult<SalesProjectDto>> Handoff(Guid id, CancellationToken ct) => Ok(await sales.HandoffAsync(id, ct));

    [HttpPost("bids/{id:guid}/confirm-placement")]
    public async Task<ActionResult<SalesProjectDto>> ConfirmPlacement(Guid id, ConfirmSalesBidPlacementRequest request, CancellationToken ct) =>
        Ok(await sales.ConfirmPlacementAsync(id, request, ct));
}
