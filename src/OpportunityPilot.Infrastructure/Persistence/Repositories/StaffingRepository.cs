using Microsoft.EntityFrameworkCore;
using OpportunityPilot.Application.Staffing;
using OpportunityPilot.Domain.Opportunities;
using OpportunityPilot.Domain.Staffing;

namespace OpportunityPilot.Infrastructure.Persistence.Repositories;

public sealed class StaffingRepository(AppDbContext db) : IStaffingRepository
{
    private IQueryable<StaffingAccount> OwnedAccounts(Guid ownerId) => db.StaffingAccounts.Where(x => x.OwnerId == ownerId);
    private IQueryable<StaffingDeal> OwnedDeals(Guid ownerId) => db.StaffingDeals.Where(x => x.OwnerId == ownerId);

    public Task<List<StaffingAccount>> ListAccountsAsync(Guid ownerId, int take, int skip, CancellationToken ct) =>
        OwnedAccounts(ownerId).OrderByDescending(x => x.UpdatedAt).ThenBy(x => x.Id).Skip(skip).Take(take).ToListAsync(ct);

    public Task<StaffingAccount?> FindAccountAsync(Guid ownerId, Guid id, CancellationToken ct) =>
        OwnedAccounts(ownerId).FirstOrDefaultAsync(x => x.Id == id, ct);

    public Task<StaffingAccount?> FindAccountByDomainAsync(Guid ownerId, string domain, CancellationToken ct) =>
        OwnedAccounts(ownerId).FirstOrDefaultAsync(a => a.Domain == domain, ct);

    public Task<bool> AccountDomainExistsAsync(Guid ownerId, string domain, CancellationToken ct) =>
        OwnedAccounts(ownerId).AnyAsync(x => x.Domain == domain, ct);

    public Task<StaffingAccount?> FindAccountByNameAsync(Guid ownerId, string lowerName, CancellationToken ct) =>
        OwnedAccounts(ownerId).FirstOrDefaultAsync(a => a.Name.ToLower() == lowerName, ct);

    public Task<List<StaffingContact>> ContactsForAccountsAsync(Guid ownerId, IReadOnlyCollection<Guid> accountIds, CancellationToken ct)
    {
        var ids = accountIds.ToArray();
        return db.StaffingContacts.Where(x => x.OwnerId == ownerId && ids.Contains(x.AccountId)).OrderBy(x => x.Name).ToListAsync(ct);
    }

    public Task<StaffingContact?> FindContactAsync(Guid ownerId, Guid accountId, Guid contactId, CancellationToken ct) =>
        db.StaffingContacts.FirstOrDefaultAsync(x => x.Id == contactId && x.AccountId == accountId && x.OwnerId == ownerId, ct);

    public Task<List<StaffingDeal>> ListDealsAsync(Guid ownerId, StaffingDealStage? stage, StaffingDealSource? source, int take, int skip,
        CancellationToken ct)
    {
        var query = OwnedDeals(ownerId);
        if (stage is { } requestedStage) query = query.Where(x => x.Stage == requestedStage);
        if (source is { } requestedSource) query = query.Where(x => x.Source == requestedSource);
        return query.OrderByDescending(x => x.UpdatedAt).ThenBy(x => x.Id).Skip(skip).Take(take).ToListAsync(ct);
    }

    public Task<StaffingDeal?> FindDealAsync(Guid ownerId, Guid id, CancellationToken ct) =>
        OwnedDeals(ownerId).FirstOrDefaultAsync(x => x.Id == id, ct);

    public Task<StaffingDeal?> FindDealByReferenceAsync(Guid ownerId, string externalReference, CancellationToken ct) =>
        OwnedDeals(ownerId).FirstOrDefaultAsync(d => d.ExternalReference == externalReference, ct);

    public Task<List<StaffingDealActivity>> ActivitiesForDealsAsync(Guid ownerId, IReadOnlyCollection<Guid> dealIds, CancellationToken ct)
    {
        var ids = dealIds.ToArray();
        return db.StaffingDealActivities.Where(x => x.OwnerId == ownerId && ids.Contains(x.DealId))
            .OrderByDescending(x => x.OccurredAt).ThenBy(x => x.Id).ToListAsync(ct);
    }

    public Task<Opportunity?> FindOpportunityAsync(Guid ownerId, Guid opportunityId, CancellationToken ct) =>
        db.Opportunities.FirstOrDefaultAsync(x => x.Id == opportunityId && x.OwnerId == ownerId, ct);

    public void AddAccount(StaffingAccount account) => db.StaffingAccounts.Add(account);

    public void AddContact(StaffingContact contact) => db.StaffingContacts.Add(contact);

    public void AddDeal(StaffingDeal deal) => db.StaffingDeals.Add(deal);

    public void AddActivity(StaffingDealActivity activity) => db.StaffingDealActivities.Add(activity);

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}
