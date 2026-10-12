using Microsoft.Extensions.DependencyInjection;
using OpportunityPilot.Application.Agents;
using OpportunityPilot.Application.Accounts;
using OpportunityPilot.Application.Ai;
using OpportunityPilot.Application.Analytics;
using OpportunityPilot.Application.Applications;
using OpportunityPilot.Application.Approvals;
using OpportunityPilot.Application.Auth;
using OpportunityPilot.Application.Automation;
using OpportunityPilot.Application.Campaigns;
using OpportunityPilot.Application.Capabilities;
using OpportunityPilot.Application.Drafts;
using OpportunityPilot.Application.Sales;
using OpportunityPilot.Application.Staffing;
using OpportunityPilot.Application.Imports;
using OpportunityPilot.Application.Opportunities;
using OpportunityPilot.Application.Outreach;
using OpportunityPilot.Application.Profiles;
using OpportunityPilot.Application.Research;
using OpportunityPilot.Application.Research.Boards;
using OpportunityPilot.Application.Sources;
using OpportunityPilot.Application.Wellfound;

namespace OpportunityPilot.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<ProfileService>();
        services.AddScoped<Trends.TrendService>();
        services.AddScoped<AccountDataService>();
        services.AddScoped<AiService>();
        services.AddScoped<ApplicationService>();
        services.AddScoped<AgentKeyService>();
        services.AddScoped<GuestSessionService>();
        services.AddScoped<CampaignService>();
        services.AddScoped<SourceService>();
        services.AddScoped<ImportService>();
        services.AddScoped<ResearchService>();
        services.AddScoped<OpportunityService>();
        services.AddScoped<AgentResearchService>();
        services.AddScoped<ApprovalService>();
        services.AddScoped<AnalyticsService>();
        services.AddScoped<DraftService>();
        services.AddScoped<OutreachService>();
        services.AddScoped<SalesService>();
        services.AddScoped<UpworkOpportunityService>();
        services.AddScoped<ScheduleService>();
        services.AddScoped<StaffingService>();
        services.AddScoped<StaffingPipelineService>();
        services.AddScoped<Auth.SecurityAudit>();
        services.AddSingleton<Common.OperationalMetrics>();
        services.AddScoped<Common.RetentionService>();
        services.AddScoped<Outreach.ProviderGateway>();
        services.AddSingleton<Mail.TokenProtector>();
        services.AddScoped<Mail.MailboxService>();
        services.AddScoped<Capabilities.ProviderReadinessService>();
        services.AddScoped<WellfoundService>();
        services.AddScoped<JobBoardGatherer>();
        services.AddScoped<JobBoards.FeedSearchService>();
        services.AddScoped<IResearchRunner, ResearchRunner>();
        services.AddSingleton<CapabilityService>();
        return services;
    }
}
