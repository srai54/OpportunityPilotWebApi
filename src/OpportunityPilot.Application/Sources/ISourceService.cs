namespace OpportunityPilot.Application.Sources;

/// <summary>A campaign's research sources: list, validated create, delete. Owner-scoped through <c>ICurrentUser</c>.</summary>
public interface ISourceService
{
    Task<IReadOnlyList<SourceDto>> ListAsync(Guid campaignId, CancellationToken ct);
    Task<SourceDto> CreateAsync(Guid campaignId, CreateSourceRequest request, CancellationToken ct);
    Task DeleteAsync(Guid campaignId, Guid sourceId, CancellationToken ct);
}
