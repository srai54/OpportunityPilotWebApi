using Microsoft.EntityFrameworkCore;
using OpportunityPilot.Application.Abstractions;
using OpportunityPilot.Application.Common;
using OpportunityPilot.Domain.Campaigns;

namespace OpportunityPilot.Application.Campaigns;

public sealed class CampaignService(IAppDbContext db, ICurrentUser user, TimeProvider clock)
{
    private IQueryable<Campaign> Owned => db.Campaigns.Where(c => c.OwnerId == user.OwnerId);

    public async Task<IReadOnlyList<CampaignSummaryDto>> ListAsync(CancellationToken ct)
    {
        var campaigns = await Owned.OrderByDescending(c => c.CreatedAt).ThenBy(c => c.Id).ToListAsync(ct);
        var stats = await StatsAsync(campaigns.Select(c => c.Id).ToList(), ct);
        return campaigns.Select(c =>
        {
            var s = stats.For(c.Id);
            return new CampaignSummaryDto(c.Id, c.ProfileId, c.Mode, c.Name, c.Goal, c.ResultLimit, c.Version,
                c.CreatedAt, c.UpdatedAt, s.Sources, s.Opportunities, s.LastJob, c.AutoSuggestMinScore, c.AutoApproveMinScore);
        }).ToList();
    }

    public async Task<int> CountAsync(CancellationToken ct) => await Owned.CountAsync(ct);

    public async Task<CampaignDto> GetAsync(Guid id, CancellationToken ct) =>
        await ToDtoAsync(await FindOwnedAsync(id, ct), ct);

    public async Task<CampaignDto> CreateAsync(CreateCampaignRequest request, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (request is null) throw new RequestValidationException(new Dictionary<string, string[]> { ["body"] = ["Request body is required."] });
        if (!Enum.IsDefined(request.Mode)) errors["mode"] = ["Unknown mode."];
        else if (!CampaignCriteria.IsSupportedMode(request.Mode)) errors["mode"] = ["Unknown mode."];

        if (request.ProfileId == Guid.Empty || !await db.Profiles.AnyAsync(p => p.Id == request.ProfileId && p.OwnerId == user.OwnerId, ct))
            errors["profileId"] = ["Profile not found."];

        var (criteria, weights, resultLimit) = ValidateFields(request.Mode, request.Name, request.Goal, request.Criteria,
            request.Weights, request.ResultLimit ?? Campaign.DefaultResultLimit, errors);
        ValidateAutoSuggest(request.Mode, request.AutoSuggestMinScore, errors);
        ValidateAutoApprove(request.Mode, request.AutoApproveMinScore, errors);
        if (errors.Count > 0) throw new RequestValidationException(errors);

        var campaign = new Campaign(user.OwnerId, request.ProfileId, request.Mode, request.Name, request.Goal,
            criteria.ToJson(), CampaignWeights.ToJson(weights), resultLimit, clock.GetUtcNow().UtcDateTime, request.AutoSuggestMinScore);
        campaign.SetAutoApprove(request.AutoApproveMinScore);
        db.Campaigns.Add(campaign);
        await db.SaveChangesAsync(ct);
        return await ToDtoAsync(campaign, ct);
    }

    public async Task<CampaignDto> UpdateAsync(Guid id, UpdateCampaignRequest request, CancellationToken ct)
    {
        if (request is null) throw new RequestValidationException(new Dictionary<string, string[]> { ["body"] = ["Request body is required."] });
        var campaign = await FindOwnedAsync(id, ct);

        var errors = new Dictionary<string, string[]>();
        // Weights left out of an edit keep their current values rather than resetting to defaults.
        var weightsInput = request.Weights
            ?? CampaignWeights.FromJson(campaign.Mode, campaign.WeightsJson).ToDictionary(kv => kv.Key, kv => (double)kv.Value);
        var (criteria, weights, resultLimit) = ValidateFields(campaign.Mode, request.Name, request.Goal, request.Criteria,
            weightsInput, request.ResultLimit ?? campaign.ResultLimit, errors);
        // Left out of an edit, the threshold keeps its stored value; an explicit null turns auto-suggest off.
        var autoSuggest = request.AutoSuggestMinScoreSent ? request.AutoSuggestMinScore : campaign.AutoSuggestMinScore;
        ValidateAutoSuggest(campaign.Mode, autoSuggest, errors);
        var autoApprove = request.AutoApproveMinScoreSent ? request.AutoApproveMinScore : campaign.AutoApproveMinScore;
        ValidateAutoApprove(campaign.Mode, autoApprove, errors);
        if (errors.Count > 0) throw new RequestValidationException(errors);

        if (campaign.Version != request.ExpectedVersion)
            throw new ConflictException($"Campaign was changed elsewhere (now version {campaign.Version}). Reload before saving.");

        campaign.SetAutoApprove(autoApprove);
        campaign.Update(request.Name, request.Goal, criteria.ToJson(), CampaignWeights.ToJson(weights), resultLimit, autoSuggest,
            clock.GetUtcNow().UtcDateTime);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException("Campaign was changed elsewhere. Reload before saving.");
        }
        return await ToDtoAsync(campaign, ct);
    }

    private async Task<Campaign> FindOwnedAsync(Guid id, CancellationToken ct) =>
        await Owned.FirstOrDefaultAsync(c => c.Id == id, ct)
        ?? throw new NotFoundException("Campaign not found.");

    private static (CampaignCriteria Criteria, Dictionary<string, int> Weights, int ResultLimit) ValidateFields(
        Domain.Common.OpportunityMode mode, string? name, string? goal, CampaignCriteria? criteriaInput,
        IReadOnlyDictionary<string, double>? weightsInput, int resultLimit, Dictionary<string, string[]> errors)
    {
        if (string.IsNullOrWhiteSpace(name)) errors["name"] = ["Name is required."];
        else if (name.Trim().Length > Campaign.MaxNameLength) errors["name"] = [$"Name must be at most {Campaign.MaxNameLength} characters."];
        if (goal is not null && goal.Trim().Length > Campaign.MaxGoalLength)
            errors["goal"] = [$"Goal must be at most {Campaign.MaxGoalLength} characters."];
        if (resultLimit is < Campaign.MinResultLimit or > Campaign.MaxResultLimit)
            errors["resultLimit"] = [$"Result limit must be between {Campaign.MinResultLimit} and {Campaign.MaxResultLimit}."];

        var criteria = CampaignCriteria.Normalise(criteriaInput, errors);
        var weights = CampaignWeights.Normalise(mode, weightsInput, errors);
        return (criteria, weights, resultLimit);
    }

    private static void ValidateAutoSuggest(Domain.Common.OpportunityMode mode, int? minScore, Dictionary<string, string[]> errors)
    {
        if (minScore is null) return;
        if (mode != Domain.Common.OpportunityMode.Job)
            errors["autoSuggestMinScore"] = ["Suggestions for approval are available for Job campaigns only."];
        else if (minScore is < Campaign.MinAutoSuggestScore or > Campaign.MaxAutoSuggestScore)
            errors["autoSuggestMinScore"] = [$"Auto-suggest score must be between {Campaign.MinAutoSuggestScore} and {Campaign.MaxAutoSuggestScore}, or null to turn it off."];
    }

    private static void ValidateAutoApprove(Domain.Common.OpportunityMode mode, int? minScore, Dictionary<string, string[]> errors)
    {
        if (minScore is null) return;
        if (mode != Domain.Common.OpportunityMode.Job)
            errors["autoApproveMinScore"] = ["Automatic approval is available for Job campaigns only."];
        else if (minScore is < Campaign.MinAutoSuggestScore or > Campaign.MaxAutoSuggestScore)
            errors["autoApproveMinScore"] = [$"Auto-approve score must be between {Campaign.MinAutoSuggestScore} and {Campaign.MaxAutoSuggestScore}, or null to turn it off."];
    }

    private async Task<CampaignDto> ToDtoAsync(Campaign c, CancellationToken ct)
    {
        var s = (await StatsAsync([c.Id], ct)).For(c.Id);
        return new CampaignDto(c.Id, c.ProfileId, c.Mode, c.Name, c.Goal, c.ResultLimit, c.Version, c.CreatedAt, c.UpdatedAt,
            s.Sources, s.Opportunities, s.LastJob,
            CampaignCriteria.FromJson(c.CriteriaJson), CampaignWeights.FromJson(c.Mode, c.WeightsJson), c.AutoSuggestMinScore, c.AutoApproveMinScore);
    }

    private sealed record Stats(int Sources, int Opportunities, LastJobDto? LastJob);

    private sealed class StatsLookup(Dictionary<Guid, int> sources, Dictionary<Guid, int> opportunities, Dictionary<Guid, LastJobDto> jobs)
    {
        public Stats For(Guid id) => new(sources.GetValueOrDefault(id), opportunities.GetValueOrDefault(id), jobs.GetValueOrDefault(id));
    }

    // Separate grouped queries rather than correlated subqueries: portable across Postgres, SQL Server and InMemory.
    private async Task<StatsLookup> StatsAsync(List<Guid> ids, CancellationToken ct)
    {
        var ownerId = user.OwnerId;
        var sources = await db.Sources.Where(s => s.OwnerId == ownerId && ids.Contains(s.CampaignId))
            .GroupBy(s => s.CampaignId).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var opportunities = await db.Opportunities.Where(o => o.OwnerId == ownerId && ids.Contains(o.CampaignId))
            .GroupBy(o => o.CampaignId).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var jobs = (await db.ResearchJobs.Where(j => j.OwnerId == ownerId && ids.Contains(j.CampaignId))
                .Select(j => new { j.CampaignId, j.Id, j.State, j.Stage, j.FinishedAt, j.CreatedAt })
                .ToListAsync(ct))
            .GroupBy(j => j.CampaignId)
            .ToDictionary(g => g.Key, g =>
            {
                var last = g.OrderByDescending(j => j.CreatedAt).First();
                return new LastJobDto(last.Id, last.State, last.Stage, last.FinishedAt);
            });
        return new StatsLookup(sources, opportunities, jobs);
    }
}
