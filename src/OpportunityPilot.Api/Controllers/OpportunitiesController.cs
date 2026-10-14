using System.Text;
using Microsoft.AspNetCore.Mvc;
using OpportunityPilot.Application.Opportunities;
using OpportunityPilot.Domain.Opportunities;

namespace OpportunityPilot.Api.Controllers;

[ApiController]
[Route("api/v1/opportunities")]
public sealed class OpportunitiesController(OpportunityService opportunities, ContactFinder contacts, PeopleFinder people) : ControllerBase
{
    /// <summary>Suggests contact details that the posting or the company's own contact pages publish. Reads at most three public pages; nothing is stored.</summary>
    [HttpPost("{id:guid}/contacts/find")]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting(Hosting.RateLimits.JobBoardSearch)]
    public async Task<ActionResult<FindContactsResult>> FindContacts(Guid id, FindContactsRequest? request, CancellationToken ct) =>
        await contacts.FindAsync(id, request, ct);

    /// <summary>Looks up recruiters and decision makers at the company with the person's own Hunter or Apollo key. Third-party data; nothing is stored but an activity line.</summary>
    [HttpPost("{id:guid}/people/find")]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting(Hosting.RateLimits.Connectors)]
    public async Task<ActionResult<FindPeopleResult>> FindPeople(Guid id, FindPeopleRequest request, CancellationToken ct) =>
        await people.FindAsync(id, request, ct);

    /// <summary>Apollo only: reveals one person's work email. This uses the person's Apollo credits.</summary>
    [HttpPost("{id:guid}/people/reveal")]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting(Hosting.RateLimits.Connectors)]
    public async Task<ActionResult<RevealPersonResult>> RevealPerson(Guid id, RevealPersonRequest request, CancellationToken ct) =>
        await people.RevealAsync(id, request, ct);

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
