using OpportunityPilot.Domain.Opportunities;
using OpportunityPilot.Domain.Staffing;

namespace OpportunityPilot.Application.Staffing;

/// <summary>Data access for staffing accounts, contacts, deals and deal activities. Every method takes the owner id.</summary>
public interface IStaffingRepository
{
    /// <summary>Most recently updated first.</summary>
    Task<List<StaffingAccount>> ListAccountsAsync(Guid ownerId, int take, int skip, CancellationToken ct);
    Task<StaffingAccount?> FindAccountAsync(Guid ownerId, Guid id, CancellationToken ct);
    /// <summary>Exact match on the stored (lower-case) domain.</summary>
    Task<StaffingAccount?> FindAccountByDomainAsync(Guid ownerId, string domain, CancellationToken ct);
    Task<bool> AccountDomainExistsAsync(Guid ownerId, string domain, CancellationToken ct);
    /// <summary>Case-insensitive match on the name; <paramref name="lowerName"/> is already lower-case.</summary>
    Task<StaffingAccount?> FindAccountByNameAsync(Guid ownerId, string lowerName, CancellationToken ct);
    /// <summary>Contacts of the given accounts, by name.</summary>
    Task<List<StaffingContact>> ContactsForAccountsAsync(Guid ownerId, IReadOnlyCollection<Guid> accountIds, CancellationToken ct);
    Task<StaffingContact?> FindContactAsync(Guid ownerId, Guid accountId, Guid contactId, CancellationToken ct);

    /// <summary>Most recently updated first.</summary>
    Task<List<StaffingDeal>> ListDealsAsync(Guid ownerId, StaffingDealStage? stage, StaffingDealSource? source, int take, int skip, CancellationToken ct);
    Task<StaffingDeal?> FindDealAsync(Guid ownerId, Guid id, CancellationToken ct);
    Task<StaffingDeal?> FindDealByReferenceAsync(Guid ownerId, string externalReference, CancellationToken ct);
    /// <summary>Activities of the given deals, newest first.</summary>
    Task<List<StaffingDealActivity>> ActivitiesForDealsAsync(Guid ownerId, IReadOnlyCollection<Guid> dealIds, CancellationToken ct);

    Task<Opportunity?> FindOpportunityAsync(Guid ownerId, Guid opportunityId, CancellationToken ct);

    void AddAccount(StaffingAccount account);
    void AddContact(StaffingContact contact);
    void AddDeal(StaffingDeal deal);
    void AddActivity(StaffingDealActivity activity);
    /// <summary>Saves the shared unit of work; throws <c>DbUpdateConcurrencyException</c> on a version clash.</summary>
    Task SaveChangesAsync(CancellationToken ct);
}
