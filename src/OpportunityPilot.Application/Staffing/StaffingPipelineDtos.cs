using OpportunityPilot.Domain.Staffing;

namespace OpportunityPilot.Application.Staffing;

public sealed record StaffingCandidateDto(
    Guid Id, string Name, string? Headline, string? Email, string? Phone, string? Location, string? Skills,
    double? YearsExperience, CandidateAvailability Availability, int? NoticePeriodDays, decimal? RateAmount,
    string? RateCurrency, RateUnit? RateUnit, bool HasResume, int ResumeVersion, CandidateConsent Consent,
    DateTime? ConsentRecordedAt, string? ConsentEvidence, IReadOnlyList<string> ShareableFields, bool NotifyByEmail,
    int Version, DateTime CreatedAt, DateTime UpdatedAt);

public sealed record SaveStaffingCandidateRequest(
    string Name, string? Headline, string? Email, string? Phone, string? Location, string? Skills, double? YearsExperience,
    CandidateAvailability Availability, int? NoticePeriodDays, decimal? RateAmount, string? RateCurrency, RateUnit? RateUnit,
    string? ResumeText, bool NotifyByEmail, int? ExpectedVersion);

public sealed record RecordConsentRequest(CandidateConsent Consent, IReadOnlyList<string>? ShareableFields, string? Evidence, int ExpectedVersion);

public sealed record StaffingSubmissionDto(
    Guid Id, Guid DealId, Guid CandidateId, string CandidateName, IReadOnlyList<string> SharedFields,
    IReadOnlyDictionary<string, string?> Snapshot, int CandidateVersion, int ResumeVersion, bool CandidateChangedSince,
    string? Note, SubmissionState State, DateTime? ApprovedAt, HandoffChannel? Channel, string? Receipt, DateTime? SentAt,
    int Version, DateTime CreatedAt, DateTime UpdatedAt);

public sealed record SaveSubmissionRequest(Guid CandidateId, IReadOnlyList<string> SharedFields, string? Note, int? ExpectedVersion);
public sealed record VersionRequest(int ExpectedVersion);
public sealed record MarkHandoffRequest(int ExpectedVersion, HandoffChannel Channel, string Receipt);

public sealed record StaffingInterviewDto(
    Guid Id, Guid DealId, Guid SubmissionId, int Round, InterviewState State, DateTime? ScheduledAt, string? TimeZone,
    int? DurationMinutes, InterviewMode? Mode, string? Location, string? CandidateNotes, string? InternalNotes,
    NotificationStatus CandidateNotification, DateTime? CandidateNotifiedAt, int Version, DateTime UpdatedAt);

public sealed record RequestInterviewRequest(Guid SubmissionId);
public sealed record ScheduleInterviewRequest(
    DateTime ScheduledAt, string TimeZone, int DurationMinutes, InterviewMode Mode, string? Location, string? CandidateNotes,
    string? InternalNotes, int ExpectedVersion);
public sealed record FinishInterviewRequest(InterviewState Outcome, int ExpectedVersion);
public sealed record NotifyCandidateRequest(NotificationStatus Status, int ExpectedVersion);

public sealed record StaffingFeedbackDto(
    Guid Id, Guid DealId, Guid SubmissionId, Guid? InterviewId, FeedbackSource Source, FeedbackDecision Decision,
    string Detail, bool SharedWithCandidate, DateTime RecordedAt);

public sealed record RecordFeedbackRequest(
    Guid SubmissionId, Guid? InterviewId, FeedbackSource Source, FeedbackDecision Decision, string Detail, bool SharedWithCandidate);

public sealed record StaffingOfferDto(
    Guid Id, Guid DealId, Guid SubmissionId, decimal? ClientRate, decimal? CandidatePay, string? Currency, RateUnit? Unit,
    DateOnly? StartDate, decimal? PlacementValue, OfferState State, ContractStatus Contract, string? ContractVersion,
    string? SignatureProvider, bool HasSignedDocument, PlacementOutcome Outcome, string? Notes, int Version, DateTime UpdatedAt);

public sealed record SaveOfferRequest(
    Guid SubmissionId, decimal? ClientRate, decimal? CandidatePay, string? Currency, RateUnit? Unit, DateOnly? StartDate,
    decimal? PlacementValue, string? Notes, int? ExpectedVersion);
public sealed record MoveOfferRequest(OfferState State, int ExpectedVersion);
public sealed record UpdateContractRequest(
    ContractStatus Status, string? ContractVersion, string? SignatureProvider, string? SignedDocumentReference, int ExpectedVersion);
public sealed record RecordOutcomeRequest(PlacementOutcome Outcome, int ExpectedVersion);

public sealed record RateCardLine(string Role, string? Seniority, RateUnit Unit, decimal Rate);

public sealed record StaffingRateCardDto(
    Guid Id, string Name, string Currency, IReadOnlyList<RateCardLine> Lines, string? Terms, DateOnly? ValidUntil,
    int CardVersion, RateCardStatus Status, int Version, DateTime UpdatedAt);

public sealed record SaveRateCardRequest(
    string Name, string Currency, IReadOnlyList<RateCardLine> Lines, string? Terms, DateOnly? ValidUntil, int? ExpectedVersion);
public sealed record RateCardStatusRequest(RateCardStatus Status, int ExpectedVersion);

public sealed record StaffingProposalDto(
    Guid Id, Guid DealId, Guid RateCardId, int RateCardVersion, string Title, string Currency, IReadOnlyList<RateCardLine> Lines,
    string? Terms, string? Body, DateOnly? ValidUntil, ProposalState State, DateTime? ApprovedAt, string? Receipt,
    DateTime? SentAt, int Version, DateTime UpdatedAt);

/// <summary>Lines default to the whole rate card; pass a subset to quote only some roles.</summary>
public sealed record CreateProposalRequest(Guid RateCardId, string Title, IReadOnlyList<RateCardLine>? Lines, string? Body, DateOnly? ValidUntil);
public sealed record EditProposalRequest(string Title, IReadOnlyList<RateCardLine> Lines, string? Terms, string? Body, DateOnly? ValidUntil, int ExpectedVersion);
public sealed record ClientAnswerRequest(bool Accepted, int ExpectedVersion);

/// <summary>One row of the sales interview tracker: the interview plus who, for which client and role, and what the candidate can see.</summary>
/// <param name="ProgressLinkActive">The candidate has a live progress link.</param>
/// <param name="ProgressShowsInterviews">That link shows interview details, so a status change here appears on it.</param>
public sealed record StaffingInterviewBoardRowDto(
    StaffingInterviewDto Interview, Guid CandidateId, string CandidateName, string? CandidateEmail, string DealTitle, string? Client,
    bool ProgressLinkActive, bool ProgressShowsInterviews);

public sealed record StaffingDealWorkDto(
    IReadOnlyList<StaffingSubmissionDto> Submissions, IReadOnlyList<StaffingInterviewDto> Interviews,
    IReadOnlyList<StaffingFeedbackDto> Feedback, IReadOnlyList<StaffingOfferDto> Offers, IReadOnlyList<StaffingProposalDto> Proposals);

public sealed record StageCount(StaffingDealStage Stage, int Current, int Reached);
public sealed record Conversion(string From, string To, int FromCount, int ToCount, double? Rate);
public sealed record SourceCount(StaffingDealSource Source, int Deals, int Won);
public sealed record MoneyTotal(string Currency, decimal Amount);

/// <summary>Every number is counted from stored deals, activities and records; nothing is estimated.</summary>
public sealed record StaffingKpisDto(
    int OpenDeals, int WonDeals, int LostDeals, IReadOnlyList<StageCount> Stages, IReadOnlyList<Conversion> Conversions,
    IReadOnlyList<SourceCount> Sources, IReadOnlyList<MoneyTotal> OpenPipelineValue, IReadOnlyList<MoneyTotal> PlacementValue,
    double? AverageDaysInStage, double? AverageHoursToReply, int OverdueNextActions,
    int Candidates, int CandidatesWithConsent, int CandidatesAvailable,
    int SubmissionsDraft, int SubmissionsApproved, int SubmissionsSent,
    int InterviewsUpcoming, int InterviewsCompleted, int InterviewsNotNotified,
    int OffersExtended, int OffersAccepted, int ContractsSigned, int Placements,
    int MessagesAwaitingSend, int RepliesToClassify, int MeetingsUpcoming, DateTime GeneratedAt);
