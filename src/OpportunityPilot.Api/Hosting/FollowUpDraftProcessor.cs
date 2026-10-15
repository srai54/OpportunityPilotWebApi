using OpportunityPilot.Application.Outreach;

namespace OpportunityPilot.Api.Hosting;

/// <summary>Every 30 minutes: writes a follow-up draft for each due follow-up with no reply yet. Drafts only; sending still needs approval.</summary>
public sealed class FollowUpDraftProcessor(IServiceScopeFactory scopes, ILogger<FollowUpDraftProcessor> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(3), stoppingToken); } catch (OperationCanceledException) { return; }
        using var timer = new PeriodicTimer(Interval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<FollowUpDraftService>().DraftDueFollowUpsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogWarning(ex, "Follow-up draft pass failed; retrying on the next poll"); }
            try { if (!await timer.WaitForNextTickAsync(stoppingToken)) return; }
            catch (OperationCanceledException) { return; }
        }
    }
}
