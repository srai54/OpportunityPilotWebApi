using Microsoft.AspNetCore.Mvc;
using OpportunityPilot.Application.Interviews;

namespace OpportunityPilot.Api.Controllers;

/// <summary>Candidate workspace: interviews the person records for jobs they applied to.</summary>
[ApiController]
[Route("api/v1/interviews")]
public sealed class JobInterviewsController(JobInterviewService interviews) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<JobInterviewDto>>> List([FromQuery] Guid? opportunityId, CancellationToken ct) =>
        Ok(await interviews.ListAsync(opportunityId, ct));

    [HttpPost("/api/v1/opportunities/{opportunityId:guid}/interviews")]
    public async Task<ActionResult<JobInterviewDto>> Create(Guid opportunityId, SaveJobInterviewRequest request, CancellationToken ct) =>
        StatusCode(StatusCodes.Status201Created, await interviews.CreateAsync(opportunityId, request, ct));

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<JobInterviewDto>> Update(Guid id, SaveJobInterviewRequest request, CancellationToken ct) =>
        Ok(await interviews.UpdateAsync(id, request, ct));

    [HttpPost("{id:guid}/status")]
    public async Task<ActionResult<JobInterviewDto>> Status(Guid id, JobInterviewStatusRequest request, CancellationToken ct) =>
        Ok(await interviews.SetStatusAsync(id, request, ct));

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await interviews.DeleteAsync(id, ct);
        return NoContent();
    }
}
