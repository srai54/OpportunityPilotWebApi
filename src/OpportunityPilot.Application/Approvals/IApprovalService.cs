namespace OpportunityPilot.Application.Approvals;

/// <summary>The batch approval queue of Suggested opportunities. Owner-scoped through <c>ICurrentUser</c>.</summary>
public interface IApprovalService
{
    Task<ApprovalPageDto> ListAsync(Guid? campaignId, int take, int skip, CancellationToken ct);
    Task<int> CountAsync(CancellationToken ct);
    Task<DecideApprovalsResult> DecideAllAsync(DecideAllApprovalsRequest request, CancellationToken ct);
    Task<DecideApprovalsResult> DecideAsync(DecideApprovalsRequest request, CancellationToken ct);
}
