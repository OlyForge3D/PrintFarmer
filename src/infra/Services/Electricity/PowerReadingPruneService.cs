using Farm.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.Electricity;

/// <summary>
/// Background service that deletes <see cref="Domain.PowerReading"/> rows older than 90 days
/// (the hot-retention window). Runs once per day.
/// Shutdown cancellation during a prune pass is not reported as an error.
/// </summary>
public class PowerReadingPruneService(
    IServiceScopeFactory scopeFactory,
    ILogger<PowerReadingPruneService> logger,
    Farm.Infrastructure.Services.HostUpdates.PowerReadingPruneFenceFlag? hostUpdateFence = null,
    TimeProvider? timeProvider = null) : BackgroundService
{
    private const int RetentionDays = 90;
    private readonly TimeSpan _interval = TimeSpan.FromHours(24);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Host-update fence (issue #2663): skip this pass's delete entirely while a
                // coordinated backup/migration is in progress, and acknowledge quiescence to
                // the fence coordinator rather than racing it with an in-flight ExecuteDeleteAsync.
                if (hostUpdateFence is not null && await hostUpdateFence.IsPauseRequestedAsync(stoppingToken))
                {
                    await hostUpdateFence.AcknowledgePausedAsync(stoppingToken);
                    await Task.Delay(TimeSpan.FromMilliseconds(250), _timeProvider, stoppingToken).ConfigureAwait(false);
                }
                else
                {
                    await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
                    AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                    DateTime cutoff = _timeProvider.GetUtcNow().UtcDateTime.AddDays(-RetentionDays);
                    int deleted = await db.PowerReadings
                        .Where(r => r.RecordedAt < cutoff)
                        .ExecuteDeleteAsync(stoppingToken);

                    if (deleted > 0)
                    {
                        logger.LogInformation(
                            "PowerReadingPruneService: deleted {Count} readings older than {Days} days",
                            deleted,
                            RetentionDays);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "PowerReadingPruneService: error during prune");
            }

            if (await WaitForIntervalOrPauseAsync(stoppingToken).ConfigureAwait(false))
            {
                await hostUpdateFence!.AcknowledgePausedAsync(stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<bool> WaitForIntervalOrPauseAsync(CancellationToken stoppingToken)
    {
        DateTimeOffset until = _timeProvider.GetUtcNow() + _interval;
        while (_timeProvider.GetUtcNow() < until)
        {
            if (hostUpdateFence is not null && await hostUpdateFence.IsPauseRequestedAsync(stoppingToken).ConfigureAwait(false))
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), _timeProvider, stoppingToken).ConfigureAwait(false);
        }

        return false;
    }
}
