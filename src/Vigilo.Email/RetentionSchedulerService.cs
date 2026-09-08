using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Vigilo.Core;

namespace Vigilo.Email;

/// <summary>
/// Runs local-data retention on a fixed wall-clock interval, independent of the scan loop.
/// Retention previously ran only after a full scheduled scan completed, so a long-running
/// scan — a local-model reprocess over the one-month scan window can occupy the loop for
/// hours — or a parked IDLE wait postponed the seven-day commercial-offer purge and the
/// other retention cutoffs for the whole session. The cadence is deliberately much tighter
/// than the day-granular retention cutoffs.
/// </summary>
internal sealed class RetentionSchedulerService(
    IServiceScopeFactory scopeFactory,
    ILogger<RetentionSchedulerService> logger,
    TimeSpan? initialDelay = null,
    TimeSpan? interval = null) : BackgroundService
{
    private static readonly TimeSpan DefaultInitialDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var effectiveInterval = interval ?? DefaultInterval;
        logger.LogInformation(
            "Retention scheduler started. InitialDelay={InitialDelay} Interval={Interval}",
            initialDelay ?? DefaultInitialDelay,
            effectiveInterval);

        try
        {
            await Task.Delay(initialDelay ?? DefaultInitialDelay, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                await RunRetentionOnceAsync(stoppingToken);
                await Task.Delay(effectiveInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Host shutdown through the stopping token.
        }
    }

    private async Task RunRetentionOnceAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var maintenance = scope.ServiceProvider.GetRequiredService<ILocalDataMaintenanceService>();
            var result = await maintenance.ApplyRetentionAsync(stoppingToken);
            logger.LogInformation(
                "Scheduled retention maintenance completed. DeletedMessages={DeletedMessages} DeletedProcessedRows={DeletedProcessedRows}",
                result.DeletedMessages,
                result.DeletedProcessedRows);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // A failed pass must not end the schedule; the next interval retries.
            logger.LogWarning(
                exception,
                "Scheduled retention maintenance failed. Interval={Interval}",
                interval ?? DefaultInterval);
        }
    }
}
