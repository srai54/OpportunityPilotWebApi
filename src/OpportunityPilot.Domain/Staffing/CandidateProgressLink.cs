using OpportunityPilot.Domain.Common;

namespace OpportunityPilot.Domain.Staffing;

/// <summary>What a candidate may see about their own progress. The stage is always shown; the rest is the sales team's choice.</summary>
[Flags]
public enum ProgressShow
{
    None = 0,
    Client = 1,
    Role = 2,
    Interviews = 4,
    Offer = 8,
    /// <summary>A short note from the recruiter at the top of the page.</summary>
    Note = 16,
    /// <summary>Prep tips the recruiter wrote for each scheduled interview.</summary>
    Prep = 32,
    /// <summary>Open roles the recruiter suggests; the candidate can answer "interested" or "not for me".</summary>
    Roles = 64,
    /// <summary>The candidate writes a resume for a shared role in the agency's format; the sales team scores it.</summary>
    Resume = 128
}

/// <summary>
/// A private link that shows one candidate how their submissions are going, limited to what the sales team chose to share. Only a hash
/// of the token is stored, so the link can be shown once at creation. Revoking (or creating a new link) stops the old one at once.
/// Pay, rates and internal notes are never part of what it can show.
/// </summary>
public sealed class CandidateProgressLink : IOwned
{
    public const int MaxNoteLength = 1_000;

    private CandidateProgressLink() { }

    public CandidateProgressLink(Guid ownerId, Guid candidateId, string tokenHash, ProgressShow show, DateTime utcNow, string? note = null)
    {
        if (ownerId == Guid.Empty) throw new ArgumentException("Owner is required.", nameof(ownerId));
        if (candidateId == Guid.Empty) throw new ArgumentException("Candidate is required.", nameof(candidateId));
        if (string.IsNullOrWhiteSpace(tokenHash)) throw new ArgumentException("Token hash is required.", nameof(tokenHash));
        Id = Guid.NewGuid();
        OwnerId = ownerId;
        CandidateId = candidateId;
        TokenHash = tokenHash;
        Show = show;
        Note = Guard.Optional(note, MaxNoteLength, nameof(note));
        CreatedAt = utcNow;
    }

    public Guid Id { get; private set; }
    public Guid OwnerId { get; private set; }
    public Guid CandidateId { get; private set; }
    public string TokenHash { get; private set; } = string.Empty;
    public ProgressShow Show { get; private set; }
    /// <summary>The recruiter's note; shown only when <see cref="ProgressShow.Note"/> is chosen.</summary>
    public string? Note { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime? RevokedAt { get; private set; }

    public bool Active => RevokedAt is null;

    public void Revoke(DateTime utcNow) => RevokedAt ??= utcNow;
}
