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

namespace Farm.Backend.Plugin.PrusaLink;

/// <summary>
/// Background service for polling PrusaLink printers and broadcasting status updates via SignalR.
/// Unlike Moonraker which supports real-time WebSocket subscriptions, PrusaLink requires HTTP polling.
/// </summary>
public sealed class PrusaLinkPollingService(
    IHubContext<PrinterHub> hub,
    IServiceScopeFactory scopeFactory,
    ILogger<PrusaLinkPollingService> logger,
    IPrinterStatusCacheWriter statusCacheWriter) : IHostedService, IDisposable
{
    private readonly ILogger<PrusaLinkPollingService> _logger = logger;
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly IHubContext<PrinterHub> _hub = hub;
    private readonly IPrinterStatusCacheWriter _statusCacheWriter = statusCacheWriter;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<Guid, PrinterPollingState> _printerStates = new();
    private readonly ConcurrentDictionary<Guid, Task> _pollingLoops = new();

    // Polling interval for PrusaLink printers
    private static readonly TimeSpan PollingInterval = TimeSpan.FromSeconds(5);

    private Task? _mainLoop;

    /// <summary>
    /// Persistent state for each PrusaLink printer to track changes and avoid redundant updates.
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

        /// <summary>
        /// Whether printer info (NozzleDiameter, HasMmu) has been synced to the entity on this polling session.
        /// </summary>
        public bool HasSyncedPrinterInfo { get; set; }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("PrusaLinkPollingService starting");
#pragma warning disable VSTHRD003 // Avoid awaiting or returning a Task representing work that was not started within this context
        _ = _mainLoop = Task.Run(() => RunAsync(_cts.Token), _cts.Token);
#pragma warning restore VSTHRD003
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("PrusaLinkPollingService stopping");
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
    /// Main loop that continuously monitors and polls all PrusaLink printers.
    /// </summary>
    private async Task RunAsync(CancellationToken ct)
    {
        _logger.LogInformation("PrusaLinkPollingService main loop started");
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    // Get list of PrusaLink printers from database
                    List<Guid> printerIds = await GetPrusaLinkPrinterIdsAsync(ct);
                    _logger.LogDebug("PrusaLinkPollingService: Found {PrinterIdsCount} PrusaLink printers", printerIds.Count);

                    // Ensure polling loops exist for all PrusaLink printers
                    foreach (Guid id in printerIds)
                    {
                        if (!_pollingLoops.ContainsKey(id))
                        {
#pragma warning disable S6612 // Use the loop variable instead of capturing
                            var pollingLoop = Task.Run(() => PollPrinterAsync(id, ct), ct);
#pragma warning restore S6612
                            _pollingLoops.TryAdd(id, pollingLoop);
                            _logger.LogDebug("Started polling loop for PrusaLink printer {Id}", id);
                        }
                    }

                    // Remove polling loops for printers that are no longer PrusaLink
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
                    _logger.LogError(ex, "Error in PrusaLinkPollingService main loop");
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("PrusaLinkPollingService main loop cancelled");
        }
    }

    /// <summary>
    /// Polls a single PrusaLink printer at regular intervals.
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
                if (printer?.Backend != (int)PrinterBackend.PrusaLink)
                {
                    // Printer is no longer PrusaLink, remove from polling
                    _pollingLoops.TryRemove(printerId, out _);
                    _printerStates.TryRemove(printerId, out _);
                    return;
                }

                // Poll the printer
                try
                {
                    // Get the PrusaLink client from a scoped context
                    // (scoped services cannot be injected directly into singletons)
                    using IServiceScope scope = _scopeFactory.CreateScope();
                    IPrusaLinkClient prusaLinkClient = scope.ServiceProvider.GetRequiredService<IPrusaLinkClient>();
                    ManagedSpoolProviderHelper spoolProvider = scope.ServiceProvider.GetRequiredService<ManagedSpoolProviderHelper>();

                    // Use the Credential property populated by the repository layer
                    PrusaCompositeStatus status = await prusaLinkClient.GetCompositeStatusAsync(
                        printer.ServerUrl,
                        printer.Credential,
                        ct);

                    _logger.LogDebug("PrusaLink {PrinterId}: Got status - Online={StatusIsOnline}, State={StatusState}, Progress={StatusProgress}, JobName={StatusJobName}", printerId, status.IsOnline, status.State, status.Progress, status.JobName);

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

                    // Check for state transition from printing to idle/finished for job completion sync
                    string? previousState = state.LastKnownState;
                    bool stateChanged = status.State != previousState;

                    // Update state tracking (including PreviousState for transition detection)
                    state.PreviousState = previousState;
                    state.LastKnownIsOnline = status.IsOnline;
                    state.LastKnownState = status.State;
                    state.LastKnownProgress = status.Progress;
                    state.LastKnownJobName = status.JobName;
                    state.ConsecutiveFailures = 0;

                    // One-time sync of PrusaLink printer info (NozzleDiameter, HasMmu) to the entity
                    if (!state.HasSyncedPrinterInfo)
                    {
                        state.HasSyncedPrinterInfo = await SyncPrinterInfoAsync(printer, ct);
                    }

                    // Check for print completion/failure transitions
                    if (stateChanged && previousState != null)
                    {
                        await CheckAndSyncJobCompletionAsync(printerId, previousState, status.State!, ct);
                    }

                    // Resolve spool info from DB assignment
                    PrinterSpoolInfoDto? spoolInfo = await spoolProvider.GetManagedSpoolInfoAsync(printer, ct);

                    // Update cache before broadcasting to clients
                    var cacheUpdate = new PrinterStatusDto(
                        Id: printerId,
                        IsOnline: status.IsOnline,
                        State: PrinterStateNormalizer.NormalizeState(status.State),
                        Progress: status.Progress,
                        JobName: status.JobName,
                        ThumbnailUrl: status.ThumbnailUrl,
                        CameraStreamUrl: status.CameraStreamUrl,
                        CameraSnapshotUrl: null,
                        X: status.AxisX,
                        Y: status.AxisY,
                        Z: status.AxisZ,
                        HotendTemp: status.HotendTemp,
                        BedTemp: status.BedTemp,
                        HotendTarget: status.HotendTarget,
                        BedTarget: status.BedTarget,
                        SpoolInfo: spoolInfo,
                        PrintTimeLeftSeconds: status.TimeRemainingSeconds,
                        SpeedMultiplier: status.SpeedMultiplier);
                    _statusCacheWriter.UpdateStatus(cacheUpdate);

                    var signalRUpdate = new PrinterStatusUpdate(
                        Id: printerId,
                        IsOnline: status.IsOnline,
                        State: PrinterStateNormalizer.NormalizeState(status.State),
                        Progress: status.Progress,
                        JobName: status.JobName,
                        ThumbnailUrl: status.ThumbnailUrl,
                        CameraStreamUrl: status.CameraStreamUrl,
                        X: status.AxisX,
                        Y: status.AxisY,
                        Z: status.AxisZ,
                        HotendTemp: status.HotendTemp,
                        BedTemp: status.BedTemp,
                        HotendTarget: status.HotendTarget,
                        BedTarget: status.BedTarget,
                        HomedAxes: null,
                        SpoolInfo: spoolInfo,
                        FileName: PrinterStatusDto.ExtractFileName(status.JobName));

                    await _hub.Clients.All.SendAsync("printerupdated", signalRUpdate, ct);

                    state.LastPollTime = DateTime.UtcNow;
                }
                catch (Exception ex)
                {
                    state.ConsecutiveFailures++;
                    _logger.LogDebug(ex, "Failed to poll PrusaLink printer {PrinterId} (attempt {StateConsecutiveFailures})", printerId, state.ConsecutiveFailures);

                    // After 3 consecutive failures, mark as offline
                    if (state.ConsecutiveFailures >= 3 && state.LastKnownIsOnline)
                    {
                        _logger.LogWarning("PrusaLink printer {PrinterId} marked offline after {StateConsecutiveFailures} failures", printerId, state.ConsecutiveFailures);
                        state.LastKnownIsOnline = false;
                        var offlineCacheUpdate = new PrinterStatusDto(
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
                            SpoolInfo: null,
                            PrintTimeLeftSeconds: null,
                            SpeedMultiplier: null);
                        _statusCacheWriter.UpdateStatus(offlineCacheUpdate);

                        var offlineSignalRUpdate = new PrinterStatusUpdate(
                            Id: printerId,
                            IsOnline: false,
                            State: null,
                            Progress: null,
                            JobName: null,
                            ThumbnailUrl: null,
                            CameraStreamUrl: null,
                            X: null,
                            Y: null,
                            Z: null,
                            HotendTemp: null,
                            BedTemp: null,
                            HotendTarget: null,
                            BedTarget: null,
                            HomedAxes: null,
                            SpoolInfo: null,
                            FileName: null);

                        await _hub.Clients.All.SendAsync("printerupdated", offlineSignalRUpdate, ct);
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
                _logger.LogError(ex, "Unexpected error polling PrusaLink printer {PrinterId}", printerId);
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }
    }

    /// <summary>
    /// Gets the list of all PrusaLink printer IDs from the database.
    /// </summary>
    private async Task<List<Guid>> GetPrusaLinkPrinterIdsAsync(CancellationToken ct)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<Farm.Infrastructure.Repositories.UnitOfWork.IUnitOfWork>();
        List<Printer> printers = await unitOfWork.Printers.GetByBackendAsync(PrinterBackend.PrusaLink, ct);
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
    /// Called when printer state changes from "printing" to idle/finished (completion) or error (failure).
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

            _logger.LogInformation("[PrusaLinkPollingService] Detected state transition for printer {PrinterId}: {PreviousState} -> {NewState}", printerId, previousState, newState);

            // Create a new scope to get the scoped service
            using IServiceScope scope = _scopeFactory.CreateScope();
            IPrintJobCompletionService completionService = scope.ServiceProvider.GetRequiredService<IPrintJobCompletionService>();

            if (PrintJobCompletionService.IsCompletionState(newState))
            {
                // Print completed successfully
                bool marked = await completionService.MarkCurrentJobAsCompletedAsync(printerId, newState, ct);
                if (marked)
                {
                    _logger.LogInformation("[PrusaLinkPollingService] Print job marked as completed for printer {PrinterId}", printerId);
                }
            }
            else if (PrintJobCompletionService.IsFailureState(newState))
            {
                // Print failed
                bool marked = await completionService.MarkCurrentJobAsFailedAsync(printerId, $"Printer state changed to {newState}", ct);
                if (marked)
                {
                    _logger.LogWarning("[PrusaLinkPollingService] Print job marked as failed for printer {PrinterId} (state: {NewState})", printerId, newState);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[PrusaLinkPollingService] Failed to sync job completion for printer {PrinterId}", printerId);
        }
    }

    /// <summary>
    /// Fetches PrusaLink printer info and syncs NozzleDiameter and HasMmu to the Printer entity.
    /// </summary>
    private async Task<bool> SyncPrinterInfoAsync(Printer printer, CancellationToken ct)
    {
        try
        {
            using IServiceScope scope = _scopeFactory.CreateScope();
            IPrusaLinkClient prusaLinkClient = scope.ServiceProvider.GetRequiredService<IPrusaLinkClient>();

            PrinterInformation? info = await prusaLinkClient.GetPrinterInformationAsync(printer.ServerUrl, printer.Credential, ct);
            if (info is null)
            {
                return false;
            }

            bool changed = false;
            if (printer.NozzleDiameter != info.NozzleDiameter)
            {
                printer.NozzleDiameter = info.NozzleDiameter;
                changed = true;
            }

            if (printer.HasMmu != info.HasMmu)
            {
                printer.HasMmu = info.HasMmu;
                changed = true;
            }

            if (changed)
            {
                IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

                // Re-load the entity in this scope's context so EF tracks changes
                Printer? tracked = await unitOfWork.Printers.FindByIdAsync(printer.Id, ct);
                if (tracked is not null)
                {
                    tracked.NozzleDiameter = printer.NozzleDiameter;
                    tracked.HasMmu = printer.HasMmu;
                    await unitOfWork.SaveChangesAsync(ct);
                    _logger.LogInformation(
                        "[PrusaLinkPollingService] Synced printer info for {PrinterId}: NozzleDiameter={NozzleDiameter}, HasMmu={HasMmu}",
                        printer.Id, info.NozzleDiameter, info.HasMmu);
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[PrusaLinkPollingService] Failed to sync printer info for {PrinterId}", printer.Id);
            return false;
        }
    }
}
