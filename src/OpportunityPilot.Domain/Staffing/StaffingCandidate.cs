using OpportunityPilot.Domain.Common;

namespace OpportunityPilot.Domain.Staffing;

/// <summary>Facts about a company candidate that a submission may share. Nothing is shared unless the candidate allows it.</summary>
[Flags]
public enum CandidateField
{
    None = 0,
    Name = 1,
    Headline = 2,
    Skills = 4,
    Experience = 8,
    Location = 16,
    Availability = 32,
    Rate = 64,
    Email = 128,
    Phone = 256,
    Resume = 512
}

public enum CandidateConsent { Pending, Granted, Withdrawn }
public enum CandidateAvailability { Unknown, Immediate, NoticePeriod, NotAvailable }
public enum RateUnit { Hour, Day, Month, Year }

/// <summary>
/// A candidate on the company's bench. Consent and the set of shareable fields are explicit and audited; a submission can
/// only snapshot fields that are both shareable here and chosen for that client, and only while consent is granted.
/// </summary>
public sealed class StaffingCandidate : IOwned
{
    public const int MaxNameLength = 200;
    public const int MaxHeadlineLength = 300;
    public const int MaxContactLength = 320;
    public const int MaxLocationLength = 200;
    public const int MaxSkillsLength = 2_000;
    public const int MaxResumeLength = 20_000;
    public const int MaxConsentEvidenceLength = 1_000;

    private StaffingCandidate() { }

    public StaffingCandidate(Guid ownerId, string name, DateTime utcNow)
    {
        if (ownerId == Guid.Empty) throw new ArgumentException("Owner is required.", nameof(ownerId));
        Id = Guid.NewGuid();
        OwnerId = ownerId;
        Name = Guard.Required(name, MaxNameLength, nameof(name));
        Consent = CandidateConsent.Pending;
        ShareableFields = CandidateField.None;
        CreatedAt = utcNow;
        UpdatedAt = utcNow;
    }

    public Guid Id { get; private set; }
    public Guid OwnerId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string? Headline { get; private set; }
    public string? Email { get; private set; }
    public string? Phone { get; private set; }
    public string? Location { get; private set; }
    /// <summary>Comma-separated, as entered.</summary>
    public string? Skills { get; private set; }
    public double? YearsExperience { get; private set; }
    public CandidateAvailability Availability { get; private set; }
    public int? NoticePeriodDays { get; private set; }
    public decimal? RateAmount { get; private set; }
    public string? RateCurrency { get; private set; }
    public RateUnit? RateUnit { get; private set; }
    public string? ResumeText { get; private set; }
    /// <summary>Increases each time the resume text changes, so a submission names the exact resume it shared.</summary>
    public int ResumeVersion { get; private set; }
    public CandidateConsent Consent { get; private set; }
    public DateTime? ConsentRecordedAt { get; private set; }
    public string? ConsentEvidence { get; private set; }
    public CandidateField ShareableFields { get; private set; }
    public bool NotifyByEmail { get; private set; }
    /// <summary>The day the resource joined the bench (journey day 1). Null: the record's creation date stands in.</summary>
    public DateTime? JoinedAt { get; private set; }
    /// <summary>The salesperson who looks after this resource, shown to the candidate.</summary>
    public string? AlignedSalesperson { get; private set; }
    /// <summary>Put forward first: a strong profile with a fitting open role.</summary>
    public bool Priority { get; private set; }
    public int Version { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }

    public const int MaxSalespersonLength = 120;

    /// <summary>Journey profile (2026-10-26). Not a profile edit: no new version, so consent and submissions are unaffected.</summary>
    public void SetJourneyProfile(DateTime? joinedAt, string? alignedSalesperson, bool priority)
    {
        JoinedAt = joinedAt is null ? null : DateTime.SpecifyKind(joinedAt.Value.Date, DateTimeKind.Utc);
        AlignedSalesperson = Guard.Optional(alignedSalesperson, MaxSalespersonLength, nameof(alignedSalesperson));
        Priority = priority;
    }

    public void Update(string name, string? headline, string? email, string? phone, string? location, string? skills,
        double? yearsExperience, CandidateAvailability availability, int? noticePeriodDays, decimal? rateAmount,
        string? rateCurrency, RateUnit? rateUnit, string? resumeText, bool notifyByEmail, DateTime utcNow)
    {
        if (!Enum.IsDefined(availability)) throw new ArgumentOutOfRangeException(nameof(availability));
        if (rateUnit is { } u && !Enum.IsDefined(u)) throw new ArgumentOutOfRangeException(nameof(rateUnit));
        if (yearsExperience is < 0 or > 60) throw new ArgumentException("Years of experience must be 0–60.", nameof(yearsExperience));
        if (noticePeriodDays is < 0 or > 365) throw new ArgumentException("Notice period must be 0–365 days.", nameof(noticePeriodDays));
        if (rateAmount is < 0) throw new ArgumentException("Rate cannot be negative.", nameof(rateAmount));
        if (rateAmount is not null && (string.IsNullOrWhiteSpace(rateCurrency) || rateUnit is null))
            throw new ArgumentException("A rate needs a currency and a unit.", nameof(rateAmount));
        Name = Guard.Required(name, MaxNameLength, nameof(name));
        Headline = Guard.Optional(headline, MaxHeadlineLength, nameof(headline));
        Email = Guard.Optional(email, MaxContactLength, nameof(email));
        Phone = Guard.Optional(phone, MaxContactLength, nameof(phone));
        Location = Guard.Optional(location, MaxLocationLength, nameof(location));
        Skills = Guard.Optional(skills, MaxSkillsLength, nameof(skills));
        YearsExperience = yearsExperience;
        Availability = availability;
        NoticePeriodDays = availability == CandidateAvailability.NoticePeriod ? noticePeriodDays : null;
        RateAmount = rateAmount;
        RateCurrency = rateAmount is null ? null : Guard.Optional(rateCurrency?.ToUpperInvariant(), 3, nameof(rateCurrency));
        RateUnit = rateAmount is null ? null : rateUnit;
        var resume = Guard.Optional(resumeText, MaxResumeLength, nameof(resumeText));
        if (resume != ResumeText) { ResumeText = resume; ResumeVersion++; }
        NotifyByEmail = notifyByEmail;
        Version++;
        UpdatedAt = utcNow;
    }

    /// <summary>Records what the candidate agreed to. Withdrawing consent also clears every shareable field.</summary>
    public void RecordConsent(CandidateConsent consent, CandidateField shareable, string? evidence, DateTime utcNow)
    {
        if (!Enum.IsDefined(consent)) throw new ArgumentOutOfRangeException(nameof(consent));
        if ((shareable & ~AllFields) != 0) throw new ArgumentOutOfRangeException(nameof(shareable));
        if (consent == CandidateConsent.Granted && string.IsNullOrWhiteSpace(evidence))
            throw new ArgumentException("Say how consent was given (e.g. \"email reply 8 Oct\").", nameof(evidence));
        Consent = consent;
        ShareableFields = consent == CandidateConsent.Granted ? shareable : CandidateField.None;
        ConsentEvidence = Guard.Optional(evidence, MaxConsentEvidenceLength, nameof(evidence));
        ConsentRecordedAt = utcNow;
        Version++;
        UpdatedAt = utcNow;
    }

    public const CandidateField AllFields = CandidateField.Name | CandidateField.Headline | CandidateField.Skills |
        CandidateField.Experience | CandidateField.Location | CandidateField.Availability | CandidateField.Rate |
        CandidateField.Email | CandidateField.Phone | CandidateField.Resume;
}
