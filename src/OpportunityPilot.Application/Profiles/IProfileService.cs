namespace OpportunityPilot.Application.Profiles;

/// <summary>Profile use cases: validation, versioning and history. Owner-scoped through <c>ICurrentUser</c>.</summary>
public interface IProfileService
{
    Task<IReadOnlyList<ProfileSummaryDto>> ListAsync(CancellationToken ct);
    Task<int> CountAsync(CancellationToken ct);
    Task<ProfileDto> GetAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<ProfileVersionDto>> VersionsAsync(Guid id, CancellationToken ct);
    Task<ProfileDto> CreateAsync(CreateProfileRequest request, CancellationToken ct);
    Task<ProfileDto> UpdateAsync(Guid id, UpdateProfileRequest request, CancellationToken ct);
}
