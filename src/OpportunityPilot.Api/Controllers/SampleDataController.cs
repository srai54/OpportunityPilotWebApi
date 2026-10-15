using Microsoft.AspNetCore.Mvc;
using OpportunityPilot.Application.SampleData;

namespace OpportunityPilot.Api.Controllers;

/// <summary>Demo records for the caller's own account, created once; a repeat call adds only what is still missing.</summary>
[ApiController]
[Route("api/v1/sample-data")]
public sealed class SampleDataController(SampleDataService sampleData) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<SampleDataResultDto>> Create([FromQuery] string? workspace, CancellationToken ct) =>
        Ok(await sampleData.CreateAsync(new SampleDataRequest(workspace), ct));
}
