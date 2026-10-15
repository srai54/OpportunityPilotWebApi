using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpportunityPilot.Application.Abstractions;
using OpportunityPilot.Application.Common;
using OpportunityPilot.Application.Imports;
using OpportunityPilot.Application.Research.Rules;
using OpportunityPilot.Domain.Opportunities;
using OpportunityPilot.Domain.Outreach;

namespace OpportunityPilot.Application.Opportunities;

public sealed class OpportunityService(IOpportunityRepository opportunities, ICurrentUser user, TimeProvider clock) : IOpportunityService
{
    public const int DefaultTake = 50;
    public const int MaxTake = 200;
    public const int MaxExportRows = 5000;

    public async Task<OpportunityPageDto> ListAsync(
        Guid campaignId, FilterOutcome? outcome, OpportunityStatus? status, string? sort, int take, int skip, CancellationToken ct)
    {
        await EnsureCampaignAsync(campaignId, ct);
        take = Math.Clamp(take, 1, MaxTake);
        skip = Math.Max(0, skip);

        var (total, items) = await opportunities.PageAsync(user.OwnerId, campaignId, outcome, status, sort, take, skip, ct);
        return new OpportunityPageDto(total, items.Select(ToSummary).ToList());
    }

    /// <summary>Qualified opportunities nobody has decided on yet (status New): the review queue of campaigns that do not suggest for approval, such as Sales.</summary>
    public async Task<int> CountQualifiedNewAsync(CancellationToken ct) =>
        await opportunities.CountAsync(user.OwnerId, OpportunityStatus.New, FilterOutcome.Qualified, ct);

    public async Task<int> CountShortlistedAsync(CancellationToken ct) =>
        await opportunities.CountAsync(user.OwnerId, OpportunityStatus.Shortlisted, null, ct);

    public async Task<OpportunityDetailDto> GetAsync(Guid id, CancellationToken ct) =>
        await ToDetailAsync(await FindOwnedAsync(id, ct), ct);

    /// <summary>A user-chosen pipeline move (Applied here is the user's own confirmation). Records an activity when it changes.</summary>
    public async Task<OpportunityDetailDto> UpdateStatusAsync(Guid id, UpdateOpportunityStatusRequest request, CancellationToken ct)
    {
        if (request is null || !Enum.IsDefined(request.Status))
            throw new RequestValidationException(new Dictionary<string, string[]> { ["status"] = ["Unknown status."] });
        var opportunity = await FindOwnedAsync(id, ct);
        Activity? activity;
        try { activity = opportunity.ChangeStatus(request.Status, clock.GetUtcNow().UtcDateTime); }
        catch (InvalidOperationException ex)
        {
            throw new RequestValidationException(new Dictionary<string, string[]> { ["status"] = [ex.Message] });
        }
        if (activity is not null)
        {
            opportunities.AddActivity(activity);
            // Applying is only the start: remind the person to check back, unless a follow-up is already open for it.
            if (request.Status == OpportunityStatus.Applied
                && !await opportunities.HasOpenFollowUpAsync(user.OwnerId, opportunity.Id, ct))
            {
                var now = clock.GetUtcNow().UtcDateTime;
                opportunities.AddNextAction(new NextAction(user.OwnerId, opportunity.Id, NextActionKind.FollowUp,
                    "Check on this application (scheduled when you marked it applied).", now.AddDays(7), "UTC", now));
            }
            try
            {
                await opportunities.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                throw new ConflictException("The opportunity was updated at the same time (for example by research). Reload and try again.");
            }
        }
        return await ToDetailAsync(opportunity, ct);
    }

    /// <summary>
    /// CSV of the campaign's opportunities with evidence links and gaps. Every cell is quoted and formula-like values
    /// are neutralised, because the file is meant to be opened in a spreadsheet.
    /// </summary>
    public async Task<CsvExport> ExportAsync(Guid campaignId, CancellationToken ct)
    {
        var campaign = await opportunities.FindCampaignAsync(user.OwnerId, campaignId, ct)
            ?? throw new NotFoundException("Campaign not found.");
        var rows = await opportunities.ListForExportAsync(user.OwnerId, campaignId, MaxExportRows, ct);
        var evidenceUrls = await opportunities.EvidenceUrlsAsync(user.OwnerId, rows.Select(o => o.Id).ToList(), ct);

        var csv = new StringBuilder();
        csv.Append(Csv.Row(["Title", "Organization", "Location", "Url", "ApplyUrl", "Platform", "ExternalId", "Score", "Coverage",
            "Outcome", "OutcomeReason", "Status", "Gaps", "EvidenceUrls", "UpdatedAt"]));
        foreach (var o in rows)
        {
            csv.Append(Csv.Row([
                o.Title, o.Organization, o.Location, o.Url, o.ApplyUrl, o.Platform?.ToString(), o.ExternalId,
                o.Score.ToString(), o.Coverage.ToString(), o.Outcome.ToString(), o.OutcomeReason, o.Status.ToString(),
                string.Join("; ", Deserialize<List<string>>(o.GapsJson) ?? []),
                evidenceUrls.GetValueOrDefault(o.Id), o.UpdatedAt.ToString("O")
            ]));
        }
        return new CsvExport($"{FileSafe(campaign.Name)}-opportunities.csv", csv.ToString());
    }

    private async Task<Opportunity> FindOwnedAsync(Guid id, CancellationToken ct) =>
        await opportunities.FindAsync(user.OwnerId, id, ct)
        ?? throw new NotFoundException("Opportunity not found.");

    private async Task EnsureCampaignAsync(Guid campaignId, CancellationToken ct)
    {
        if (!await opportunities.CampaignExistsAsync(user.OwnerId, campaignId, ct))
            throw new NotFoundException("Campaign not found.");
    }

    private async Task<OpportunityDetailDto> ToDetailAsync(Opportunity o, CancellationToken ct)
    {
        var evidence = await opportunities.EvidenceAsync(user.OwnerId, o.Id, ct);
        var sourceIds = evidence.Select(e => e.SourceId).Distinct().ToList();
        var labels = await opportunities.SourceLabelsAsync(user.OwnerId, sourceIds, ct);
        var activities = await opportunities.ActivitiesAsync(user.OwnerId, o.Id, 100, ct);

        return new OpportunityDetailDto(o.Id, o.CampaignId, o.Mode, o.Title, o.Organization, o.Location, o.Url, o.ApplyUrl, o.Platform,
            o.Score, o.Coverage, o.Outcome, o.OutcomeReason, o.Status, o.GapsCount, o.UpdatedAt, o.CreatedAt, o.Description, o.Version,
            Deserialize<List<BreakdownRow>>(o.BreakdownJson) ?? [],
            Deserialize<List<FactRow>>(o.FactsJson) ?? [],
            Deserialize<List<string>>(o.GapsJson) ?? [],
            evidence.Select(e => new EvidenceDto(e.Id, e.SourceId, labels.GetValueOrDefault(e.SourceId), e.Url, e.RetrievedAt, e.Excerpt,
                e.ExtractionMethod)).ToList(),
            activities,
            o.LastResearchJobId is { } jobId
                ? await opportunities.ScoredByAsync(user.OwnerId, jobId, ct)
                : null);
    }

    public static OpportunitySummaryDto ToSummary(Opportunity o) => new(
        o.Id, o.CampaignId, o.Mode, o.Title, o.Organization, o.Location, o.Url, o.ApplyUrl, o.Platform, o.Score, o.Coverage,
        o.Outcome, o.OutcomeReason, o.Status, o.GapsCount, o.UpdatedAt, o.CreatedAt);

    private static T? Deserialize<T>(string json) where T : class
    {
        try { return JsonSerializer.Deserialize<T>(json, JsonSerializerOptions.Web); }
        catch (JsonException) { return null; }
    }

    private static string FileSafe(string name)
    {
        var chars = name.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-').ToArray();
        var slug = string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
        return slug.Length == 0 ? "campaign" : slug.Length > 60 ? slug[..60] : slug;
    }
}
