using OpportunityPilot.Domain.Common;

namespace OpportunityPilot.Domain.Opportunities;

/// <summary>The kinds of things a person does that are not a change on one opportunity (those are <see cref="Activity"/> rows).</summary>
public static class UserEventKinds
{
    public const string Searched = "Searched";
    public const string RunStarted = "RunStarted";
    public const string ProfileSaved = "ProfileSaved";
    public const string DraftCreated = "DraftCreated";
    public const string DraftEdited = "DraftEdited";
    public const string DraftApproved = "DraftApproved";
    public const string FollowUpUpdated = "FollowUpUpdated";
    public const string MailboxConnected = "MailboxConnected";

    public static readonly IReadOnlyList<string> All =
        [Searched, RunStarted, ProfileSaved, DraftCreated, DraftEdited, DraftApproved, FollowUpUpdated, MailboxConnected];
}

/// <summary>
/// One line of "My activity" for something the person did: a search, a run, a saved profile, a draft written, edited or approved.
/// It holds a short safe summary only (never message text, tokens or search results). It may point at an opportunity.
/// </summary>
public sealed class UserEvent : IOwned
{
    public const int MaxDetailLength = 300;
    public const int MaxTitleLength = 200;

    private UserEvent() { }

    public UserEvent(Guid ownerId, string kind, DateTime occurredAt, string? detail, Guid? opportunityId = null, string? title = null)
    {
        if (ownerId == Guid.Empty) throw new ArgumentException("Owner is required.", nameof(ownerId));
        if (!UserEventKinds.All.Contains(kind)) throw new ArgumentOutOfRangeException(nameof(kind), "Unknown event kind.");
        Id = Guid.NewGuid();
        OwnerId = ownerId;
        Kind = kind;
        OccurredAt = occurredAt;
        Detail = Guard.Truncate(detail, MaxDetailLength);
        OpportunityId = opportunityId;
        Title = Guard.TruncateOptional(title, MaxTitleLength);
    }

    public Guid Id { get; private set; }
    public Guid OwnerId { get; private set; }
    public string Kind { get; private set; } = string.Empty;
    public DateTime OccurredAt { get; private set; }
    public string Detail { get; private set; } = string.Empty;
    public Guid? OpportunityId { get; private set; }
    public string? Title { get; private set; }
}
