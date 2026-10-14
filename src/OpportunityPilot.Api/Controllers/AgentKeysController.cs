using Microsoft.AspNetCore.Mvc;
using OpportunityPilot.Application.Agents;

namespace OpportunityPilot.Api.Controllers;

[ApiController]
[Route("api/v1/agent-keys")]
public sealed class AgentKeysController(AgentKeyService keys, AgentStatusService status) : ControllerBase
{
    /// <summary>INT6: is the desktop agent running, what it applied to lately, and how many approved jobs wait for it.</summary>
    [HttpGet("status")]
    public async Task<ActionResult<AgentStatusDto>> Status(CancellationToken ct) => await status.GetAsync(ct);

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<AgentKeyDto>>> List(CancellationToken ct) =>
        Ok(await keys.ListAsync(ct));

    /// <summary>The response is the only time the plaintext key is returned.</summary>
    [HttpPost]
    public async Task<ActionResult<CreatedAgentKeyDto>> Create(CreateAgentKeyRequest request, CancellationToken ct) =>
        StatusCode(StatusCodes.Status201Created, await keys.CreateAsync(request, ct));

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Revoke(Guid id, CancellationToken ct)
    {
        await keys.RevokeAsync(id, ct);
        return NoContent();
    }
}
