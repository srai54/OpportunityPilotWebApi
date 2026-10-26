using OpportunityPilot.Domain.Common;

namespace OpportunityPilot.Domain.Staffing;

public enum ResumeStatus { Draft, Submitted, Reviewed }

/// <summary>
/// A candidate's resume for one role (deal), written in the agency's standard format through the private progress link, then scored
/// by the sales team before it goes to the client. The content is the candidate's own JSON document (summary, skill groups, projects);
/// the JD match is the share of the job description's skills the resume covers.
/// </summary>
public sealed class CandidateResume : IOwned
{
    public const int MaxDataLength = 60_000;
    public const int MaxFeedbackLength = 2_000;

    private CandidateResume() { }

    public CandidateResume(Guid ownerId, Guid candidateId, Guid dealId, DateTime utcNow)
    {
        if (ownerId == Guid.Empty || candidateId == Guid.Empty || dealId == Guid.Empty)
            throw new ArgumentException("Owner, candidate and deal are required.");
        Id = Guid.NewGuid();
        OwnerId = ownerId;
        CandidateId = candidateId;
        DealId = dealId;
        Data = "{}";
        Status = ResumeStatus.Draft;
        CreatedAt = utcNow;
        UpdatedAt = utcNow;
    }

    public Guid Id { get; private set; }
    public Guid OwnerId { get; private set; }
    public Guid CandidateId { get; private set; }
    public Guid DealId { get; private set; }
    public string Data { get; private set; } = "{}";
    public int? MatchScore { get; private set; }
    public ResumeStatus Status { get; private set; }
    public int? SalesScore { get; private set; }
    public string? SalesFeedback { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }
    public DateTime? SubmittedAt { get; private set; }
    public DateTime? ReviewedAt { get; private set; }

    /// <summary>Saves the candidate's edits. A change after submitting goes back to draft (the last review stays visible).</summary>
    public void Save(string data, int? matchScore, DateTime utcNow)
    {
        if (string.IsNullOrWhiteSpace(data) || data.Length > MaxDataLength) throw new ArgumentException("The resume is empty or too long.", nameof(data));
        if (matchScore is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(matchScore));
        Data = data;
        MatchScore = matchScore;
        Status = ResumeStatus.Draft;
        UpdatedAt = utcNow;
    }

    public void Submit(DateTime utcNow)
    {
        if (Data.Length <= 2) throw new InvalidOperationException("Write the resume before submitting it.");
        Status = ResumeStatus.Submitted;
        SubmittedAt = utcNow;
        UpdatedAt = utcNow;
    }

    public void Review(int score, string? feedback, DateTime utcNow)
    {
        if (Status == ResumeStatus.Draft) throw new InvalidOperationException("The candidate has not submitted this resume yet.");
        if (score is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(score), "Score 0 to 100.");
        SalesScore = score;
        SalesFeedback = Guard.Optional(feedback, MaxFeedbackLength, nameof(feedback));
        Status = ResumeStatus.Reviewed;
        ReviewedAt = utcNow;
        UpdatedAt = utcNow;
    }
}
