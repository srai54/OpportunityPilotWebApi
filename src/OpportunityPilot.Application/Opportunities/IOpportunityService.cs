using OpportunityPilot.Domain.Opportunities;

namespace OpportunityPilot.Application.Opportunities;

/// <summary>Opportunity list, detail, user status moves and CSV export. Owner-scoped through <c>ICurrentUser</c>.</summary>
public interface IOpportunityService
{
    Task<OpportunityPageDto> ListAsync(
        Guid campaignId, FilterOutcome? outcome, OpportunityStatus? status, string? sort, int take, int skip, CancellationToken ct);
    Task<int> CountQualifiedNewAsync(CancellationToken ct);
    Task<int> CountShortlistedAsync(CancellationToken ct);
    Task<OpportunityDetailDto> GetAsync(Guid id, CancellationToken ct);
    Task<OpportunityDetailDto> UpdateStatusAsync(Guid id, UpdateOpportunityStatusRequest request, CancellationToken ct);
    Task<CsvExport> ExportAsync(Guid campaignId, CancellationToken ct);
}
