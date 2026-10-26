using OpportunityPilot.Domain.Common;

namespace OpportunityPilot.Domain.Staffing;

public enum StaffingDealSource
{
    Manual,
    Import,
    LinkedInAssisted,
    Upwork,
    Freelancer,
    Tender,
    Referral,
    PublicWeb
}

public enum StaffingDealStage
{
    New,
    Qualified,
    Shortlisted,
    OutreachApproved,
    Contacted,
    Replied,
    MeetingScheduled,
    RequirementConfirmed,
    CandidatesSubmitted,
    Interviewing,
    Offer,
    Contracting,
    Won,
    Disqualified,
    Lost,
    OnHold
}

/// <summary>The commercial staffing opportunity; stages advance only from evidence or an explicit user action.</summary>
public sealed class StaffingDeal : IOwned
{
    public const int MaxTitleLength = 300;
    public const int MaxExternalReferenceLength = 1_000;
    public const int MaxCurrencyLength = 3;
    public const int MaxNextActionLength = 500;
    public const int MaxJobDescriptionLength = 8_000;

    private StaffingDeal() { }

    public StaffingDeal(Guid ownerId, Guid accountId, Guid? contactId, string title, StaffingDealSource source, DateTime utcNow)
    {
        if (ownerId == Guid.Empty) throw new ArgumentException("Owner is required.", nameof(ownerId));
        if (accountId == Guid.Empty) throw new ArgumentException("Account is required.", nameof(accountId));
        if (!Enum.IsDefined(source)) throw new ArgumentOutOfRangeException(nameof(source));
        Id = Guid.NewGuid();
        OwnerId = ownerId;
        AccountId = accountId;
        ContactId = contactId;
        Title = Guard.Required(title, MaxTitleLength, nameof(title));
        Source = source;
        Stage = StaffingDealStage.New;
        CreatedAt = utcNow;
        UpdatedAt = utcNow;
    }

    public Guid Id { get; private set; }
    public Guid OwnerId { get; private set; }
    public Guid AccountId { get; private set; }
    public Guid? ContactId { get; private set; }
    public string Title { get; private set; } = string.Empty;
    /// <summary>The client's job description, pasted by the sales team; candidates write their resume against it.</summary>
    public string? JobDescription { get; private set; }
    public StaffingDealSource Source { get; private set; }
    public string? ExternalReference { get; private set; }
    public decimal? EstimatedValue { get; private set; }
    public string? Currency { get; private set; }
    public StaffingDealStage Stage { get; private set; }
    public StaffingDealStage? StageBeforeHold { get; private set; }
    public string? NextAction { get; private set; }
    public DateTime? NextActionAt { get; private set; }
    public int Version { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }

    /// <summary>Sets the job description. Not a commercial change: no new version, so stage moves and edits are unaffected.</summary>
    public void SetJobDescription(string? text) => JobDescription = Guard.Optional(text, MaxJobDescriptionLength, nameof(text));

    public void UpdateCommercials(string title, string? externalReference, decimal? estimatedValue, string? currency,
        string? nextAction, DateTime? nextActionAt, DateTime utcNow)
    {
        Title = Guard.Required(title, MaxTitleLength, nameof(title));
        ExternalReference = Guard.Optional(externalReference, MaxExternalReferenceLength, nameof(externalReference));
        if (estimatedValue is < 0) throw new ArgumentOutOfRangeException(nameof(estimatedValue));
        EstimatedValue = estimatedValue;
        Currency = Guard.Optional(currency?.ToUpperInvariant(), MaxCurrencyLength, nameof(currency));
        NextAction = Guard.Optional(nextAction, MaxNextActionLength, nameof(nextAction));
        NextActionAt = nextActionAt;
        Version++;
        UpdatedAt = utcNow;
    }

    public void MoveTo(StaffingDealStage next, DateTime utcNow)
    {
        if (!CanMoveTo(next)) throw new InvalidOperationException($"A deal cannot move from {Stage} to {next}.");
        if (next == Stage) return;
        if (next == StaffingDealStage.OnHold) StageBeforeHold = Stage;
        else if (Stage == StaffingDealStage.OnHold) StageBeforeHold = null;
        Stage = next;
        Version++;
        UpdatedAt = utcNow;
    }

    public bool CanMoveTo(StaffingDealStage next)
    {
        if (!Enum.IsDefined(next)) return false;
        if (next == Stage) return true;
        if (Stage is StaffingDealStage.Won or StaffingDealStage.Lost or StaffingDealStage.Disqualified) return false;
        if (Stage == StaffingDealStage.OnHold) return next == StageBeforeHold;
        if (next == StaffingDealStage.OnHold) return true;
        if (next is StaffingDealStage.Lost or StaffingDealStage.Disqualified) return true;
        return (Stage, next) switch
        {
            (StaffingDealStage.New, StaffingDealStage.Qualified) => true,
            (StaffingDealStage.Qualified, StaffingDealStage.Shortlisted) => true,
            (StaffingDealStage.Shortlisted, StaffingDealStage.OutreachApproved) => true,
            (StaffingDealStage.OutreachApproved, StaffingDealStage.Contacted) => true,
            (StaffingDealStage.Contacted, StaffingDealStage.Replied) => true,
            (StaffingDealStage.Replied, StaffingDealStage.MeetingScheduled) => true,
            (StaffingDealStage.MeetingScheduled, StaffingDealStage.RequirementConfirmed) => true,
            (StaffingDealStage.RequirementConfirmed, StaffingDealStage.CandidatesSubmitted) => true,
            (StaffingDealStage.CandidatesSubmitted, StaffingDealStage.Interviewing) => true,
            (StaffingDealStage.Interviewing, StaffingDealStage.Offer) => true,
            (StaffingDealStage.Offer, StaffingDealStage.Contracting) => true,
            (StaffingDealStage.Contracting, StaffingDealStage.Won) => true,
            _ => false
        };
    }
}
