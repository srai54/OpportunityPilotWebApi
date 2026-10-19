using Microsoft.EntityFrameworkCore;
using OpportunityPilot.Application.Abstractions;
using OpportunityPilot.Application.Common;
using OpportunityPilot.Domain.Staffing;

namespace OpportunityPilot.Application.Staffing;

public sealed class StaffingService(IStaffingRepository staffing, ICurrentUser user, TimeProvider clock) : IStaffingService
{
    public async Task<IReadOnlyList<StaffingAccountDto>> ListAccountsAsync(int take, int skip, CancellationToken ct)
    {
        take = Math.Clamp(take, 1, 200);
        skip = Math.Max(0, skip);
        var accounts = await staffing.ListAccountsAsync(user.OwnerId, take, skip, ct);
        var ids = accounts.Select(x => x.Id).ToArray();
        var contacts = await staffing.ContactsForAccountsAsync(user.OwnerId, ids, ct);
        return accounts.Select(x => ToDto(x, contacts.Where(c => c.AccountId == x.Id))).ToList();
    }

    public async Task<StaffingAccountDto> CreateAccountAsync(CreateStaffingAccountRequest request, CancellationToken ct)
    {
        if (request is null) throw Invalid("body", "Request body is required.");
        if (string.IsNullOrWhiteSpace(request.Name)) throw Invalid("name", "Account name is required.");
        if (!Enum.IsDefined(request.Source)) throw Invalid("source", "Unknown account source.");
        if (!string.IsNullOrWhiteSpace(request.Domain)
            && await staffing.AccountDomainExistsAsync(user.OwnerId, request.Domain.Trim().ToLowerInvariant(), ct))
            throw new ConflictException("An account with this domain already exists.");

        StaffingAccount account;
        try
        {
            account = new(user.OwnerId, request.Name, request.Source, Now);
            account.Update(request.Name, NormaliseDomain(request.Domain), request.Industry, request.Location, request.SourceReference, Now);
        }
        catch (ArgumentException ex) { throw Invalid("account", ex.Message); }
        staffing.AddAccount(account);
        await staffing.SaveChangesAsync(ct);
        return ToDto(account, []);
    }

    public async Task<StaffingContactDto> CreateContactAsync(Guid accountId, CreateStaffingContactRequest request, CancellationToken ct)
    {
        if (request is null) throw Invalid("body", "Request body is required.");
        if (string.IsNullOrWhiteSpace(request.Name)) throw Invalid("name", "Contact name is required.");
        _ = await FindAccountAsync(accountId, ct);
        StaffingContact contact;
        try
        {
            contact = new(user.OwnerId, accountId, request.Name, Now);
            contact.Update(
                request.Name, request.Title, request.Email, request.EmailVerified, request.LinkedInUrl, request.Evidence, request.Phone, Now);
        }
        catch (ArgumentException ex) { throw Invalid("contact", ex.Message); }
        staffing.AddContact(contact);
        await staffing.SaveChangesAsync(ct);
        return ToDto(contact);
    }

    public async Task<StaffingContactDto> UpdateContactAsync(
        Guid accountId, Guid contactId, UpdateStaffingContactRequest request, CancellationToken ct)
    {
        if (request is null) throw Invalid("body", "Request body is required.");
        if (string.IsNullOrWhiteSpace(request.Name)) throw Invalid("name", "Contact name is required.");
        var contact = await staffing.FindContactAsync(user.OwnerId, accountId, contactId, ct)
            ?? throw new NotFoundException("Contact not found.");
        if (contact.Version != request.ExpectedVersion)
            throw new ConflictException($"The contact changed elsewhere (now version {contact.Version}). Reload before saving.");
        try
        {
            contact.Update(
                request.Name, request.Title, request.Email, request.EmailVerified, request.LinkedInUrl, request.Evidence, request.Phone, Now);
        }
        catch (ArgumentException ex) { throw Invalid("contact", ex.Message); }
        try { await staffing.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw new ConflictException("The contact changed elsewhere. Reload before saving."); }
        return ToDto(contact);
    }

    public async Task<IReadOnlyList<StaffingDealDto>> ListDealsAsync(
        int take, int skip, StaffingDealStage? stage, StaffingDealSource? source, CancellationToken ct)
    {
        take = Math.Clamp(take, 1, 200);
        skip = Math.Max(0, skip);
        var deals = await staffing.ListDealsAsync(user.OwnerId, stage, source, take, skip, ct);
        return await WithActivitiesAsync(deals, ct);
    }

    public async Task<StaffingDealDto> GetDealAsync(Guid id, CancellationToken ct)
    {
        var deal = await FindDealAsync(id, ct);
        return (await WithActivitiesAsync([deal], ct))[0];
    }

    public async Task<StaffingDealDto> CreateDealAsync(Guid accountId, CreateStaffingDealRequest request, CancellationToken ct)
    {
        if (request is null) throw Invalid("body", "Request body is required.");
        if (string.IsNullOrWhiteSpace(request.Title)) throw Invalid("title", "Deal title is required.");
        if (!Enum.IsDefined(request.Source)) throw Invalid("source", "Unknown deal source.");
        _ = await FindAccountAsync(accountId, ct);
        if (request.ContactId is { } contactId)
        {
            var contact = await staffing.FindContactAsync(user.OwnerId, accountId, contactId, ct);
            if (contact is null) throw new NotFoundException("Staffing contact not found.");
        }

        StaffingDeal deal;
        try
        {
            deal = new(user.OwnerId, accountId, request.ContactId, request.Title, request.Source, Now);
            deal.UpdateCommercials(request.Title, request.ExternalReference, request.EstimatedValue, request.Currency,
                request.NextAction, request.NextActionAt, Now);
        }
        catch (ArgumentException ex) { throw Invalid("deal", ex.Message); }
        staffing.AddDeal(deal);
        staffing.AddActivity(new(user.OwnerId, deal.Id, StaffingDealActivityType.Created, "Deal created.", Now));
        await staffing.SaveChangesAsync(ct);
        return await GetDealAsync(deal.Id, ct);
    }

    public async Task<StaffingDealDto> UpdateDealAsync(Guid id, UpdateStaffingDealRequest request, CancellationToken ct)
    {
        if (request is null) throw Invalid("body", "Request body is required.");
        var deal = await FindDealAsync(id, ct);
        EnsureVersion(deal, request.ExpectedVersion);
        try
        {
            deal.UpdateCommercials(request.Title, request.ExternalReference, request.EstimatedValue, request.Currency,
                request.NextAction, request.NextActionAt, Now);
        }
        catch (ArgumentException ex) { throw Invalid("deal", ex.Message); }
        staffing.AddActivity(new(user.OwnerId, deal.Id, StaffingDealActivityType.DetailsChanged, "Deal details changed.", Now));
        await SaveConcurrentAsync(ct);
        return await GetDealAsync(id, ct);
    }

    public async Task<StaffingDealDto> MoveDealAsync(Guid id, MoveStaffingDealRequest request, CancellationToken ct)
    {
        if (request is null) throw Invalid("body", "Request body is required.");
        if (!Enum.IsDefined(request.Stage)) throw Invalid("stage", "Unknown deal stage.");
        var deal = await FindDealAsync(id, ct);
        EnsureVersion(deal, request.ExpectedVersion);
        var previous = deal.Stage;
        try { deal.MoveTo(request.Stage, Now); }
        catch (InvalidOperationException ex) { throw new ConflictException(ex.Message); }
        if (previous != deal.Stage)
            staffing.AddActivity(new(user.OwnerId, deal.Id, StaffingDealActivityType.StageChanged,
                $"{previous} → {deal.Stage}", Now));
        await SaveConcurrentAsync(ct);
        return await GetDealAsync(id, ct);
    }

    public async Task<StaffingDealDto> AddNoteAsync(Guid id, AddStaffingDealNoteRequest request, CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Detail)) throw Invalid("detail", "Note is required.");
        if (request.Detail.Trim().Length > StaffingDealActivity.MaxDetailLength)
            throw Invalid("detail", $"Note must be {StaffingDealActivity.MaxDetailLength} characters or fewer.");
        var deal = await FindDealAsync(id, ct);
        staffing.AddActivity(new(user.OwnerId, deal.Id, StaffingDealActivityType.NoteAdded, request.Detail, Now));
        await staffing.SaveChangesAsync(ct);
        return await GetDealAsync(id, ct);
    }

    private static readonly string[] BoardHosts = ["indeed.com", "linkedin.com", "seek.com.au", "seek.co.nz", "wellfound.com", "greenhouse.io", "lever.co"];

    /// <summary>
    /// X3: turn a researched Sales opportunity (a company, possibly found as a hiring signal) into a staffing account and
    /// deal, linked to its source. Accounts are de-duplicated by website domain, then by name; promoting the same
    /// opportunity twice returns the existing deal. Nothing is contacted.
    /// </summary>
    public async Task<PromotedLeadDto> PromoteOpportunityAsync(Guid opportunityId, CancellationToken ct)
    {
        var o = await staffing.FindOpportunityAsync(user.OwnerId, opportunityId, ct)
            ?? throw new NotFoundException("Opportunity not found.");
        if (o.Mode == Domain.Common.OpportunityMode.Job)
            throw Invalid("opportunity", "Only companies found by Sales campaigns become staffing leads; job postings stay in the Candidate workspace.");
        var reference = $"opportunity:{o.Id:N}";
        var existingDeal = await staffing.FindDealByReferenceAsync(user.OwnerId, reference, ct);
        if (existingDeal is not null)
            return new(await GetDealAsync(existingDeal.Id, ct), existingDeal.AccountId, true, true, IdentityConfidence.High, o.Url);

        var name = string.IsNullOrWhiteSpace(o.Organization) ? o.Title : o.Organization;
        var domain = DomainOf(o.Url);
        StaffingAccount? account = null;
        var confidence = IdentityConfidence.Low;
        if (domain is not null)
        {
            account = await staffing.FindAccountByDomainAsync(user.OwnerId, domain, ct);
            confidence = IdentityConfidence.High;
        }
        if (account is null)
        {
            var lower = name.Trim().ToLowerInvariant();
            account = await staffing.FindAccountByNameAsync(user.OwnerId, lower, ct);
            if (account is not null && domain is null) confidence = IdentityConfidence.Medium;
        }
        var reused = account is not null;
        if (account is null)
        {
            account = new StaffingAccount(user.OwnerId, name, StaffingAccountSource.PublicWeb, Now);
            account.Update(name, domain, null, o.Location, o.Url, Now);
            staffing.AddAccount(account);
        }
        var deal = new StaffingDeal(user.OwnerId, account.Id, null, $"Staffing for {name}", StaffingDealSource.PublicWeb, Now);
        deal.UpdateCommercials(deal.Title, reference, null, null, "Qualify the requirement", null, Now);
        staffing.AddDeal(deal);
        staffing.AddActivity(new(user.OwnerId, deal.Id, StaffingDealActivityType.Created,
            $"Created from {o.Mode} opportunity \"{o.Title}\" (fit {o.Score}){(o.Url is null ? "" : $" — evidence: {o.Url}")}. Account match: {confidence}.", Now));
        await staffing.SaveChangesAsync(ct);
        return new(await GetDealAsync(deal.Id, ct), account.Id, reused, false, confidence, o.Url);
    }

    /// <summary>The company's own domain from a URL; null for job boards and ATS hosts, which identify the board, not the company.</summary>
    private static string? DomainOf(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return null;
        var host = u.Host.ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal)) host = host[4..];
        return BoardHosts.Any(b => host == b || host.EndsWith("." + b, StringComparison.Ordinal)) ? null : host;
    }

    private async Task<StaffingAccount> FindAccountAsync(Guid id, CancellationToken ct) =>
        await staffing.FindAccountAsync(user.OwnerId, id, ct)
        ?? throw new NotFoundException("Staffing account not found.");

    private async Task<StaffingDeal> FindDealAsync(Guid id, CancellationToken ct) =>
        await staffing.FindDealAsync(user.OwnerId, id, ct)
        ?? throw new NotFoundException("Staffing deal not found.");

    private async Task<IReadOnlyList<StaffingDealDto>> WithActivitiesAsync(IReadOnlyList<StaffingDeal> deals, CancellationToken ct)
    {
        var ids = deals.Select(x => x.Id).ToArray();
        var activities = await staffing.ActivitiesForDealsAsync(user.OwnerId, ids, ct);
        return deals.Select(x => ToDto(x, activities.Where(a => a.DealId == x.Id))).ToList();
    }

    private async Task SaveConcurrentAsync(CancellationToken ct)
    {
        try { await staffing.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw new ConflictException("The deal changed elsewhere. Reload before saving."); }
    }

    private static void EnsureVersion(StaffingDeal deal, int expectedVersion)
    {
        if (deal.Version != expectedVersion)
            throw new ConflictException($"The deal changed elsewhere (now version {deal.Version}). Reload before saving.");
    }

    private static string? NormaliseDomain(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    private static StaffingAccountDto ToDto(StaffingAccount account, IEnumerable<StaffingContact> contacts) =>
        new(account.Id, account.Name, account.Domain, account.Industry, account.Location, account.Source,
            account.SourceReference, account.Version, contacts.Select(ToDto).ToList(), account.CreatedAt, account.UpdatedAt);

    private static StaffingContactDto ToDto(StaffingContact contact) =>
        new(contact.Id, contact.AccountId, contact.Name, contact.Title, contact.Email, contact.EmailVerified,
            contact.LinkedInUrl, contact.Evidence, contact.Version, contact.CreatedAt, contact.UpdatedAt, contact.Phone);

    private static StaffingDealDto ToDto(StaffingDeal deal, IEnumerable<StaffingDealActivity> activities) =>
        new(deal.Id, deal.AccountId, deal.ContactId, deal.Title, deal.Source, deal.ExternalReference, deal.EstimatedValue,
            deal.Currency, deal.Stage, deal.StageBeforeHold, deal.NextAction, deal.NextActionAt, deal.Version,
            activities.Select(x => new StaffingDealActivityDto(x.Id, x.Type, x.Detail, x.OccurredAt)).ToList(),
            deal.CreatedAt, deal.UpdatedAt);

    private static RequestValidationException Invalid(string field, string message) =>
        new(new Dictionary<string, string[]> { [field] = [message] });
}
