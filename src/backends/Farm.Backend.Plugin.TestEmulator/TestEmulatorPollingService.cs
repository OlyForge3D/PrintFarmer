using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.SignalR;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Farm.Backend.Plugin.TestEmulator;

/// <summary>
/// Background service that polls the TestEmulatorStateManager and broadcasts
/// status updates via SignalR, following the PrusaLinkPollingService pattern.
/// </summary>
public sealed class TestEmulatorPollingService(
    IHubContext<PrinterHub> hub,
    ILogger<TestEmulatorPollingService> logger,
    IPrinterStatusCacheWriter statusCacheWriter,
    TestEmulatorStateManager stateManager) : IHostedService, IDisposable
{
    private static readonly TimeSpan PollingInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan CompleteDwellTime = TimeSpan.FromSeconds(5);

    private readonly CancellationTokenSource _cts = new();
    private Task? _mainLoop;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("TestEmulatorPollingService starting");
#pragma warning disable VSTHRD003
        _ = _mainLoop = Task.Run(() => RunAsync(_cts.Token), _cts.Token);
#pragma warning restore VSTHRD003
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("TestEmulatorPollingService stopping");
        try
        {
            await _cts.CancelAsync();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed — safe to ignore
        }

        try
        {
            if (_mainLoop is not null)
            {
#pragma warning disable VSTHRD003
                await _mainLoop;
#pragma warning restore VSTHRD003
            }
        }
        catch (OperationCanceledException)
        {
            // Expected
        }
    }

    public void Dispose()
    {
        _cts?.Dispose();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        logger.LogInformation("TestEmulatorPollingService main loop started");
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    foreach (Guid printerId in stateManager.GetAllPrinterIds())
                    {
                        await TickPrinterAsync(printerId, ct);
                    }

                    await Task.Delay(PollingInterval, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error in TestEmulatorPollingService loop");
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("TestEmulatorPollingService main loop cancelled");
        }
    }

    private async Task TickPrinterAsync(Guid printerId, CancellationToken ct)
    {
        EmulatedPrinterState? state = stateManager.GetState(printerId);
        if (state is null)
        {
            return;
        }

        // Advance the state machine
        AdvanceStateMachine(printerId, state);

        bool isOnline = state.State != EmulatorPrinterState.Offline;
        string? rawState = state.State switch
        {
            EmulatorPrinterState.Idle => "idle",
            EmulatorPrinterState.Printing => "printing",
            EmulatorPrinterState.Paused => "paused",
            EmulatorPrinterState.Complete => "complete",
            EmulatorPrinterState.Error => "error",
            EmulatorPrinterState.Offline => null,
            _ => null
        };

        double? progress = state.State is EmulatorPrinterState.Printing or EmulatorPrinterState.Paused
            ? Math.Round(state.Progress, 1)
            : null;

        string? jobName = state.State is EmulatorPrinterState.Printing or EmulatorPrinterState.Paused
            ? state.JobName
            : null;

        bool isHeating = state.State is EmulatorPrinterState.Printing or EmulatorPrinterState.Paused;
        double? hotendTarget = isHeating ? EmulatedPrinterState.TargetHotendTemp : null;
        double? bedTarget = isHeating ? EmulatedPrinterState.TargetBedTemp : null;

        var update = new PrinterStatusDto(
            Id: printerId,
            IsOnline: isOnline,
            State: PrinterStateNormalizer.NormalizeState(rawState),
            Progress: progress,
            JobName: jobName,
            ThumbnailUrl: null,
            CameraStreamUrl: null,
            CameraSnapshotUrl: null,
            X: null,
            Y: null,
            Z: null,
            HotendTemp: Math.Round(state.GetHotendTemp(), 1),
            BedTemp: Math.Round(state.GetBedTemp(), 1),
            HotendTarget: hotendTarget,
            BedTarget: bedTarget,
            SpoolInfo: null);

        statusCacheWriter.UpdateStatus(update);
        await hub.Clients.All.SendAsync("printerupdated", update.WithNormalizedFileName(), ct);
    }

    /// <summary>
    /// Advances the state machine for a single printer: progress increments, completion, reset.
    /// </summary>
    private void AdvanceStateMachine(Guid printerId, EmulatedPrinterState state)
    {
        switch (state.State)
        {
            case EmulatorPrinterState.Printing when state.PrintStartedAt.HasValue:
            {
                double elapsed = (DateTime.UtcNow - state.PrintStartedAt.Value).TotalSeconds;
                double newProgress = Math.Min((elapsed / state.PrintDurationSeconds) * 100.0, 100.0);
                state.Progress = newProgress;

                if (newProgress >= 100.0)
                {
                    stateManager.MarkComplete(printerId);
                    logger.LogInformation("TestEmulator printer {PrinterId} completed print", printerId);
                }

                break;
            }

            case EmulatorPrinterState.Complete when state.CompletedAt.HasValue:
            {
                if (DateTime.UtcNow - state.CompletedAt.Value >= CompleteDwellTime)
                {
                    stateManager.ResetToIdle(printerId);
                    logger.LogInformation("TestEmulator printer {PrinterId} reset to Idle after completion dwell", printerId);
                }

                break;
            }
        }
    }
}
