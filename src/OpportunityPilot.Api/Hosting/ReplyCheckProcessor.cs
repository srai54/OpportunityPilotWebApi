using OpportunityPilot.Application.Mail;

namespace OpportunityPilot.Api.Hosting;

/// <summary>
/// Every 30 minutes: checks the inbox of each person with an app-password mailbox and an email sent in the last 30 days (at most 50 per
/// pass, continuing where the last pass stopped) and records replies, so follow-ups stop once someone answers. No signed-in user is
/// involved: the owner is passed explicitly.
/// </summary>
public sealed class ReplyCheckProcessor(IServiceScopeFactory scopes, ILogger<ReplyCheckProcessor> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Guid? after = null;
        try { await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken); } catch (OperationCanceledException) { return; }
        using var timer = new PeriodicTimer(Interval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var (last, replies) = await scope.ServiceProvider.GetRequiredService<ReplyCheckService>().CheckDueOwnersAsync(after, stoppingToken);
                after = last;
                if (replies > 0) logger.LogInformation("Reply check recorded {Count} replies", replies);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogWarning(ex, "Reply check pass failed; retrying on the next poll"); }
            try { if (!await timer.WaitForNextTickAsync(stoppingToken)) return; }
            catch (OperationCanceledException) { return; }
        }
    }
}
