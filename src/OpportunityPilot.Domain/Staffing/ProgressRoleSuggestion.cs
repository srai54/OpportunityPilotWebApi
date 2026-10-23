using OpportunityPilot.Domain.Common;

namespace OpportunityPilot.Domain.Staffing;

public enum RoleAnswer { None, Interested, NotForMe }

/// <summary>
/// An open role (deal) the recruiter suggests to a candidate through their progress link, and the candidate's answer. It belongs to the
/// candidate, not to one link, so a new link keeps the answers. Only the role title (and the client, when the team shares it) is shown.
/// </summary>
public sealed class ProgressRoleSuggestion : IOwned
{
    public const int MaxPerCandidate = 10;

    private ProgressRoleSuggestion() { }

    public ProgressRoleSuggestion(Guid ownerId, Guid candidateId, Guid dealId, DateTime utcNow)
    {
        if (ownerId == Guid.Empty || candidateId == Guid.Empty || dealId == Guid.Empty)
            throw new ArgumentException("Owner, candidate and deal are required.");
        Id = Guid.NewGuid();
        OwnerId = ownerId;
        CandidateId = candidateId;
        DealId = dealId;
        Answer = RoleAnswer.None;
        SuggestedAt = utcNow;
    }

    public Guid Id { get; private set; }
    public Guid OwnerId { get; private set; }
    public Guid CandidateId { get; private set; }
    public Guid DealId { get; private set; }
    public RoleAnswer Answer { get; private set; }
    public DateTime SuggestedAt { get; private set; }
    public DateTime? AnsweredAt { get; private set; }

    public void Respond(RoleAnswer answer, DateTime utcNow)
    {
        if (answer == RoleAnswer.None || !Enum.IsDefined(answer)) throw new ArgumentOutOfRangeException(nameof(answer));
        Answer = answer;
        AnsweredAt = utcNow;
    }
}
