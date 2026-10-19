using Microsoft.AspNetCore.Mvc;
using OpportunityPilot.Application.Staffing;
using OpportunityPilot.Domain.Staffing;

namespace OpportunityPilot.Api.Controllers;

[ApiController]
[Route("api/v1/staffing")]
public sealed class StaffingController(IStaffingService staffing) : ControllerBase
{
    [HttpGet("accounts")]
    public async Task<ActionResult<IReadOnlyList<StaffingAccountDto>>> ListAccounts(
        [FromQuery] int take = 50, [FromQuery] int skip = 0, CancellationToken ct = default) =>
        Ok(await staffing.ListAccountsAsync(take, skip, ct));

    [HttpPost("accounts")]
    public async Task<ActionResult<StaffingAccountDto>> CreateAccount(CreateStaffingAccountRequest request, CancellationToken ct)
    {
        var created = await staffing.CreateAccountAsync(request, ct);
        return Created($"/api/v1/staffing/accounts/{created.Id}", created);
    }

    [HttpPost("accounts/{accountId:guid}/contacts")]
    public async Task<ActionResult<StaffingContactDto>> CreateContact(
        Guid accountId, CreateStaffingContactRequest request, CancellationToken ct) =>
        Ok(await staffing.CreateContactAsync(accountId, request, ct));

    [HttpPut("accounts/{accountId:guid}/contacts/{contactId:guid}")]
    public async Task<ActionResult<StaffingContactDto>> UpdateContact(
        Guid accountId, Guid contactId, UpdateStaffingContactRequest request, CancellationToken ct) =>
        Ok(await staffing.UpdateContactAsync(accountId, contactId, request, ct));

    [HttpGet("deals")]
    public async Task<ActionResult<IReadOnlyList<StaffingDealDto>>> ListDeals(
        [FromQuery] int take = 50, [FromQuery] int skip = 0, [FromQuery] StaffingDealStage? stage = null,
        [FromQuery] StaffingDealSource? source = null, CancellationToken ct = default) =>
        Ok(await staffing.ListDealsAsync(take, skip, stage, source, ct));

    [HttpGet("deals/{id:guid}")]
    public async Task<ActionResult<StaffingDealDto>> GetDeal(Guid id, CancellationToken ct) =>
        Ok(await staffing.GetDealAsync(id, ct));

    [HttpPost("accounts/{accountId:guid}/deals")]
    public async Task<ActionResult<StaffingDealDto>> CreateDeal(
        Guid accountId, CreateStaffingDealRequest request, CancellationToken ct)
    {
        var created = await staffing.CreateDealAsync(accountId, request, ct);
        return CreatedAtAction(nameof(GetDeal), new { id = created.Id }, created);
    }

    [HttpPut("deals/{id:guid}")]
    public async Task<ActionResult<StaffingDealDto>> UpdateDeal(
        Guid id, UpdateStaffingDealRequest request, CancellationToken ct) =>
        Ok(await staffing.UpdateDealAsync(id, request, ct));

    [HttpPost("deals/{id:guid}/stage")]
    public async Task<ActionResult<StaffingDealDto>> MoveDeal(
        Guid id, MoveStaffingDealRequest request, CancellationToken ct) =>
        Ok(await staffing.MoveDealAsync(id, request, ct));

    [HttpPost("from-opportunity/{opportunityId:guid}")]
    public async Task<ActionResult<PromotedLeadDto>> FromOpportunity(Guid opportunityId, CancellationToken ct) =>
        Ok(await staffing.PromoteOpportunityAsync(opportunityId, ct));

    [HttpPost("deals/{id:guid}/notes")]
    public async Task<ActionResult<StaffingDealDto>> AddNote(
        Guid id, AddStaffingDealNoteRequest request, CancellationToken ct) =>
        Ok(await staffing.AddNoteAsync(id, request, ct));
}
