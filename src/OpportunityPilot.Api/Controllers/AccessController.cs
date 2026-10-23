using Microsoft.AspNetCore.Mvc;
using OpportunityPilot.Application.Access;

namespace OpportunityPilot.Api.Controllers;

/// <summary>Role-based access (2026-10-22): the caller's own role and, for the admin, every user's role.</summary>
[ApiController]
[Route("api/v1")]
public sealed class AccessController(AccessService access) : ControllerBase
{
    /// <summary>The caller's role (Candidate, Sales or Both), whether it was chosen, and whether the caller is the admin.</summary>
    /// <param name="label">Optional: the email the app shows, kept only as a label for the admin when the sign-in token has none.</param>
    [HttpGet("me")]
    public async Task<ActionResult<MeDto>> Me([FromQuery] string? label, CancellationToken ct) => Ok(await access.MeAsync(ct, label));

    /// <summary>The sign-up choice; only once (409 afterwards: the admin changes it).</summary>
    [HttpPut("me/role")]
    public async Task<ActionResult<MeDto>> ChooseRole(RoleRequest request, CancellationToken ct) => Ok(await access.ChooseAsync(request, ct));

    [HttpGet("admin/users")]
    public async Task<ActionResult<IReadOnlyList<AccessUserDto>>> Users(CancellationToken ct) =>
        access.IsAdmin() ? Ok(await access.UsersAsync(ct)) : AdminOnly();

    [HttpPut("admin/users/{accountId:guid}/role")]
    public async Task<ActionResult<AccessUserDto>> SetRole(Guid accountId, RoleRequest request, CancellationToken ct) =>
        access.IsAdmin() ? Ok(await access.SetRoleAsync(accountId, request, ct)) : AdminOnly();

    private ObjectResult AdminOnly() => Problem(statusCode: StatusCodes.Status403Forbidden, title: "Only the admin can do this.");
}
