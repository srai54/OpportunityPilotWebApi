using OpportunityPilot.Domain.Common;

namespace OpportunityPilot.Domain.Opportunities;

public static class ActivityKinds
{
    public const string StatusChanged = "StatusChanged";
    public const string Applied = "Applied";
    public const string Researched = "Researched";

    /// <summary>Research moved it New → Suggested (campaign auto-suggest threshold).</summary>
    public const string Suggested = "Suggested";

    /// <summary>The user approved it in the approval queue (Suggested → Shortlisted).</summary>
    public const string Approved = "Approved";

    /// <summary>The user rejected it in the approval queue (Suggested → Dismissed).</summary>
    public const string Rejected = "Rejected";

    /// <summary>A reply arrived: entered by the user, or found in the connected inbox by reply detection.</summary>
    public const string Replied = "Replied";
}

/// <summary>History line on an opportunity. Detail is a short safe summary.</summary>
public class Activity : IOwned
{
    public const int MaxDetailLength = 500;

    private Activity() { }

    public Activity(Guid ownerId, Guid opportunityId, string kind, DateTime occurredAt, string? detail)
    {
        if (ownerId == Guid.Empty) throw new ArgumentException("Owner is required.", nameof(ownerId));
        if (opportunityId == Guid.Empty) throw new ArgumentException("Opportunity is required.", nameof(opportunityId));
        Id = Guid.NewGuid();
        OwnerId = ownerId;
        OpportunityId = opportunityId;
        Kind = Guard.Required(kind, 32, nameof(kind));
        OccurredAt = occurredAt;
        Detail = Guard.Truncate(detail, MaxDetailLength);
    }

    public Guid Id { get; private set; }
    public Guid OwnerId { get; private set; }
    public Guid OpportunityId { get; private set; }
    public string Kind { get; private set; } = string.Empty;
    public DateTime OccurredAt { get; private set; }
    public string Detail { get; private set; } = string.Empty;
}

/// <summary>Links an opportunity to the evidence its current score relied on.</summary>
public class OpportunityEvidence
{
    private OpportunityEvidence() { }

    public OpportunityEvidence(Guid opportunityId, Guid evidenceId)
    {
        if (opportunityId == Guid.Empty) throw new ArgumentException("Opportunity is required.", nameof(opportunityId));
        if (evidenceId == Guid.Empty) throw new ArgumentException("Evidence is required.", nameof(evidenceId));
        OpportunityId = opportunityId;
        EvidenceId = evidenceId;
    }

    public Guid OpportunityId { get; private set; }
    public Guid EvidenceId { get; private set; }
}
