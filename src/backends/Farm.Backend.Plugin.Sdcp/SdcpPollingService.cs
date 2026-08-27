using System.Collections.Concurrent;
using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Printers;
using Farm.Infrastructure.Repositories.UnitOfWork;
using Farm.Infrastructure.Services.Mutations;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.SignalR;
using Farm.Infrastructure.Services.Spoolman;
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
    IPrinterStatusCacheWriter statusCacheWriter,
    IFilamentCoverageBroadcaster? coverageBroadcaster = null,
    IMutationWatermarkReader? watermarkReader = null,
    IPrinterCacheInvalidator? printerCacheInvalidator = null) : IHostedService, IDisposable, IPrinterConnectionHealthProvider
{
    private readonly ILogger<SdcpPollingService> _logger = logger;
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly IHubContext<PrinterHub> _hub = hub;
    private readonly IPrinterStatusCacheWriter _statusCacheWriter = statusCacheWriter;
    private readonly IFilamentCoverageBroadcaster? _coverageBroadcaster = coverageBroadcaster;
    private readonly IPrinterCacheInvalidator? _printerCacheInvalidator = printerCacheInvalidator;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<Guid, PrinterPollingState> _printerStates = new();
    private readonly ConcurrentDictionary<Guid, Task> _pollingLoops = new();
    private readonly ConcurrentDictionary<Guid, PrinterConnectionHealth> _connectionHealth = new();

    /// <summary>
    /// Durable per-printer invalidation counter, kept independent of <see cref="PrinterPollingState"/>
    /// so it survives that state's teardown/recreation (e.g. the printer briefly leaving and
    /// re-entering this backend). Entries are never removed. <see cref="OnPrinterInvalidated"/>
    /// unconditionally bumps a printer's entry; both <see cref="RunAsync"/> and
    /// <see cref="PollPrinterAsync"/> snapshot a printer's generation before an in-flight database
    /// read and only publish that read's result to <see cref="PrinterPollingState.CachedPrinter"/>
    /// if the generation is still unchanged afterward — otherwise an invalidation raced the read
    /// and the (possibly stale) result is used for this tick only, never cached.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, long> _invalidationGenerations = new();

    /// <summary>
    /// Per-printer gate serializing <see cref="OnPrinterInvalidated"/>'s generation-bump-and-clear
    /// against every write-back site's generation-check-and-publish (<see cref="RunAsync"/> and
    /// <see cref="PollPrinterAsync"/>). Without this, "check generation, then write
    /// <see cref="PrinterPollingState.CachedPrinter"/>" is two independent, unsynchronized
    /// operations: a thread can pass the check, then be preempted before its write while
    /// <see cref="OnPrinterInvalidated"/> runs and clears the cache, then resume and overwrite the
    /// just-cleared cache with its (now provably stale) fetch result — silently undoing the
    /// invalidation. Taking this lock around both the bump/clear and each check/write makes the
    /// two sequences mutually exclusive, closing that race. Entries are never removed (matching
    /// <see cref="_invalidationGenerations"/>'s non-removal policy), for the same reason: removing
    /// a lock object while another thread might be holding or waiting on it would let a
    /// subsequently created replacement lock run concurrently with the old one, reintroducing the
    /// exact race this gate exists to prevent.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, object> _cacheGates = new();

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

        /// <summary>
        /// The last "printerupdated" payload actually broadcast for this printer, used to suppress
        /// byte-identical re-broadcasts. Null until the first broadcast (including after a backend
        /// restart, since this state is in-memory only), so the first message is never suppressed.
        /// </summary>
        public PrinterStatusUpdate? LastBroadcastUpdate { get; set; }

        /// <summary>
        /// Cached, fully decrypted printer row, refreshed by the 30-second reconciliation loop or
        /// cleared by an explicit invalidation (see <see cref="IPrinterCacheInvalidator"/>). Avoids
        /// re-querying the database and re-decrypting credentials on every poll tick (issue #1763).
        /// </summary>
        public Printer? CachedPrinter { get; set; }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("SdcpPollingService starting");
        if (_printerCacheInvalidator is not null)
        {
            _printerCacheInvalidator.Subscribe(OnPrinterInvalidated);
        }
#pragma warning disable VSTHRD003 // Avoid awaiting or returning a Task representing work that was not started within this context
        _ = _mainLoop = Task.Run(() => RunAsync(_cts.Token), _cts.Token);
#pragma warning restore VSTHRD003
        return Task.CompletedTask;
    }

    /// <summary>
    /// Invoked when a printer's persisted row has changed (e.g. edited via the API). Drops the
    /// cached copy so the next poll tick re-reads the row from the database instead of waiting
    /// for the next 30-second reconciliation pass.
    /// </summary>
    private void OnPrinterInvalidated(Guid printerId)
    {
        // Take the same per-printer gate every write-back site uses, so this can never
        // interleave with an in-flight fetch's check-then-write: either the bump/clear runs
        // fully before the write-back's check (which then correctly observes a mismatch), or
        // fully after (the just-published stale row is immediately cleared again).
        object gate = _cacheGates.GetOrAdd(printerId, static _ => new object());
        lock (gate)
        {
            // Bump unconditionally (even if no PrinterPollingState exists yet) so an invalidation
            // that races a first-ever fetch for this printer is still durably recorded.
            _invalidationGenerations.AddOrUpdate(printerId, 1, static (_, current) => current + 1);

            if (_printerStates.TryGetValue(printerId, out PrinterPollingState? state))
            {
                state.CachedPrinter = null;
            }
        }
    }

    /// <summary>
    /// Publishes <paramref name="printer"/> into <paramref name="state"/>.CachedPrinter only if no
    /// invalidation has raced the fetch that produced it -- i.e. only if the current invalidation
    /// generation for this printer still matches <paramref name="capturedGeneration"/>, the value
    /// observed *before* the fetch began. Runs under the same per-printer <see cref="_cacheGates"/>
    /// lock <see cref="OnPrinterInvalidated"/> uses, so the generation check and the publish are
    /// atomic with respect to a concurrent invalidation: either this call's check-and-publish runs
    /// fully before an invalidation's bump-and-clear (which then correctly observes the freshly
    /// published row and clears it again), or fully after it (this call then observes the bumped
    /// generation and declines to publish) -- there is no window in which a stale check can pass
    /// and then have its write silently undo a subsequent invalidation. Shared by both write-back
    /// sites (the 30s reconciliation loop and the per-tick cache-miss fallback) so a single test can
    /// exercise this critical section for both callers at once.
    /// </summary>
    /// <param name="printerId">The printer whose cache entry is being published.</param>
    /// <param name="printer">The freshly fetched printer snapshot to publish.</param>
    /// <param name="capturedGeneration">The invalidation generation observed before the fetch began.</param>
    /// <param name="state">The per-printer polling state whose <c>CachedPrinter</c> is written.</param>
    /// <param name="onGenerationCheckPassedForTestingOnly">
    /// Test-only seam, always null in production. When supplied, it is invoked synchronously
    /// while <paramref name="state"/>'s <c>gate</c> is still held, immediately after the
    /// generation check passes but before the write -- letting a test deterministically pause
    /// inside the critical section and prove a concurrent <see cref="OnPrinterInvalidated"/>
    /// call genuinely blocks on the same lock rather than merely being unlikely to interleave.
    /// </param>
    private void TryPublishCachedPrinter(Guid printerId, Printer printer, long capturedGeneration, PrinterPollingState state, Action? onGenerationCheckPassedForTestingOnly = null)
    {
        object gate = _cacheGates.GetOrAdd(printerId, static _ => new object());
        lock (gate)
        {
            if (_invalidationGenerations.GetOrAdd(printerId, 0L) == capturedGeneration)
            {
                onGenerationCheckPassedForTestingOnly?.Invoke();
                state.CachedPrinter = printer;
            }
        }
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
        if (_printerCacheInvalidator is not null)
        {
            _printerCacheInvalidator.Unsubscribe(OnPrinterInvalidated);
        }

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
                    // Get list of SDCP printers (fully decrypted) from database, refreshing the
                    // per-printer cache used by PollPrinterAsync (issue #1763)
                    Dictionary<Guid, long> generationSnapshot = _invalidationGenerations.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
                    List<Printer> printers = await GetSdcpPrintersAsync(ct);
                    List<Guid> printerIds = printers.Select(p => p.Id).ToList();
                    _logger.LogDebug("SdcpPollingService: Found {PrinterIdsCount} SDCP printers", printerIds.Count);

                    foreach (Printer printer in printers)
                    {
                        PrinterPollingState refreshState = _printerStates.GetOrAdd(
                            printer.Id,
                            _ => new PrinterPollingState { PrinterId = printer.Id, LastKnownIsOnline = false });

                        // Only publish if no invalidation raced the fetch above; otherwise this
                        // row may already be stale and the next tick's cache-miss re-read (or the
                        // next reconciliation pass) will pick up the fresh value instead.
                        long capturedGeneration = generationSnapshot.GetValueOrDefault(printer.Id, 0L);
                        TryPublishCachedPrinter(printer.Id, printer, capturedGeneration, refreshState);
                    }

                    // Ensure polling loops exist for all SDCP printers
                    foreach (Guid id in printerIds.Where(id => !_pollingLoops.ContainsKey(id)))
                    {
#pragma warning disable S6612 // Use the loop variable instead of capturing
                        var pollingLoop = Task.Run(() => PollPrinterAsync(id, ct), ct);
#pragma warning restore S6612
                        _pollingLoops.TryAdd(id, pollingLoop);
                        _logger.LogDebug("Started polling loop for SDCP printer {Id}", id);
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
                // Get printer details - use the cached row refreshed by the 30s reconciliation
                // loop (or by an explicit invalidation) instead of re-querying the database
                // every tick (issue #1763). Fall back to a fresh read only on a cache miss.
                Printer? printer = state.CachedPrinter;
                if (printer is null)
                {
                    long capturedGeneration = _invalidationGenerations.GetOrAdd(printerId, 0L);
                    printer = await GetPrinterAsync(printerId, ct);

                    // Only cache the result if no invalidation raced this fetch; otherwise leave
                    // the cache empty so the very next tick re-reads instead of resurrecting a
                    // row that may already be stale.
                    if (printer is not null)
                    {
                        TryPublishCachedPrinter(printerId, printer, capturedGeneration, state);
                    }
                }

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
                    long? originWatermark = await OriginWatermark
                        .CaptureAsync(watermarkReader, _logger, "SDCP status", ct)
                        .ConfigureAwait(false);
                    PrinterCompositeStatus status = await sdcpClient.GetCompositeStatusAsync(
                        printer.BackendUrl,
                        ct);

                    if (!wasOnline && status.IsOnline)
                    {
                        _logger.LogInformation(
                            "SDCP printer recovered to Online. PrinterId={PrinterId}, BackendUrl={BackendUrl}, PreviousFailures={PreviousFailures}",
                            printerId, printer.BackendUrl, previousFailures);
                    }
                    else if (previousFailures > 0)
                    {
                        _logger.LogDebug(
                            "SDCP poll succeeded after failures. PrinterId={PrinterId}, BackendUrl={BackendUrl}, PreviousFailures={PreviousFailures}",
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
                    _ = state.LastKnownIsOnline != status.IsOnline
                        || state.LastKnownState != status.State
                        || progressChanged
                        || state.LastKnownJobName != status.JobName;

                    // Check for state transition from printing to idle for job completion sync
                    string? previousState = state.LastKnownState;
                    string? previousJobName = state.LastKnownJobName;
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
                        await CheckAndSyncJobCompletionAsync(
                            printerId,
                            previousState,
                            status.State,
                            previousJobName,
                            ct);
                    }

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
                        X: status.X,
                        Y: status.Y,
                        Z: status.Z,
                        HotendTemp: status.HotendTemp,
                        BedTemp: status.BedTemp,
                        HotendTarget: status.HotendTarget,
                        BedTarget: status.BedTarget,
                        SpoolInfo: null);
                    _statusCacheWriter.UpdateStatus(cacheUpdate, originWatermark);

                    var signalRUpdate = new PrinterStatusUpdate(
                        Id: printerId,
                        IsOnline: status.IsOnline,
                        State: PrinterStateNormalizer.NormalizeState(status.State),
                        Progress: status.Progress,
                        JobName: status.JobName,
                        ThumbnailUrl: status.ThumbnailUrl,
                        CameraStreamUrl: status.CameraStreamUrl,
                        X: status.X,
                        Y: status.Y,
                        Z: status.Z,
                        HotendTemp: status.HotendTemp,
                        BedTemp: status.BedTemp,
                        HotendTarget: status.HotendTarget,
                        BedTarget: status.BedTarget,
                        HomedAxes: null,
                        SpoolInfo: null,
                        FileName: PrinterStatusDto.ExtractFileName(status.JobName));

                    if (PrinterStatusBroadcastGate.ShouldBroadcast(state.LastBroadcastUpdate, signalRUpdate))
                    {
                        await _hub.Clients.Group(
                                Farm.Infrastructure.Security.AuthorizedHubGroups.Printer(printerId))
                            .SendAsync("printerupdated", signalRUpdate, ct);
                        state.LastBroadcastUpdate = signalRUpdate;
                    }

                    await _coverageBroadcaster
                        .BroadcastJobProgressIfChangedAsync(printerId, progressChanged, ct)
                        .ConfigureAwait(false);

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

                        _logger.LogWarning(
                            "SDCP printer marked Offline after consecutive failures. PrinterId={PrinterId}, BackendUrl={BackendUrl}, Failures={Failures}",
                            printerId, printer?.BackendUrl, state.ConsecutiveFailures);

                        RecordHealthTransition(printerId, printer?.Name ?? printerId.ToString(), PrinterConnectionState.Offline, $"Failed {state.ConsecutiveFailures} consecutive times");

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
                            SpoolInfo: null);
                        _statusCacheWriter.UpdateStatus(offlineCacheUpdate, originWatermark: null);

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

                        await _hub.Clients.Group(
                                Farm.Infrastructure.Security.AuthorizedHubGroups.Printer(printerId))
                            .SendAsync("printerupdated", offlineSignalRUpdate, ct);
                        state.LastBroadcastUpdate = offlineSignalRUpdate;
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
    /// Gets all SDCP printers (fully decrypted) from the database.
    /// </summary>
    private async Task<List<Printer>> GetSdcpPrintersAsync(CancellationToken ct)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<Farm.Infrastructure.Repositories.UnitOfWork.IUnitOfWork>();
        return await unitOfWork.Printers.GetByBackendAsync(PrinterBackend.SDCP, ct);
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
    private async Task CheckAndSyncJobCompletionAsync(
        Guid printerId,
        string previousState,
        string newState,
        string? previousJobName,
        CancellationToken ct)
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
                bool marked = await completionService.MarkCurrentJobAsCompletedAsync(
                    printerId,
                    newState,
                    new PrinterTerminalObservation(previousJobName),
                    ct);
                if (marked)
                {
                    _logger.LogInformation("[SdcpPollingService] Print job marked as completed for printer {PrinterId}", printerId);
                }
            }
            else if (PrintJobCompletionService.IsFailureState(newState))
            {
                // Print failed
                bool marked = await completionService.MarkCurrentJobAsFailedAsync(
                    printerId,
                    $"Printer state changed to {newState}",
                    new PrinterTerminalObservation(previousJobName),
                    ct);
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
        return _connectionHealth.ToDictionary(entry => entry.Key, entry =>
        {
            PrinterConnectionHealth snapshot = entry.Value.CreateSnapshot();
            snapshot.UpdateUptimePercent(TimeSpan.FromHours(1));
            snapshot.ConnectionMode = "Polling";

            if (_printerStates.TryGetValue(snapshot.PrinterId, out var state))
            {
                snapshot.ConsecutiveFailures = state.ConsecutiveFailures;
            }

            return snapshot;
        });
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
