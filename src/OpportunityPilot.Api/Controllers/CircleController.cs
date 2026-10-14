using Microsoft.AspNetCore.Mvc;
using OpportunityPilot.Application.Circle;

namespace OpportunityPilot.Api.Controllers;

/// <summary>The people around a job or company lead (JD circle). The desktop agent fills it from LinkedIn; the person can edit it.</summary>
[ApiController]
[Route("api/v1/opportunities/{id:guid}/circle")]
public sealed class CircleController(CircleService circle) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<CircleDto>> Get(Guid id, CancellationToken ct) => await circle.GetAsync(id, ct);

    /// <summary>Asks the desktop agent to map this lead's circle on its next run. Idempotent while a request waits.</summary>
    [HttpPost("request")]
    public async Task<ActionResult<CircleDto>> Ask(Guid id, CancellationToken ct) => await circle.RequestAsync(id, ct);

    [HttpPost]
    public async Task<ActionResult<CirclePersonDto>> Add(Guid id, AddCirclePersonRequest request, CancellationToken ct) =>
        StatusCode(StatusCodes.Status201Created, await circle.AddAsync(id, request, ct));

    [HttpDelete("{personId:guid}")]
    public async Task<IActionResult> Delete(Guid id, Guid personId, CancellationToken ct)
    {
        await circle.DeleteAsync(id, personId, ct);
        return NoContent();
    }
}
