using OpportunityPilot.Domain.Drafts;

namespace OpportunityPilot.Application.Drafts;

/// <summary>Outreach and cover-note drafts: generation, edits, approval and counts. Owner-scoped through <c>ICurrentUser</c>.</summary>
public interface IDraftService
{
    Task<DraftDto> CreateAsync(Guid opportunityId, CreateDraftRequest request, CancellationToken ct);
    Task<IReadOnlyList<DraftDto>> ListForOpportunityAsync(Guid opportunityId, CancellationToken ct);
    Task<DraftPageDto> ListAsync(DraftState? state, DraftChannel? channel, Guid? campaignId, int take, int skip, CancellationToken ct);
    Task<DraftDto> GetAsync(Guid id, CancellationToken ct);
    Task<DraftDto> UpdateAsync(Guid id, UpdateDraftRequest request, CancellationToken ct);
    Task<DraftDto> ApproveAsync(Guid id, ApproveDraftRequest request, CancellationToken ct);
    Task<IReadOnlyList<BatchApproveDraftResult>> BatchApproveAsync(BatchApproveDraftRequest request, CancellationToken ct);
    Task<DraftDto> RevokeAsync(Guid id, CancellationToken ct);
    Task<DraftDto> ReopenAsync(Guid id, CancellationToken ct);
    Task DeleteAsync(Guid id, CancellationToken ct);
    Task<int> CountAwaitingReviewAsync(CancellationToken ct);
    Task<int> CountApprovedAsync(CancellationToken ct);
    Task<int> CountSentAsync(CancellationToken ct);
}
