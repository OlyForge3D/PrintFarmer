using System.Collections.Concurrent;
using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Printers;
using Farm.Infrastructure.Repositories.UnitOfWork;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.SignalR;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Farm.Backend.Plugin.Sdcp;

/// <summary>
/// Background service for polling SDCP printers and broadcasting status updates via SignalR.
/// SDCP printers use WebSocket-based communication but don't support real-time subscriptions,
/// so we poll them periodically for status updates.
/// </summary>
public sealed class SdcpPollingService(
    IHubContext<PrinterHub> hub,
    IServiceScopeFactory scopeFactory,
    ILogger<SdcpPollingService> logger,
    IPrinterStatusCacheWriter statusCacheWriter) : IHostedService, IDisposable, IPrinterConnectionHealthProvider
{
    private readonly ILogger<SdcpPollingService> _logger = logger;
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly IHubContext<PrinterHub> _hub = hub;
    private readonly IPrinterStatusCacheWriter _statusCacheWriter = statusCacheWriter;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<Guid, PrinterPollingState> _printerStates = new();
    private readonly ConcurrentDictionary<Guid, Task> _pollingLoops = new();
    private readonly ConcurrentDictionary<Guid, PrinterConnectionHealth> _connectionHealth = new();

    // Polling interval for SDCP printers (same as PrusaLink)
    private static readonly TimeSpan PollingInterval = TimeSpan.FromSeconds(5);

    private Task? _mainLoop;

    /// <summary>
    /// Persistent state for each SDCP printer to track changes and avoid redundant updates.
    /// </summary>
    private sealed class PrinterPollingState
    {
        public Guid PrinterId { get; set; }

        public string? LastKnownState { get; set; }

        /// <summary>
        /// Previous state before the last update, used for detecting print completion transitions.
        /// </summary>
        public string? PreviousState { get; set; }

        public double? LastKnownProgress { get; set; }

        public string? LastKnownJobName { get; set; }

        public bool LastKnownIsOnline { get; set; }

        public DateTime LastPollTime { get; set; }

        public int ConsecutiveFailures { get; set; }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("SdcpPollingService starting");
#pragma warning disable VSTHRD003 // Avoid awaiting or returning a Task representing work that was not started within this context
        _ = _mainLoop = Task.Run(() => RunAsync(_cts.Token), _cts.Token);
#pragma warning restore VSTHRD003
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("SdcpPollingService stopping");
        try
        {
            await _cts.CancelAsync();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed – safe to ignore during shutdown
        }

        try
        {
            if (_mainLoop is not null)
            {
#pragma warning disable VSTHRD003 // Task not started in async context is expected here
                await _mainLoop;
#pragma warning restore VSTHRD003
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when service is stopped
        }
    }

    public void Dispose()
    {
        _cts?.Dispose();
        _pollingLoops.Clear();
    }

    /// <summary>
    /// Main loop that continuously monitors and polls all SDCP printers.
    /// </summary>
    private async Task RunAsync(CancellationToken ct)
    {
        _logger.LogInformation("SdcpPollingService main loop started");
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    // Get list of SDCP printers from database
                    List<Guid> printerIds = await GetSdcpPrinterIdsAsync(ct);
                    _logger.LogDebug("SdcpPollingService: Found {PrinterIdsCount} SDCP printers", printerIds.Count);

                    // Ensure polling loops exist for all SDCP printers
                    foreach (Guid id in printerIds)
                    {
                        if (!_pollingLoops.ContainsKey(id))
                        {
#pragma warning disable S6612 // Use the loop variable instead of capturing
                            var pollingLoop = Task.Run(() => PollPrinterAsync(id, ct), ct);
#pragma warning restore S6612
                            _pollingLoops.TryAdd(id, pollingLoop);
                            _logger.LogDebug("Started polling loop for SDCP printer {Id}", id);
                        }
                    }

                    // Remove polling loops for printers that are no longer SDCP
                    var inactiveIds = _pollingLoops.Keys.Except(printerIds).ToList();
                    foreach (Guid printerId in inactiveIds)
                    {
                        _pollingLoops.TryRemove(printerId, out _);
                        _printerStates.TryRemove(printerId, out _);
                        _logger.LogDebug("Stopped polling for printer {PrinterId}", printerId);
                    }

                    // Check every 30 seconds for printer list changes
                    await Task.Delay(TimeSpan.FromSeconds(30), ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in SdcpPollingService main loop");
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("SdcpPollingService main loop cancelled");
        }
    }

    /// <summary>
    /// Polls a single SDCP printer at regular intervals.
    /// </summary>
    private async Task PollPrinterAsync(Guid printerId, CancellationToken ct)
    {
#pragma warning disable S6612 // Capturing printerId in lambda is intentional and safe
        PrinterPollingState state = _printerStates.GetOrAdd(printerId, _ => new PrinterPollingState { PrinterId = printerId, LastKnownIsOnline = false });
#pragma warning restore S6612

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Get printer details
                Printer? printer = await GetPrinterAsync(printerId, ct);
                if (printer?.Backend != (int)PrinterBackend.SDCP)
                {
                    // Printer is no longer SDCP, remove from polling
                    _pollingLoops.TryRemove(printerId, out _);
                    _printerStates.TryRemove(printerId, out _);
                    return;
                }

                // Poll the printer
                try
                {
                    bool wasOnline = state.LastKnownIsOnline;
                    int previousFailures = state.ConsecutiveFailures;

                    // Get the SDCP client from a scoped context
                    // (scoped services cannot be injected directly into singletons)
                    using IServiceScope scope = _scopeFactory.CreateScope();
                    ISdcpClient sdcpClient = scope.ServiceProvider.GetRequiredService<ISdcpClient>();

                    // SDCP uses BackendUrl which combines ServerUrl with BackendPort
                    // The GetCompositeStatusAsync expects the base URL (which it converts to WebSocket URL internally)
                    PrinterCompositeStatus status = await sdcpClient.GetCompositeStatusAsync(
                        printer.BackendUrl,
                        ct);

                    if (!wasOnline && status.IsOnline)
                    {
                        _logger.LogInformation("SDCP printer recovered to Online. PrinterId={PrinterId}, BackendUrl={BackendUrl}, PreviousFailures={PreviousFailures}",
                            printerId, printer.BackendUrl, previousFailures);
                    }
                    else if (previousFailures > 0)
                    {
                        _logger.LogDebug("SDCP poll succeeded after failures. PrinterId={PrinterId}, BackendUrl={BackendUrl}, PreviousFailures={PreviousFailures}",
                            printerId, printer.BackendUrl, previousFailures);
                    }

                    _logger.LogDebug("SDCP {PrinterId}: Got status - Online={StatusIsOnline}, State={StatusState}, Progress={StatusProgress}, JobName={StatusJobName}", printerId, status.IsOnline, status.State, status.Progress, status.JobName);

                    // Check if any values changed
                    // Use tolerance for float progress comparison
#pragma warning disable S1244 // Using explicit tolerance (0.01) for float comparison is appropriate here
                    bool progressChanged = state.LastKnownProgress == null || status.Progress == null
                        ? state.LastKnownProgress != status.Progress
                        : Math.Abs(state.LastKnownProgress.Value - status.Progress.Value) > 0.01;
#pragma warning restore S1244
                    bool hasChanges = state.LastKnownIsOnline != status.IsOnline
                        || state.LastKnownState != status.State
                        || progressChanged
                        || state.LastKnownJobName != status.JobName;

                    // Check for state transition from printing to idle for job completion sync
                    string? previousState = state.LastKnownState;
                    bool stateChanged = status.State != previousState;

                    // Update state tracking (including PreviousState for transition detection)
                    state.PreviousState = previousState;
                    state.LastKnownIsOnline = status.IsOnline;
                    state.LastKnownState = status.State;
                    state.LastKnownProgress = status.Progress;
                    state.LastKnownJobName = status.JobName;
                    state.ConsecutiveFailures = 0;

                    // Track connection health
                    if (status.IsOnline && (!wasOnline || previousFailures > 0))
                    {
                        RecordHealthTransition(printerId, printer.Name ?? printerId.ToString(), PrinterConnectionState.Connected, "Poll successful");
                    }

                    // Check for print completion/failure transitions
                    if (stateChanged && previousState != null && status.State != null)
                    {
                        await CheckAndSyncJobCompletionAsync(printerId, previousState, status.State, ct);
                    }

                    // Broadcast update via SignalR using PrinterStatusDto
                    var update = new PrinterStatusDto(
                        Id: printerId,
                        IsOnline: status.IsOnline,
                        State: PrinterStateNormalizer.NormalizeState(status.State),
                        Progress: status.Progress,
                        JobName: status.JobName,
                        ThumbnailUrl: status.ThumbnailUrl,
                        CameraStreamUrl: status.CameraStreamUrl,
                        CameraSnapshotUrl: null,
                        X: status.X,
                        Y: status.Y,
                        Z: status.Z,
                        HotendTemp: status.HotendTemp,
                        BedTemp: status.BedTemp,
                        HotendTarget: status.HotendTarget,
                        BedTarget: status.BedTarget,
                        SpoolInfo: null);

                    // Update cache before broadcasting to clients
                    _statusCacheWriter.UpdateStatus(update);

                    await _hub.Clients.All.SendAsync("printerupdated", update, ct);

                    state.LastPollTime = DateTime.UtcNow;
                }
                catch (Exception ex)
                {
                    state.ConsecutiveFailures++;
                    _logger.LogDebug(ex, "Failed to poll SDCP printer {PrinterId} (attempt {StateConsecutiveFailures})", printerId, state.ConsecutiveFailures);

                    _logger.LogDebug(ex, "SDCP poll failed. PrinterId={PrinterId}, BackendUrl={BackendUrl}, Attempt={Attempt}",
                        printerId, printer?.BackendUrl, state.ConsecutiveFailures);

                    // Track reconnecting state on first failure
                    if (state.ConsecutiveFailures == 1)
                    {
                        RecordHealthTransition(printerId, printer?.Name ?? printerId.ToString(), PrinterConnectionState.Reconnecting, $"Poll failed: {ex.GetType().Name}");
                    }

                    // After 3 consecutive failures, mark as offline
                    if (state.ConsecutiveFailures >= 3 && state.LastKnownIsOnline)
                    {
                        _logger.LogWarning("SDCP printer {PrinterId} marked offline after {StateConsecutiveFailures} failures", printerId, state.ConsecutiveFailures);
                        state.LastKnownIsOnline = false;

                        _logger.LogWarning("SDCP printer marked Offline after consecutive failures. PrinterId={PrinterId}, BackendUrl={BackendUrl}, Failures={Failures}",
                            printerId, printer?.BackendUrl, state.ConsecutiveFailures);

                        RecordHealthTransition(printerId, printer?.Name ?? printerId.ToString(), PrinterConnectionState.Offline, $"Failed {state.ConsecutiveFailures} consecutive times");

                        var offlineUpdate = new PrinterStatusDto(
                            Id: printerId,
                            IsOnline: false,
                            State: null,
                            Progress: null,
                            JobName: null,
                            ThumbnailUrl: null,
                            CameraStreamUrl: null,
                            CameraSnapshotUrl: null,
                            X: null,
                            Y: null,
                            Z: null,
                            HotendTemp: null,
                            BedTemp: null,
                            HotendTarget: null,
                            BedTarget: null,
                            SpoolInfo: null);

                        // Update cache before broadcasting to clients
                        _statusCacheWriter.UpdateStatus(offlineUpdate);

                        await _hub.Clients.All.SendAsync("printerupdated", offlineUpdate, ct);
                    }
                }

                state.LastPollTime = DateTime.UtcNow;
                await Task.Delay(PollingInterval, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error polling SDCP printer {PrinterId}", printerId);
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }
    }

    /// <summary>
    /// Gets the list of all SDCP printer IDs from the database.
    /// </summary>
    private async Task<List<Guid>> GetSdcpPrinterIdsAsync(CancellationToken ct)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<Farm.Infrastructure.Repositories.UnitOfWork.IUnitOfWork>();
        List<Printer> printers = await unitOfWork.Printers.GetByBackendAsync(PrinterBackend.SDCP, ct);
        return printers.Select(p => p.Id).ToList();
    }

    /// <summary>
    /// Gets a printer by ID from the database.
    /// </summary>
    private async Task<Printer?> GetPrinterAsync(Guid printerId, CancellationToken ct)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<Farm.Infrastructure.Repositories.UnitOfWork.IUnitOfWork>();
        return await unitOfWork.Printers.FindByIdAsync(printerId, ct);
    }

    /// <summary>
    /// Checks for print completion/failure state transitions and synchronizes job status in database.
    /// Called when printer state changes from "printing" to idle (completion) or error state.
    /// </summary>
    private async Task CheckAndSyncJobCompletionAsync(Guid printerId, string previousState, string newState, CancellationToken ct)
    {
        try
        {
            // Only act on transitions FROM "printing" state
            if (!PrintJobCompletionService.IsPrintingState(previousState))
            {
                return;
            }

            _logger.LogInformation("[SdcpPollingService] Detected state transition for printer {PrinterId}: {PreviousState} -> {NewState}", printerId, previousState, newState);

            // Create a new scope to get the scoped service
            using IServiceScope scope = _scopeFactory.CreateScope();
            IPrintJobCompletionService completionService = scope.ServiceProvider.GetRequiredService<IPrintJobCompletionService>();

            if (PrintJobCompletionService.IsCompletionState(newState))
            {
                // Print completed successfully
                bool marked = await completionService.MarkCurrentJobAsCompletedAsync(printerId, newState, ct);
                if (marked)
                {
                    _logger.LogInformation("[SdcpPollingService] Print job marked as completed for printer {PrinterId}", printerId);
                }
            }
            else if (PrintJobCompletionService.IsFailureState(newState))
            {
                // Print failed
                bool marked = await completionService.MarkCurrentJobAsFailedAsync(printerId, $"Printer state changed to {newState}", ct);
                if (marked)
                {
                    _logger.LogWarning("[SdcpPollingService] Print job marked as failed for printer {PrinterId} (state: {NewState})", printerId, newState);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SdcpPollingService] Failed to sync job completion for printer {PrinterId}", printerId);
        }
    }

    #region Connection Health

    /// <inheritdoc/>
    public IReadOnlyDictionary<Guid, PrinterConnectionHealth> GetConnectionHealth()
    {
        foreach (var health in _connectionHealth.Values)
        {
            health.UpdateUptimePercent(TimeSpan.FromHours(1));
            health.ConnectionMode = "Polling";

            if (_printerStates.TryGetValue(health.PrinterId, out var state))
            {
                health.ConsecutiveFailures = state.ConsecutiveFailures;
            }
        }

        return _connectionHealth;
    }

    private void RecordHealthTransition(Guid printerId, string printerName, PrinterConnectionState newState, string? reason)
    {
        var health = _connectionHealth.GetOrAdd(printerId, id => new PrinterConnectionHealth
        {
            PrinterId = id,
            PrinterName = printerName,
            Backend = PrinterBackend.SDCP
        });

        health.RecordTransition(newState, reason);
    }

    #endregion
}
