using OpportunityPilot.Domain.Interviews;

namespace OpportunityPilot.Application.Interviews;

/// <param name="Title">The job title of the opportunity the interview belongs to.</param>
/// <param name="Organization">The company of that opportunity.</param>
public sealed record JobInterviewDto(
    Guid Id, Guid OpportunityId, string Title, string Organization, int Round, DateTime? ScheduledAt, string? MeetingUrl, string? Notes,
    JobInterviewStatus Status, int Version, DateTime CreatedAt, DateTime UpdatedAt);

/// <param name="Round">1–20; when left out on create, the next round for that job.</param>
/// <param name="ScheduledAt">UTC (ends in Z); empty while the time is not agreed.</param>
/// <param name="ExpectedVersion">Required on update.</param>
public sealed record SaveJobInterviewRequest(int? Round, DateTime? ScheduledAt, string? MeetingUrl, string? Notes, int? ExpectedVersion = null);

public sealed record JobInterviewStatusRequest(JobInterviewStatus Status, int? ExpectedVersion);
