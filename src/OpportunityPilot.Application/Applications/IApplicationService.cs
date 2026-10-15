using OpportunityPilot.Domain.Applications;

namespace OpportunityPilot.Application.Applications;

/// <summary>Job applications reported by the desktop agent: report, list, summary. Owner-scoped through <c>ICurrentUser</c>.</summary>
public interface IApplicationService
{
    Task<ApplicationReportResult> ReportAsync(ApplicationReportRequest request, CancellationToken ct);
    Task<ApplicationPageDto> ListAsync(ApplicationStatus? status, ApplicationPlatform? platform, int take, int skip, CancellationToken ct);
    Task<ApplicationSummaryDto> SummaryAsync(CancellationToken ct);
}
