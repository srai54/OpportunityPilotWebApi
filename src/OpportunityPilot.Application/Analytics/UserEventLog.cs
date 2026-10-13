using OpportunityPilot.Application.Abstractions;
using OpportunityPilot.Domain.Opportunities;

namespace OpportunityPilot.Application.Analytics;

/// <summary>
/// Records "My activity" lines. <see cref="Add"/> only stages the row, so it is saved by the same <c>SaveChanges</c> as the action itself
/// (an action that fails leaves no line). <see cref="RecordAsync"/> saves on its own, for actions that save nothing else (a search).
/// A failure to write an activity line must never fail the person's action, so <see cref="RecordAsync"/> swallows storage errors.
/// </summary>
public sealed class UserEventLog(IAppDbContext db, ICurrentUser user, TimeProvider clock)
{
    public void Add(string kind, string detail, Guid? opportunityId = null, string? title = null) =>
        db.UserEvents.Add(new UserEvent(user.OwnerId, kind, clock.GetUtcNow().UtcDateTime, detail, opportunityId, title));

    public async Task RecordAsync(string kind, string detail, CancellationToken ct, Guid? opportunityId = null, string? title = null)
    {
        var row = new UserEvent(user.OwnerId, kind, clock.GetUtcNow().UtcDateTime, detail, opportunityId, title);
        db.UserEvents.Add(row);
        try { await db.SaveChangesAsync(ct); }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            db.UserEvents.Remove(row);
        }
    }
}
