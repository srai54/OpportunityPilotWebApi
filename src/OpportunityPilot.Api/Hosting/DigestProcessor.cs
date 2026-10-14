using OpportunityPilot.Application.Integrations;

namespace OpportunityPilot.Api.Hosting;

/// <summary>Every 15 minutes: posts the daily summary to connected Slack, Teams or webhooks once the digest hour has passed (once a day each).</summary>
public sealed class DigestProcessor(IServiceScopeFactory scopes, ILogger<DigestProcessor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15));
        while (!stoppingToken.IsCancellationRequested)
        {
            try { if (!await timer.WaitForNextTickAsync(stoppingToken)) return; }
            catch (OperationCanceledException) { return; }
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var sent = await scope.ServiceProvider.GetRequiredService<IntegrationNotifier>().SendDigestsAsync(stoppingToken);
                if (sent > 0) logger.LogInformation("Daily summary posted to {Count} connections", sent);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogWarning(ex, "Digest processor failed; retrying on the next poll"); }
        }
    }
}
