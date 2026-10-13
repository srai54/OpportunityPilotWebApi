using Microsoft.EntityFrameworkCore;
using OpportunityPilot.Application.Abstractions;
using OpportunityPilot.Application.Analytics;
using OpportunityPilot.Application.Common;
using OpportunityPilot.Application.Opportunities;
using OpportunityPilot.Domain.Opportunities;
using OpportunityPilot.Domain.Outreach;

namespace OpportunityPilot.Application.Outreach;

public sealed class OutreachService(IAppDbContext db, ICurrentUser user, TimeProvider clock, UserEventLog events)
{
    private Guid OwnerId => user.OwnerId;

    public Task<int> CountDueAsync(CancellationToken ct)
    {
        var due = clock.GetUtcNow().UtcDateTime.AddHours(24);
        return db.NextActions.CountAsync(x => x.OwnerId == OwnerId && x.State == NextActionState.Open && x.DueAt <= due, ct);
    }

    /// <summary>All follow-ups still open, due or not.</summary>
    public Task<int> CountOpenAsync(CancellationToken ct) =>
        db.NextActions.CountAsync(x => x.OwnerId == OwnerId && x.State == NextActionState.Open, ct);

    public async Task<IReadOnlyList<SuppressionDto>> ListSuppressionsAsync(CancellationToken ct) =>
        await db.Suppressions.Where(x => x.OwnerId == OwnerId).OrderBy(x => x.NormalizedRecipient)
            .Select(x => new SuppressionDto(x.Id, x.NormalizedRecipient, x.Reason, x.CreatedAt)).ToListAsync(ct);

    public async Task<SuppressionDto> CreateSuppressionAsync(CreateSuppressionRequest request, CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Recipient)) throw Invalid("recipient", "Recipient is required.");
        Suppression row;
        try { row = new Suppression(OwnerId, request.Recipient, request.Reason, clock.GetUtcNow().UtcDateTime); }
        catch (ArgumentException ex) { throw Invalid("recipient", ex.Message); }
        if (await db.Suppressions.AnyAsync(x => x.OwnerId == OwnerId && x.NormalizedRecipient == row.NormalizedRecipient, ct))
            throw new ConflictException("That recipient is already suppressed.");
        db.Suppressions.Add(row);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { throw new ConflictException("That recipient is already suppressed."); }
        return new(row.Id, row.NormalizedRecipient, row.Reason, row.CreatedAt);
    }

    public async Task DeleteSuppressionAsync(Guid id, CancellationToken ct)
    {
        var row = await db.Suppressions.FirstOrDefaultAsync(x => x.Id == id && x.OwnerId == OwnerId, ct)
            ?? throw new NotFoundException("Suppression not found.");
        db.Suppressions.Remove(row);
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<NextActionDto>> ListNextActionsAsync(NextActionState? state, CancellationToken ct)
    {
        var query = db.NextActions.Where(x => x.OwnerId == OwnerId);
        if (state is { } value) query = query.Where(x => x.State == value);
        var rows = await (from action in query
                          join opportunity in db.Opportunities on action.OpportunityId equals opportunity.Id
                          where opportunity.OwnerId == OwnerId
                          orderby action.DueAt, action.Id
                          select new { action, opportunity.Title, opportunity.Organization }).ToListAsync(ct);
        return rows.Select(x => ToDto(x.action, x.Title, x.Organization)).ToList();
    }

    public async Task<IReadOnlyList<NextActionDto>> ListForOpportunityAsync(Guid opportunityId, CancellationToken ct)
    {
        var opportunity = await FindOpportunityAsync(opportunityId, ct);
        var rows = await db.NextActions.Where(x => x.OwnerId == OwnerId && x.OpportunityId == opportunityId)
            .OrderBy(x => x.DueAt).ThenBy(x => x.Id).ToListAsync(ct);
        return rows.Select(x => ToDto(x, opportunity.Title, opportunity.Organization)).ToList();
    }

    public async Task<NextActionDto> CreateNextActionAsync(Guid opportunityId, CreateNextActionRequest request, CancellationToken ct)
    {
        var opportunity = await FindOpportunityAsync(opportunityId, ct);
        if (request is null || !Enum.IsDefined(request.Kind)) throw Invalid("kind", "Unknown next-action kind.");
        if (string.IsNullOrWhiteSpace(request.TimeZone)) throw Invalid("timeZone", "Time zone is required.");
        if (request.DueAt == default) throw Invalid("dueAt", "Due date is required.");
        NextAction row;
        try { row = new NextAction(OwnerId, opportunityId, request.Kind, request.Note, request.DueAt, request.TimeZone, clock.GetUtcNow().UtcDateTime); }
        catch (ArgumentException ex) { throw Invalid("nextAction", ex.Message); }
        db.NextActions.Add(row);
        await db.SaveChangesAsync(ct);
        return ToDto(row, opportunity.Title, opportunity.Organization);
    }

    public async Task<NextActionDto> UpdateNextActionAsync(Guid id, UpdateNextActionRequest request, CancellationToken ct)
    {
        if (request is null || (request.State is null && request.DueAt is null)) throw Invalid("nextAction", "Provide state or dueAt.");
        var row = await db.NextActions.FirstOrDefaultAsync(x => x.Id == id && x.OwnerId == OwnerId, ct)
            ?? throw new NotFoundException("Next action not found.");
        var opportunity = await FindOpportunityAsync(row.OpportunityId, ct);
        try { row.Update(request.State, request.DueAt, clock.GetUtcNow().UtcDateTime); }
        catch (ArgumentOutOfRangeException) { throw Invalid("state", "Unknown next-action state."); }
        events.Add(UserEventKinds.FollowUpUpdated, request.State is { } st ? $"Marked a follow-up {st.ToString().ToLowerInvariant()}" : "Moved a follow-up to a new date", opportunity.Id, opportunity.Title);
        await db.SaveChangesAsync(ct);
        return ToDto(row, opportunity.Title, opportunity.Organization);
    }

    public async Task<IReadOnlyList<ActivityDto>> ListActivitiesAsync(Guid opportunityId, CancellationToken ct)
    {
        await FindOpportunityAsync(opportunityId, ct);
        return await db.Activities.Where(x => x.OwnerId == OwnerId && x.OpportunityId == opportunityId)
            .OrderByDescending(x => x.OccurredAt).ThenBy(x => x.Id)
            .Select(x => new ActivityDto(x.Kind, x.OccurredAt, x.Detail)).ToListAsync(ct);
    }

    public async Task<ActivityDto> CreateActivityAsync(Guid opportunityId, CreateActivityRequest request, CancellationToken ct)
    {
        var opportunity = await FindOpportunityAsync(opportunityId, ct);
        var kind = request?.Kind?.Trim();
        var allowed = new[] { "Note", "Contacted", "Replied", "Interested", "NotInterested" };
        if (kind is null || !allowed.Contains(kind, StringComparer.OrdinalIgnoreCase)) throw Invalid("kind", "Unknown activity kind.");
        kind = allowed.First(x => string.Equals(x, kind, StringComparison.OrdinalIgnoreCase));
        var at = request!.OccurredAt?.ToUniversalTime() ?? clock.GetUtcNow().UtcDateTime;
        var row = new Activity(OwnerId, opportunityId, kind, at, request.Detail);
        db.Activities.Add(row);
        var target = kind switch
        {
            "Contacted" => OpportunityStatus.Contacted,
            "Replied" => OpportunityStatus.Responded,
            "Interested" => OpportunityStatus.Interested,
            "NotInterested" => OpportunityStatus.Closed,
            _ => (OpportunityStatus?)null
        };
        if (target is { } status)
        {
            var statusActivity = opportunity.ChangeStatus(status, at);
            if (statusActivity is not null) db.Activities.Add(statusActivity);
        }
        await db.SaveChangesAsync(ct);
        return new(row.Kind, row.OccurredAt, row.Detail);
    }

    private async Task<Opportunity> FindOpportunityAsync(Guid id, CancellationToken ct) =>
        await db.Opportunities.FirstOrDefaultAsync(x => x.Id == id && x.OwnerId == OwnerId, ct)
        ?? throw new NotFoundException("Opportunity not found.");

    private NextActionDto ToDto(NextAction row, string title, string organization) =>
        new(row.Id, row.OpportunityId, title, organization, row.Kind, row.Note, row.DueAt, row.TimeZone,
            row.State, row.State == NextActionState.Open && row.DueAt < clock.GetUtcNow().UtcDateTime, row.CreatedAt, row.CompletedAt);

    private static RequestValidationException Invalid(string field, string message) =>
        new(new Dictionary<string, string[]> { [field] = [message] });
}
