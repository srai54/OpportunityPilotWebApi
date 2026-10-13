using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpportunityPilot.Application.Abstractions;
using OpportunityPilot.Application.Analytics;
using OpportunityPilot.Application.Common;
using OpportunityPilot.Domain.Opportunities;
using OpportunityPilot.Domain.Research;

namespace OpportunityPilot.Application.Research;

/// <summary>User-facing research jobs: queue, list, read and cancel. The work itself happens in <see cref="ResearchRunner"/>.</summary>
public sealed class ResearchService(IAppDbContext db, ICurrentUser user, TimeProvider clock, UserEventLog events)
{
    public const int MaxListedJobs = 20;
    public const int MaxEvents = 100;

    private IQueryable<ResearchJob> Owned => db.ResearchJobs.Where(j => j.OwnerId == user.OwnerId);

    /// <summary>Returns the campaign's queued or running job if there is one, so repeated clicks never start a second run.</summary>
    public async Task<QueuedResearchDto> QueueAsync(Guid campaignId, CancellationToken ct)
    {
        var ownerId = user.OwnerId;
        var name = await db.Campaigns.AsNoTracking().Where(c => c.Id == campaignId && c.OwnerId == ownerId).Select(c => c.Name).FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException("Campaign not found.");
        var alreadyRunning = await ActiveJobIdAsync(db, ownerId, campaignId, ct) is not null;
        var id = await QueueForOwnerAsync(db, ownerId, campaignId, clock.GetUtcNow().UtcDateTime, ct);
        // Only a new run is a new action: clicking again while one is queued or running returns the same run.
        if (!alreadyRunning) await events.RecordAsync(UserEventKinds.RunStarted, $"Started a search run for \"{name}\"", ct);
        return new QueuedResearchDto(id);
    }

    /// <summary>Shared with the agent endpoint. The caller has already checked the campaign belongs to <paramref name="ownerId"/>.</summary>
    internal static async Task<Guid> QueueForOwnerAsync(IAppDbContext db, Guid ownerId, Guid campaignId, DateTime now, CancellationToken ct)
    {
        var active = await ActiveJobIdAsync(db, ownerId, campaignId, ct);
        if (active is { } existing) return existing;

        if (!await db.Sources.AnyAsync(s => s.OwnerId == ownerId && s.CampaignId == campaignId, ct))
            throw new RequestValidationException(new Dictionary<string, string[]>
            {
                ["campaign"] = ["Add at least one source before running research."]
            });

        var campaign = await db.Campaigns.FirstOrDefaultAsync(c => c.Id == campaignId && c.OwnerId == ownerId, ct)
            ?? throw new NotFoundException("Campaign not found.");
        var profile = await db.Profiles.FirstOrDefaultAsync(p => p.Id == campaign.ProfileId && p.OwnerId == ownerId, ct)
            ?? throw new NotFoundException("Campaign profile not found.");
        var job = new ResearchJob(ownerId, campaignId, now, campaign.Version, profile.Version,
            profile.StructuredDataJson, campaign.CriteriaJson);
        db.ResearchJobs.Add(job);
        try
        {
            await db.SaveChangesAsync(ct);
            return job.Id;
        }
        catch (DbUpdateException)
        {
            // The unique "one active job per campaign" index caught a simultaneous queue request: use that job.
            db.ResearchJobs.Entry(job).State = EntityState.Detached;
            return await ActiveJobIdAsync(db, ownerId, campaignId, ct)
                ?? throw new ConflictException("Research was queued at the same time elsewhere. Try again.");
        }
    }

    public async Task<IReadOnlyList<ResearchJobDto>> ListAsync(Guid campaignId, CancellationToken ct)
    {
        if (!await db.Campaigns.AnyAsync(c => c.Id == campaignId && c.OwnerId == user.OwnerId, ct))
            throw new NotFoundException("Campaign not found.");
        var jobs = await Owned.Where(j => j.CampaignId == campaignId)
            .OrderByDescending(j => j.CreatedAt).ThenBy(j => j.Id)
            .Take(MaxListedJobs)
            .ToListAsync(ct);
        return jobs.Select(j => ToDto(j, null, includeInputs: false)).ToList();
    }

    public async Task<ResearchJobDto> GetAsync(Guid id, CancellationToken ct)
    {
        var job = await FindOwnedAsync(id, ct);
        return ToDto(job, await EventsAsync(job.Id, ct), includeInputs: true);
    }

    public async Task<ResearchJobDto> CancelAsync(Guid id, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var job = await FindOwnedAsync(id, ct);
            if (!job.RequestCancel(clock.GetUtcNow().UtcDateTime)) return ToDto(job, await EventsAsync(job.Id, ct), includeInputs: true);
            try
            {
                await db.SaveChangesAsync(ct);
                return ToDto(job, await EventsAsync(job.Id, ct), includeInputs: true);
            }
            catch (DbUpdateConcurrencyException) when (attempt < 2)
            {
                // The processor saved progress (or claimed the job) in between; re-read and apply the request again.
                db.ChangeTracker.Clear();
            }
            catch (DbUpdateConcurrencyException)
            {
                throw new ConflictException("The job is changing right now. Try cancelling again.");
            }
        }
    }

    private static async Task<Guid?> ActiveJobIdAsync(IAppDbContext db, Guid ownerId, Guid campaignId, CancellationToken ct) =>
        await db.ResearchJobs
            .Where(j => j.OwnerId == ownerId && j.CampaignId == campaignId &&
                        (j.State == ResearchJobState.Queued || j.State == ResearchJobState.Running))
            .OrderByDescending(j => j.CreatedAt)
            .Select(j => (Guid?)j.Id)
            .FirstOrDefaultAsync(ct);

    private async Task<ResearchJob> FindOwnedAsync(Guid id, CancellationToken ct) =>
        await Owned.FirstOrDefaultAsync(j => j.Id == id, ct)
        ?? throw new NotFoundException("Research job not found.");

    private async Task<List<ResearchEventDto>> EventsAsync(Guid jobId, CancellationToken ct) =>
        await db.ResearchEvents.Where(e => e.JobId == jobId)
            .OrderByDescending(e => e.At).ThenByDescending(e => e.Id)
            .Take(MaxEvents)
            .Select(e => new ResearchEventDto(e.At, e.Stage, e.Level, e.Message))
            .ToListAsync(ct);

    private static ResearchJobDto ToDto(ResearchJob j, IReadOnlyList<ResearchEventDto>? events, bool includeInputs) =>
        new(j.Id, j.CampaignId, j.State, j.Stage, j.CreatedAt, j.StartedAt, j.FinishedAt, j.SafeError,
            ResearchCounts.FromJson(j.CountsJson), includeInputs ? Inputs(j) : null, events);

    private static ResearchInputSnapshotDto Inputs(ResearchJob job)
    {
        using var profile = JsonDocument.Parse(job.ProfileSnapshotJson);
        using var criteria = JsonDocument.Parse(job.CriteriaSnapshotJson);
        return new(job.CampaignVersion, job.ProfileVersion, profile.RootElement.Clone(), criteria.RootElement.Clone());
    }
}
