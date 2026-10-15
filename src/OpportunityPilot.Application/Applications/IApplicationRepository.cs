using OpportunityPilot.Domain.Applications;
using OpportunityPilot.Domain.Opportunities;

namespace OpportunityPilot.Application.Applications;

/// <summary>Data access for agent-reported job applications. Every method takes the owner id.</summary>
public interface IApplicationRepository
{
    /// <summary>Rows whose platform and external job id are in the given sets (the caller matches exact pairs).</summary>
    Task<List<JobApplication>> FindByJobsAsync(Guid ownerId, IReadOnlyCollection<ApplicationPlatform> platforms,
        IReadOnlyCollection<string> externalJobIds, CancellationToken ct);
    /// <summary>The owner's shortlisted Job opportunities among <paramref name="ids"/>, tracked for update.</summary>
    Task<List<Opportunity>> ShortlistedJobOpportunitiesAsync(Guid ownerId, IReadOnlyCollection<Guid> ids, CancellationToken ct);
    /// <summary>Newest first.</summary>
    Task<ApplicationPageDto> PageAsync(Guid ownerId, ApplicationStatus? status, ApplicationPlatform? platform, int take, int skip, CancellationToken ct);
    Task<Dictionary<ApplicationStatus, int>> CountByStatusAsync(Guid ownerId, CancellationToken ct);
    Task<int> CountAppliedSinceAsync(Guid ownerId, DateTime sinceUtc, CancellationToken ct);
    Task<DateTime?> LastActivityAtAsync(Guid ownerId, CancellationToken ct);
    void Add(JobApplication application);
    void AddActivity(Activity activity);
    /// <summary>Saves the shared unit of work; throws <c>DbUpdateException</c> on a duplicate or version clash.</summary>
    Task SaveChangesAsync(CancellationToken ct);
}
