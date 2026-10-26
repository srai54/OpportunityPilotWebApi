using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using OpportunityPilot.Application.Ai;

namespace OpportunityPilot.Api.Controllers;

/// <summary>
/// Bring your own Claude key (2026-10-26): connect, change the model or sharing, and remove. The key is write-only: no endpoint returns it,
/// only its last four characters. Guests cannot store a key.
/// </summary>
[ApiController]
[Route("api/v1/ai/claude")]
[Authorize(Policy = Auth.Policies.NotGuest)]
public sealed class ClaudeController(ClaudeKeyService keys) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ClaudeKeyStatusDto>> Status(CancellationToken ct) => Ok(await keys.StatusAsync(ct));

    /// <summary>Tests the key with a free call to Anthropic, then stores it encrypted. 400 when Anthropic refuses it.</summary>
    [HttpPut]
    [EnableRateLimiting(Hosting.RateLimits.ClaudeConnect)]
    public async Task<ActionResult<ClaudeKeyStatusDto>> Connect(ConnectClaudeRequest request, CancellationToken ct) => Ok(await keys.ConnectAsync(request, ct));

    [HttpPatch]
    public async Task<ActionResult<ClaudeKeyStatusDto>> Update(ClaudeSettingsRequest request, CancellationToken ct) => Ok(await keys.UpdateAsync(request, ct));

    [HttpDelete]
    public async Task<IActionResult> Remove(CancellationToken ct)
    {
        await keys.RemoveAsync(ct);
        return NoContent();
    }
}
