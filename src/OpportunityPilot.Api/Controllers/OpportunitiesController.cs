using System.Text;
using Microsoft.AspNetCore.Mvc;
using OpportunityPilot.Application.Opportunities;
using OpportunityPilot.Domain.Opportunities;

namespace OpportunityPilot.Api.Controllers;

[ApiController]
[Route("api/v1/opportunities")]
public sealed class OpportunitiesController(OpportunityService opportunities) : ControllerBase
{
    /// <summary>sort = score (default), recent (last updated), found (newest first) or oldest (first found first); take 1–200 (default 50).</summary>
    [HttpGet("/api/v1/campaigns/{campaignId:guid}/opportunities")]
    public async Task<ActionResult<OpportunityPageDto>> List(
        Guid campaignId, [FromQuery] FilterOutcome? outcome, [FromQuery] OpportunityStatus? status, [FromQuery] string? sort,
        [FromQuery] int take = OpportunityService.DefaultTake, [FromQuery] int skip = 0, CancellationToken ct = default) =>
        await opportunities.ListAsync(campaignId, outcome, status, sort, take, skip, ct);

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<OpportunityDetailDto>> Get(Guid id, CancellationToken ct) =>
        await opportunities.GetAsync(id, ct);

    [HttpPatch("{id:guid}/status")]
    public async Task<ActionResult<OpportunityDetailDto>> UpdateStatus(Guid id, UpdateOpportunityStatusRequest request, CancellationToken ct) =>
        await opportunities.UpdateStatusAsync(id, request, ct);

    /// <summary>Spreadsheet-safe CSV: every cell quoted, formula-like values prefixed with an apostrophe.</summary>
    [HttpGet("/api/v1/campaigns/{campaignId:guid}/export")]
    public async Task<IActionResult> Export(Guid campaignId, CancellationToken ct)
    {
        var export = await opportunities.ExportAsync(campaignId, ct);
        return File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(export.Content)).ToArray(), "text/csv; charset=utf-8", export.FileName);
    }
}
