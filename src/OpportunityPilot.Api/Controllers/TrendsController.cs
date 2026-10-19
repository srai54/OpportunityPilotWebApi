using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpportunityPilot.Application.Common;
using OpportunityPilot.Application.JobBoards;
using OpportunityPilot.Application.Trends;

namespace OpportunityPilot.Api.Controllers;

/// <summary>Hiring trends: what a sample of the newest postings for a role and place says. Read-only; nothing is stored.</summary>
[ApiController]
[Route("api/v1/trends")]
[Microsoft.AspNetCore.RateLimiting.EnableRateLimiting(Hosting.RateLimits.JobBoardSearch)]
public sealed class TrendsController(TrendService trends) : ControllerBase
{
    [Authorize(Policy = OpportunityPilot.Api.Auth.Policies.NotGuest)]
    [HttpGet]
    public async Task<ActionResult<TrendReport>> Get(
        [FromQuery] string query, [FromQuery] string? location = null, [FromQuery] string datePosted = "month",
        [FromQuery] string? country = null, [FromQuery] int pages = 1, CancellationToken ct = default)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(query) || query.Length > 200) errors["query"] = ["Enter a role or skill of 1–200 characters."];
        if (location is { Length: > 100 }) errors["location"] = ["Location is at most 100 characters."];
        if (!JsearchBoardParser.DatePostedValues.Contains(datePosted)) errors["datePosted"] = ["Use all, today, 3days, week or month."];
        if (country is not null && (country.Length != 2 || !country.All(char.IsAsciiLetter))) errors["country"] = ["Use a two-letter country code."];
        if (pages is < 1 or > TrendService.MaxPages) errors["pages"] = [$"Pages is 1–{TrendService.MaxPages}."];
        if (errors.Count > 0) throw new RequestValidationException(errors);
        return Ok(await trends.ReportAsync(query.Trim(), location?.Trim(), datePosted, country, pages, ct));
    }
}
