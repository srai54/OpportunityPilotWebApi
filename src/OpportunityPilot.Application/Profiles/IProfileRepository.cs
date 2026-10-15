using OpportunityPilot.Domain.Profiles;

namespace OpportunityPilot.Application.Profiles;

/// <summary>Data access for profiles and their saved versions. Every method takes the owner id.</summary>
public interface IProfileRepository
{
    /// <summary>Most recently updated first.</summary>
    Task<IReadOnlyList<ProfileSummaryDto>> ListAsync(Guid ownerId, CancellationToken ct);
    Task<int> CountAsync(Guid ownerId, CancellationToken ct);
    Task<Profile?> FindAsync(Guid ownerId, Guid id, CancellationToken ct);
    /// <summary>Newest version first.</summary>
    Task<List<ProfileVersion>> VersionsAsync(Guid ownerId, Guid profileId, CancellationToken ct);
    void Add(Profile profile);
    void AddVersion(ProfileVersion version);
    /// <summary>Saves the shared unit of work; throws <c>DbUpdateConcurrencyException</c> on a version clash.</summary>
    Task SaveChangesAsync(CancellationToken ct);
}
