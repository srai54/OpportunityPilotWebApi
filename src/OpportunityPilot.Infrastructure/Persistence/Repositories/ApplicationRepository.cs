using Microsoft.EntityFrameworkCore;
using OpportunityPilot.Application.Applications;
using OpportunityPilot.Domain.Applications;
using OpportunityPilot.Domain.Common;
using OpportunityPilot.Domain.Opportunities;

namespace OpportunityPilot.Infrastructure.Persistence.Repositories;

public sealed class ApplicationRepository(AppDbContext db) : IApplicationRepository
{
    private IQueryable<JobApplication> Owned(Guid ownerId) => db.JobApplications.Where(a => a.OwnerId == ownerId);

    public Task<List<JobApplication>> FindByJobsAsync(Guid ownerId, IReadOnlyCollection<ApplicationPlatform> platforms,
        IReadOnlyCollection<string> externalJobIds, CancellationToken ct)
    {
        var platformList = platforms.ToList();
        var jobIds = externalJobIds.ToList();
        return Owned(ownerId).Where(a => platformList.Contains(a.Platform) && jobIds.Contains(a.ExternalJobId)).ToListAsync(ct);
    }

    public Task<List<Opportunity>> ShortlistedJobOpportunitiesAsync(Guid ownerId, IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        var idList = ids.ToList();
        return db.Opportunities
            .Where(o => o.OwnerId == ownerId && idList.Contains(o.Id) && o.Mode == OpportunityMode.Job && o.Status == OpportunityStatus.Shortlisted)
            .ToListAsync(ct);
    }

    public async Task<ApplicationPageDto> PageAsync(Guid ownerId, ApplicationStatus? status, ApplicationPlatform? platform, int take, int skip,
        CancellationToken ct)
    {
        var query = Owned(ownerId);
        if (status is { } s) query = query.Where(a => a.Status == s);
        if (platform is { } p) query = query.Where(a => a.Platform == p);

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(a => a.OccurredAt).ThenBy(a => a.Id)
            .Skip(skip).Take(take)
            .Select(a => new ApplicationDto(a.Id, a.Platform, a.ExternalJobId, a.JobUrl, a.Title, a.Company,
                a.Location, a.Status, a.Detail, a.OccurredAt, a.UpdatedAt))
            .ToListAsync(ct);
        return new ApplicationPageDto(total, items);
    }

    public Task<Dictionary<ApplicationStatus, int>> CountByStatusAsync(Guid ownerId, CancellationToken ct) =>
        Owned(ownerId)
            .GroupBy(a => a.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Status, x => x.Count, ct);

    public Task<int> CountAppliedSinceAsync(Guid ownerId, DateTime sinceUtc, CancellationToken ct) =>
        Owned(ownerId).CountAsync(a => a.Status == ApplicationStatus.Applied && a.OccurredAt >= sinceUtc, ct);

    public Task<DateTime?> LastActivityAtAsync(Guid ownerId, CancellationToken ct) =>
        Owned(ownerId).MaxAsync(a => (DateTime?)a.OccurredAt, ct);

    public void Add(JobApplication application) => db.JobApplications.Add(application);

    public void AddActivity(Activity activity) => db.Activities.Add(activity);

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}
