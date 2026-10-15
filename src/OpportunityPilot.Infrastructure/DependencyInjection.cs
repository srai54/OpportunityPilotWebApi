using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpportunityPilot.Application.Abstractions;
using OpportunityPilot.Application.Ai;
using OpportunityPilot.Application.Configuration;
using OpportunityPilot.Application.Research;
using OpportunityPilot.Application.JobBoards;
using OpportunityPilot.Application.Wellfound;
using OpportunityPilot.Infrastructure.Persistence;
using OpportunityPilot.Infrastructure.Ai;
using OpportunityPilot.Infrastructure.Research;

namespace OpportunityPilot.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration, SetupState setup)
    {
        services.AddResearchInfrastructure(configuration);
        services.AddHttpClient<GeminiLlmClient>(c =>
        {
            c.BaseAddress = new Uri("https://generativelanguage.googleapis.com/");
            c.Timeout = Timeout.InfiniteTimeSpan;
        });
        services.AddHttpClient<GroqLlmClient>(c =>
        {
            c.BaseAddress = new Uri("https://api.groq.com/");
            c.Timeout = Timeout.InfiniteTimeSpan;
        });
        services.AddTransient<ILlmClient, RoutingLlmClient>();
        services.AddHttpClient<Integrations.ConnectorHttp>(Integrations.ConnectorHttp.ClientName, c => c.Timeout = TimeSpan.FromSeconds(30))
            .ConfigurePrimaryHttpMessageHandler(Integrations.ConnectorHttp.Handler);
        // Requests made with a plugin connection go through the managed-connection platform's proxy; the rest go out directly.
        services.AddTransient<Application.Integrations.IConnectorHttp>(sp => new Application.Integrations.PluginConnectorHttp(
            sp.GetRequiredService<Integrations.ConnectorHttp>(), sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<Application.Integrations.PluginOptions>>()));
        return services.AddPersistence(configuration, setup);
    }

    /// <summary>Safe outbound fetching and content parsing for research. Works the same with or without a database.</summary>
    public static IServiceCollection AddResearchInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ResearchOptions>(configuration.GetSection(ResearchOptions.Section));
        // Not configurable on purpose: no setting can let the fetcher reach private addresses.
        services.TryAddSingleton<IFetchAddressPolicy, StrictFetchAddressPolicy>();
        services.AddSingleton<FetchConcurrency>();
        services.AddSingleton<IContentParser, ContentParser>();
        services.AddScoped<IWellfoundPublicJobReader, WellfoundPublicJobReader>();
        // 130 s: a Naukri search runs an Apify scraper synchronously (usually 20–60 s); JSearch answers in a few seconds.
        services.AddHttpClient<IJobBoardSearch, JsearchBoardClient>(c => c.Timeout = TimeSpan.FromSeconds(130));
        services.AddHttpClient<IRapidApiBoardSearch, RapidApiBoardClient>(c => c.Timeout = TimeSpan.FromSeconds(30));
        services.AddHttpClient<Application.Mail.IMailTransport, Mail.MailTransport>(c => c.Timeout = TimeSpan.FromSeconds(30));
        services.AddScoped<Application.Mail.IMailboxReader, Mail.ImapMailboxReader>();
        services.AddHttpClient<Application.Trends.ITrendSource, JsearchTrendClient>(c => c.Timeout = TimeSpan.FromSeconds(30));
        services.AddHttpClient<IWebFetcher, SafeFetcher>(SafeFetcher.ClientName, c => c.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(sp => SafeFetcher.CreateHandler(sp.GetRequiredService<IFetchAddressPolicy>()))
            .SetHandlerLifetime(TimeSpan.FromMinutes(5));
        return services;
    }

    /// <summary>
    /// The connection string decides when it is unambiguous (a postgres:// URI or a Postgres host), so a mistyped
    /// Database__Provider cannot take the service down; otherwise the setting decides, case-insensitively.
    /// </summary>
    public static string ResolveProvider(string? configured, string? connectionString)
    {
        var cs = connectionString?.Trim() ?? "";
        if (cs.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) || cs.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase)
            || cs.Contains("supabase.co", StringComparison.OrdinalIgnoreCase) || cs.Contains("supabase.com", StringComparison.OrdinalIgnoreCase))
            return "Postgres";
        var value = configured?.Trim();
        if (string.Equals(value, "Postgres", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "Postgresql", StringComparison.OrdinalIgnoreCase)) return "Postgres";
        if (string.IsNullOrEmpty(value) || string.Equals(value, "SqlServer", StringComparison.OrdinalIgnoreCase)) return "SqlServer";
        throw new InvalidOperationException($"Setup required: Database:Provider must be SqlServer or Postgres (got '{value}').");
    }

    private static IServiceCollection AddPersistence(this IServiceCollection services, IConfiguration configuration, SetupState setup)
    {
        var connectionString = configuration["ConnectionStrings:Main"];
        var provider = ResolveProvider(configuration[$"{DatabaseOptions.Section}:{nameof(DatabaseOptions.Provider)}"], connectionString);
        // Report the provider actually in use, not a raw (possibly mistyped) setting.
        services.PostConfigure<DatabaseOptions>(o => o.Provider = provider);
        if (!string.IsNullOrWhiteSpace(connectionString) && provider == "Postgres")
        {
            try { connectionString = PostgresConnectionString.Normalize(connectionString); }
            catch (InvalidOperationException) { connectionString = null; setup.DatabaseMissing("ConnectionStrings__Main is not a valid connection string, so data is kept in memory and resets when the server restarts."); }
        }
        else if (string.IsNullOrWhiteSpace(connectionString))
        {
            connectionString = null;
            setup.DatabaseMissing("ConnectionStrings__Main is not set, so data is kept in memory and resets when the server restarts.");
        }

        if (connectionString is null)
        {
            // Demo mode: the app works end to end, but nothing survives a restart.
            services.AddDbContext<InMemoryAppDbContext>(o => o.UseInMemoryDatabase("opportunitypilot-demo"));
            services.AddScoped<AppDbContext>(sp => sp.GetRequiredService<InMemoryAppDbContext>());
            services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<AppDbContext>());
            services.AddScoped<Application.Common.IDatabaseDiagnostics, Persistence.DatabaseDiagnosticsProbe>();
            return services.AddRepositories();
        }

        switch (provider)
        {
            case "SqlServer":
                services.AddDbContext<SqlServerAppDbContext>(o => o.UseSqlServer(connectionString,
                    s => s.MigrationsHistoryTable("__EFMigrationsHistory", AppDbContext.Schema)));
                services.AddScoped<AppDbContext>(sp => sp.GetRequiredService<SqlServerAppDbContext>());
                break;
            case "Postgres":
                services.AddDbContext<PostgresAppDbContext>(o => o.UseNpgsql(connectionString,
                    s => s.MigrationsHistoryTable("__EFMigrationsHistory", AppDbContext.Schema)));
                services.AddScoped<AppDbContext>(sp => sp.GetRequiredService<PostgresAppDbContext>());
                break;
        }

        services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<AppDbContext>());
        services.AddScoped<Application.Common.IDatabaseDiagnostics, Persistence.DatabaseDiagnosticsProbe>();
        return services.AddRepositories();
    }

    /// <summary>EF repositories behind the Application's <c>I…Repository</c> interfaces; they share the scoped <see cref="AppDbContext"/>.</summary>
    private static IServiceCollection AddRepositories(this IServiceCollection services)
    {
        services.AddScoped<Application.Campaigns.ICampaignRepository, Persistence.Repositories.CampaignRepository>();
        services.AddScoped<Application.Opportunities.IOpportunityRepository, Persistence.Repositories.OpportunityRepository>();
        services.AddScoped<Application.Profiles.IProfileRepository, Persistence.Repositories.ProfileRepository>();
        services.AddScoped<Application.Drafts.IDraftRepository, Persistence.Repositories.DraftRepository>();
        services.AddScoped<Application.Sources.ISourceRepository, Persistence.Repositories.SourceRepository>();
        services.AddScoped<Application.Applications.IApplicationRepository, Persistence.Repositories.ApplicationRepository>();
        services.AddScoped<Application.Approvals.IApprovalRepository, Persistence.Repositories.ApprovalRepository>();
        services.AddScoped<Application.Staffing.IStaffingRepository, Persistence.Repositories.StaffingRepository>();
        return services;
    }
}
