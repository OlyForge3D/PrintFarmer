// <copyright file="BedClearAcknowledgementExpiryService.cs" company="PlaceholderCompany">
// SPDX-License-Identifier: AGPL-3.0-only
// </copyright>

using Farm.Infrastructure.Data;
using Farm.Infrastructure.Services.HostUpdates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.Queue;

/// <summary>
/// Proactively expires or invalidates exact-job bed-clear acknowledgements so clients receive
/// the durable lifecycle event without waiting for another acknowledgement or dispatch call.
/// </summary>
public sealed class BedClearAcknowledgementExpiryService(
    IServiceScopeFactory scopeFactory,
    ILogger<BedClearAcknowledgementExpiryService> logger,
    BedClearAcknowledgementExpiryMetrics metrics,
    BedClearAcknowledgementExpiryFenceFlag? hostUpdateFence = null,
    TimeProvider? timeProvider = null) : BackgroundService
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(15);

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (hostUpdateFence is not null &&
                    await hostUpdateFence.IsPauseRequestedAsync(stoppingToken).ConfigureAwait(false))
                {
                    await hostUpdateFence.AcknowledgePausedAsync(stoppingToken).ConfigureAwait(false);
                    await Task.Delay(TimeSpan.FromMilliseconds(250), _timeProvider, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                await ScanAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(
                    exception,
                    "Bed-clear acknowledgement lifecycle scan failed.");
            }

            if (await WaitForIntervalOrPauseAsync(stoppingToken).ConfigureAwait(false))
            {
                await hostUpdateFence!.AcknowledgePausedAsync(stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<bool> WaitForIntervalOrPauseAsync(CancellationToken stoppingToken)
    {
        DateTimeOffset until = _timeProvider.GetUtcNow() + ScanInterval;
        while (_timeProvider.GetUtcNow() < until)
        {
            if (hostUpdateFence is not null &&
                await hostUpdateFence.IsPauseRequestedAsync(stoppingToken).ConfigureAwait(false))
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), _timeProvider, stoppingToken).ConfigureAwait(false);
        }

        return false;
    }

    internal async Task ScanAsync(CancellationToken ct)
    {
        long scanStarted = _timeProvider.GetTimestamp();
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        IBedClearAcknowledgementService service =
            scope.ServiceProvider.GetRequiredService<IBedClearAcknowledgementService>();
        List<Guid> printerIds = await db.PrinterDispatchStates
            .AsNoTracking()
            .Where(state => state.AcknowledgedJobId != null)
            .OrderBy(state => state.PrinterId)
            .Select(state => state.PrinterId)
            .Take(100)
            .ToListAsync(ct);
        foreach (Guid printerId in printerIds)
        {
            await service.InvalidateStaleAcknowledgementsAsync(printerId, ct);
        }

        double elapsedMs = _timeProvider.GetElapsedTime(scanStarted).TotalMilliseconds;
        metrics.RecordScan(printerIds.Count, elapsedMs);
        logger.LogInformation(
            "Bed-clear acknowledgement scan pass: {ScannedCount} acknowledged printers, {ElapsedMs}ms.",
            printerIds.Count,
            elapsedMs);
    }
}
