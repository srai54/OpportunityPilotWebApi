using OpportunityPilot.Domain.Staffing;

namespace OpportunityPilot.Application.Staffing;

/// <summary>Staffing accounts, contacts and deals, and promoting a Sales opportunity into a deal. Owner-scoped through <c>ICurrentUser</c>.</summary>
public interface IStaffingService
{
    Task<IReadOnlyList<StaffingAccountDto>> ListAccountsAsync(int take, int skip, CancellationToken ct);
    Task<StaffingAccountDto> CreateAccountAsync(CreateStaffingAccountRequest request, CancellationToken ct);
    Task<StaffingContactDto> CreateContactAsync(Guid accountId, CreateStaffingContactRequest request, CancellationToken ct);
    Task<StaffingContactDto> UpdateContactAsync(Guid accountId, Guid contactId, UpdateStaffingContactRequest request, CancellationToken ct);
    Task<IReadOnlyList<StaffingDealDto>> ListDealsAsync(int take, int skip, StaffingDealStage? stage, StaffingDealSource? source, CancellationToken ct);
    Task<StaffingDealDto> GetDealAsync(Guid id, CancellationToken ct);
    Task<StaffingDealDto> CreateDealAsync(Guid accountId, CreateStaffingDealRequest request, CancellationToken ct);
    Task<StaffingDealDto> UpdateDealAsync(Guid id, UpdateStaffingDealRequest request, CancellationToken ct);
    Task<StaffingDealDto> MoveDealAsync(Guid id, MoveStaffingDealRequest request, CancellationToken ct);
    Task<StaffingDealDto> AddNoteAsync(Guid id, AddStaffingDealNoteRequest request, CancellationToken ct);
    Task<PromotedLeadDto> PromoteOpportunityAsync(Guid opportunityId, CancellationToken ct);
}
