using OpportunityPilot.Domain.Staffing;

namespace OpportunityPilot.Application.Staffing;

public sealed record StaffingContactDto(
    Guid Id, Guid AccountId, string Name, string? Title, string? Email, bool EmailVerified, string? LinkedInUrl,
    string? Evidence, int Version, DateTime CreatedAt, DateTime UpdatedAt, string? Phone = null);

public sealed record StaffingAccountDto(
    Guid Id, string Name, string? Domain, string? Industry, string? Location, StaffingAccountSource Source,
    string? SourceReference, int Version, IReadOnlyList<StaffingContactDto> Contacts, DateTime CreatedAt, DateTime UpdatedAt);

public sealed record StaffingDealActivityDto(
    Guid Id, StaffingDealActivityType Type, string Detail, DateTime OccurredAt);

public sealed record StaffingDealDto(
    Guid Id, Guid AccountId, Guid? ContactId, string Title, StaffingDealSource Source, string? ExternalReference,
    decimal? EstimatedValue, string? Currency, StaffingDealStage Stage, StaffingDealStage? StageBeforeHold,
    string? NextAction, DateTime? NextActionAt, int Version, IReadOnlyList<StaffingDealActivityDto> Activities,
    DateTime CreatedAt, DateTime UpdatedAt, string? JobDescription = null);

public sealed record CreateStaffingAccountRequest(
    string Name, StaffingAccountSource Source, string? Domain, string? Industry, string? Location, string? SourceReference);

public sealed record CreateStaffingContactRequest(
    string Name, string? Title, string? Email, bool EmailVerified, string? LinkedInUrl, string? Evidence, string? Phone = null);

/// <summary>Replaces a contact's editable fields; <c>ExpectedVersion</c> guards against overwriting a newer edit.</summary>
public sealed record UpdateStaffingContactRequest(
    string Name, string? Title, string? Email, bool EmailVerified, string? LinkedInUrl, string? Evidence, string? Phone,
    int ExpectedVersion);

public sealed record CreateStaffingDealRequest(
    Guid? ContactId, string Title, StaffingDealSource Source, string? ExternalReference, decimal? EstimatedValue,
    string? Currency, string? NextAction, DateTime? NextActionAt);

public sealed record UpdateStaffingDealRequest(
    string Title, string? ExternalReference, decimal? EstimatedValue, string? Currency, string? NextAction,
    DateTime? NextActionAt, int ExpectedVersion);

public sealed record MoveStaffingDealRequest(StaffingDealStage Stage, int ExpectedVersion);
public sealed record AddStaffingDealNoteRequest(string Detail);

public enum IdentityConfidence { High, Medium, Low }

/// <summary>X3: a found company turned into a staffing lead. Confidence says how sure the account match is.</summary>
/// <param name="Confidence">High: matched or created by website domain. Medium: matched an existing account by name. Low: new account by name only.</param>
public sealed record PromotedLeadDto(StaffingDealDto Deal, Guid AccountId, bool AccountReused, bool DealReused, IdentityConfidence Confidence, string? EvidenceUrl);
