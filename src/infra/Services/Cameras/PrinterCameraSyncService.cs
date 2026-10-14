using System.Threading.Channels;
using Farm.Infrastructure.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.Cameras;

/// <summary>
/// Background service that keeps printer cameras in sync with what backend plugins report.
/// Backfills every printer on its first pass, re-syncs all printers on the configured interval
/// (<see cref="CameraSyncSettings.IntervalMinutes"/>), and syncs individual printers on demand when
/// they connect or are created (via <see cref="IPrinterCameraSyncTrigger"/>).
/// </summary>
public sealed class PrinterCameraSyncService(
    IServiceScopeFactory scopeFactory,
    ILogger<PrinterCameraSyncService> logger,
    TimeProvider? timeProvider = null) : BackgroundService, IPrinterCameraSyncTrigger
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);

    private readonly Channel<Guid> _requests = Channel.CreateBounded<Guid>(new BoundedChannelOptions(1024)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
    });

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <inheritdoc/>
    public void RequestSync(Guid printerId)
    {
        if (printerId != Guid.Empty)
        {
            _ = _requests.Writer.TryWrite(printerId);
        }
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, _time, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        DateTimeOffset nextFullSync = _time.GetUtcNow();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                CameraSyncSettings settings = GetSettings();
                if (_time.GetUtcNow() >= nextFullSync)
                {
                    if (settings.Enabled)
                    {
                        await RunFullSyncAsync(stoppingToken).ConfigureAwait(false);
                    }

                    nextFullSync = _time.GetUtcNow().AddMinutes(Math.Clamp(settings.IntervalMinutes, 1, 1440));
                }

                await ProcessRequestsUntilAsync(nextFullSync, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unexpected error in printer camera sync loop");
                nextFullSync = _time.GetUtcNow().AddMinutes(1);
            }
        }
    }

    private async Task ProcessRequestsUntilAsync(DateTimeOffset deadline, CancellationToken stoppingToken)
    {
        TimeSpan wait = deadline - _time.GetUtcNow();
        if (wait <= TimeSpan.Zero)
        {
            return;
        }

        using var timeout = new CancellationTokenSource(wait, _time);
        using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, timeout.Token);
        try
        {
            while (await _requests.Reader.WaitToReadAsync(waitCts.Token).ConfigureAwait(false))
            {
                var batch = new HashSet<Guid>();
                while (_requests.Reader.TryRead(out Guid printerId))
                {
                    _ = batch.Add(printerId);
                }

                if (!GetSettings().Enabled)
                {
                    continue;
                }

                foreach (Guid printerId in batch)
                {
                    await SyncPrinterAsync(printerId, stoppingToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            // Interval elapsed; time for the next full sync.
        }
    }

    private async Task RunFullSyncAsync(CancellationToken ct)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        IPrinterCameraSynchronizer synchronizer = scope.ServiceProvider.GetRequiredService<IPrinterCameraSynchronizer>();
        IReadOnlyList<PrinterCameraSyncResult> results = await synchronizer.SyncAllAsync(ct).ConfigureAwait(false);
        logger.LogDebug(
            "Printer camera sync pass complete: {PrinterCount} printers, {ChangedCount} changed",
            results.Count,
            results.Count(r => r.Changed));
    }

    private async Task SyncPrinterAsync(Guid printerId, CancellationToken ct)
    {
        try
        {
            using IServiceScope scope = scopeFactory.CreateScope();
            IPrinterCameraSynchronizer synchronizer = scope.ServiceProvider.GetRequiredService<IPrinterCameraSynchronizer>();
            _ = await synchronizer.SyncPrinterAsync(printerId, explicitRequest: false, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Triggered camera sync failed for printer {PrinterId}", printerId);
        }
    }

    private CameraSyncSettings GetSettings()
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        return scope.ServiceProvider.GetService<ISettingsService>()?.Get<CameraSyncSettings>() ?? new CameraSyncSettings();
    }
}
