using Microsoft.AspNetCore.Mvc;
using OpportunityPilot.Application.Sources;

namespace OpportunityPilot.Api.Controllers;

[ApiController]
[Route("api/v1/campaigns/{campaignId:guid}/sources")]
public sealed class SourcesController(ISourceService sources) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<SourceDto>>> List(Guid campaignId, CancellationToken ct) =>
        Ok(await sources.ListAsync(campaignId, ct));

    /// <summary>Paste, Url or Feed. Pasted text is at most 50 000 characters.</summary>
    [HttpPost]
    [RequestSizeLimit(256 * 1024)]
    public async Task<ActionResult<SourceDto>> Create(Guid campaignId, CreateSourceRequest request, CancellationToken ct) =>
        StatusCode(StatusCodes.Status201Created, await sources.CreateAsync(campaignId, request, ct));

    [HttpDelete("{sourceId:guid}")]
    public async Task<IActionResult> Delete(Guid campaignId, Guid sourceId, CancellationToken ct)
    {
        await sources.DeleteAsync(campaignId, sourceId, ct);
        return NoContent();
    }
}
