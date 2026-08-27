using System.Collections.Concurrent;
using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.UnitOfWork;
using Farm.Infrastructure.Services.Mutations;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.SignalR;
using Farm.Infrastructure.Services.Spoolman;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Farm.Backend.Plugin.FlashForge;

/// <summary>
/// Background service for polling FlashForge printers and broadcasting status updates via SignalR.
/// FlashForge uses a proprietary TCP serial protocol, so status must be polled periodically.
/// </summary>
public sealed class FlashForgePollingService(
    IHubContext<PrinterHub> hub,
    IServiceScopeFactory scopeFactory,
    ILogger<FlashForgePollingService> logger,
    IPrinterStatusCacheWriter statusCacheWriter,
    IFilamentCoverageBroadcaster? coverageBroadcaster = null,
    IMutationWatermarkReader? watermarkReader = null,
    IPrinterCacheInvalidator? printerCacheInvalidator = null) : IHostedService, IDisposable
{
    private readonly ILogger<FlashForgePollingService> _logger = logger;
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly IHubContext<PrinterHub> _hub = hub;
    private readonly IPrinterStatusCacheWriter _statusCacheWriter = statusCacheWriter;
    private readonly IFilamentCoverageBroadcaster? _coverageBroadcaster = coverageBroadcaster;
    private readonly IPrinterCacheInvalidator? _printerCacheInvalidator = printerCacheInvalidator;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<Guid, PrinterPollingState> _printerStates = new();
    private readonly ConcurrentDictionary<Guid, Task> _pollingLoops = new();

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

    /// <summary>
    /// Polling interval for FlashForge printers.
    /// Slightly longer than PrusaLink (5s) because each poll opens 4 sequential TCP connections.
    /// </summary>
    private static readonly TimeSpan PollingInterval = TimeSpan.FromSeconds(10);

    private Task? _mainLoop;

    /// <summary>
    /// Persistent state for each FlashForge printer to track changes and avoid redundant updates.
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
        /// Tracks whether an external print job has been created for the current printing session.
        /// Reset when the printer leaves "Printing" state so the next external print is detected.
        /// </summary>
        public bool ExternalJobCreatedForCurrentPrint { get; set; }

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

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("FlashForgePollingService starting");
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

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("FlashForgePollingService stopping");
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

    /// <inheritdoc />
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
    /// Main loop that continuously monitors and polls all FlashForge printers.
    /// </summary>
    private async Task RunAsync(CancellationToken ct)
    {
        _logger.LogInformation("FlashForgePollingService main loop started");
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    // Get list of FlashForge printers (fully decrypted), refreshing the per-printer
                    // cache used by PollPrinterAsync (issue #1763)
                    Dictionary<Guid, long> generationSnapshot = _invalidationGenerations.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
                    List<Printer> printers = await GetFlashForgePrintersAsync(ct);
                    List<Guid> printerIds = printers.Select(p => p.Id).ToList();
                    _logger.LogDebug("FlashForgePollingService: Found {Count} FlashForge printers", printerIds.Count);

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

                    // Ensure polling loops exist for all FlashForge printers
                    foreach (Guid id in printerIds.Where(id => !_pollingLoops.ContainsKey(id)))
                    {
#pragma warning disable S6612 // Use the loop variable instead of capturing
                        var pollingLoop = Task.Run(() => PollPrinterAsync(id, ct), ct);
#pragma warning restore S6612
                        _pollingLoops.TryAdd(id, pollingLoop);
                        _logger.LogDebug("Started polling loop for FlashForge printer {Id}", id);
                    }

                    // Remove polling loops for printers that are no longer FlashForge
                    var inactiveIds = _pollingLoops.Keys.Except(printerIds).ToList();
                    foreach (Guid printerId in inactiveIds)
                    {
                        _pollingLoops.TryRemove(printerId, out _);
                        _printerStates.TryRemove(printerId, out _);
                        _logger.LogDebug("Stopped polling for FlashForge printer {PrinterId}", printerId);
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
                    _logger.LogError(ex, "Error in FlashForgePollingService main loop");
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("FlashForgePollingService main loop cancelled");
        }
    }

    /// <summary>
    /// Polls a single FlashForge printer at regular intervals.
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

                if (printer?.Backend != (int)PrinterBackend.FlashForge)
                {
                    _pollingLoops.TryRemove(printerId, out _);
                    _printerStates.TryRemove(printerId, out _);
                    return;
                }

                try
                {
                    using IServiceScope scope = _scopeFactory.CreateScope();
                    IFlashForgeClient flashForgeClient = scope.ServiceProvider.GetRequiredService<IFlashForgeClient>();
                    ManagedSpoolProviderHelper spoolProvider = scope.ServiceProvider.GetRequiredService<ManagedSpoolProviderHelper>();

                    long? originWatermark = await OriginWatermark
                        .CaptureAsync(watermarkReader, _logger, "FlashForge status", ct)
                        .ConfigureAwait(false);
                    PrinterCompositeStatus status = await flashForgeClient.GetCompositeStatusAsync(
                        printer.ServerUrl,
                        ct);

                    _logger.LogDebug(
                        "FlashForge {PrinterId}: Online={IsOnline}, State={State}, Progress={Progress}, HotendTemp={HotendTemp}",
                        printerId, status.IsOnline, status.State, status.Progress, status.HotendTemp);

                    // Track state transitions for job completion detection
                    string? previousState = state.LastKnownState;
                    string? previousJobName = state.LastKnownJobName;
                    bool stateChanged = status.State != previousState;
#pragma warning disable S1244 // Explicit tolerance is appropriate for progress telemetry.
                    bool progressChanged = state.LastKnownProgress is null || status.Progress is null
                        ? state.LastKnownProgress != status.Progress
                        : Math.Abs(state.LastKnownProgress.Value - status.Progress.Value) > 0.01;
#pragma warning restore S1244

                    // Update state tracking
                    state.PreviousState = previousState;
                    state.LastKnownIsOnline = status.IsOnline;
                    state.LastKnownState = status.State;
                    state.LastKnownProgress = status.Progress;
                    state.LastKnownJobName = status.JobName;
                    state.ConsecutiveFailures = 0;

                    // Check for print completion/failure transitions
                    if (stateChanged && previousState != null)
                    {
                        await CheckAndSyncJobCompletionAsync(
                            printerId,
                            previousState,
                            status.State!,
                            previousJobName,
                            ct);
                    }

                    // External print detection: when printer transitions TO "printing" from a
                    // non-printing state, check if a PrintFarmer job exists. If not, create a
                    // synthetic job so the print lifecycle is tracked and completion works.
                    if (stateChanged && PrintJobCompletionService.IsPrintingState(status.State)
                                     && !PrintJobCompletionService.IsPrintingState(previousState)
                                     && !state.ExternalJobCreatedForCurrentPrint)
                    {
                        await DetectAndCreateExternalPrintJobAsync(printerId, status.JobName, ct);
                        state.ExternalJobCreatedForCurrentPrint = true;
                    }

                    // Reset external job tracking when printer leaves "printing" state
                    if (stateChanged && !PrintJobCompletionService.IsPrintingState(status.State))
                    {
                        state.ExternalJobCreatedForCurrentPrint = false;
                    }

                    // Resolve spool info from DB assignment
                    PrinterSpoolInfoDto? spoolInfo = await spoolProvider.GetManagedSpoolInfoAsync(printer, ct);

                    var cacheUpdate = new PrinterStatusDto(
                        Id: printerId,
                        IsOnline: status.IsOnline,
                        State: PrinterStateNormalizer.NormalizeState(status.State),
                        Progress: status.Progress,
                        JobName: status.JobName,
                        ThumbnailUrl: status.ThumbnailUrl,
                        CameraStreamUrl: status.CameraStreamUrl,
                        CameraSnapshotUrl: status.CameraSnapshotUrl,
                        X: status.X,
                        Y: status.Y,
                        Z: status.Z,
                        HotendTemp: status.HotendTemp,
                        BedTemp: status.BedTemp,
                        HotendTarget: status.HotendTarget,
                        BedTarget: status.BedTarget,
                        SpoolInfo: spoolInfo,
                        ExtruderTemperatures: status.ExtruderTemperatures,
                        DetectedExtruderCount: status.DetectedExtruderCount,
                        PrintTimeLeftSeconds: status.PrintTimeLeftSeconds);
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
                        SpoolInfo: spoolInfo,
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
                    _logger.LogDebug(ex, "Failed to poll FlashForge printer {PrinterId} (attempt {Attempts})", printerId, state.ConsecutiveFailures);

                    // After 3 consecutive failures, mark as offline
                    if (state.ConsecutiveFailures >= 3 && state.LastKnownIsOnline)
                    {
                        _logger.LogWarning("FlashForge printer {PrinterId} marked offline after {Attempts} failures", printerId, state.ConsecutiveFailures);
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
                            ExtruderTemperatures: null,
                            DetectedExtruderCount: null,
                            PrintTimeLeftSeconds: null);
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

                        if (PrinterStatusBroadcastGate.ShouldBroadcast(state.LastBroadcastUpdate, offlineSignalRUpdate))
                        {
                            await _hub.Clients.Group(
                                    Farm.Infrastructure.Security.AuthorizedHubGroups.Printer(printerId))
                                .SendAsync("printerupdated", offlineSignalRUpdate, ct);
                            state.LastBroadcastUpdate = offlineSignalRUpdate;
                        }
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
                _logger.LogError(ex, "Unexpected error polling FlashForge printer {PrinterId}", printerId);
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }
    }

    /// <summary>
    /// Gets all FlashForge printers (fully decrypted) from the database.
    /// </summary>
    private async Task<List<Printer>> GetFlashForgePrintersAsync(CancellationToken ct)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        return await unitOfWork.Printers.GetByBackendAsync(PrinterBackend.FlashForge, ct);
    }

    /// <summary>
    /// Gets a printer by ID from the database.
    /// </summary>
    private async Task<Printer?> GetPrinterAsync(Guid printerId, CancellationToken ct)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        return await unitOfWork.Printers.FindByIdAsync(printerId, ct);
    }

    /// <summary>
    /// Checks for print completion/failure state transitions and synchronizes job status in database.
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
            if (!PrintJobCompletionService.IsPrintingState(previousState))
            {
                return;
            }

            _logger.LogInformation("[FlashForgePollingService] State transition for printer {PrinterId}: {Previous} -> {New}", printerId, previousState, newState);

            using IServiceScope scope = _scopeFactory.CreateScope();
            IPrintJobCompletionService completionService = scope.ServiceProvider.GetRequiredService<IPrintJobCompletionService>();

            if (PrintJobCompletionService.IsCompletionState(newState))
            {
                bool marked = await completionService.MarkCurrentJobAsCompletedAsync(
                    printerId,
                    newState,
                    new PrinterTerminalObservation(previousJobName),
                    ct);
                if (marked)
                {
                    _logger.LogInformation("[FlashForgePollingService] Print job marked as completed for printer {PrinterId}", printerId);
                }
            }
            else if (PrintJobCompletionService.IsFailureState(newState))
            {
                bool marked = await completionService.MarkCurrentJobAsFailedAsync(
                    printerId,
                    $"Printer state changed to {newState}",
                    new PrinterTerminalObservation(previousJobName),
                    ct);
                if (marked)
                {
                    _logger.LogWarning("[FlashForgePollingService] Print job marked as failed for printer {PrinterId} (state: {NewState})", printerId, newState);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[FlashForgePollingService] Failed to sync job completion for printer {PrinterId}", printerId);
        }
    }

    /// <summary>
    /// Detects externally-started prints (e.g., via OrcaSlicer "Upload and Print") and creates
    /// a synthetic tracking job if no active PrintFarmer job exists for the printer.
    /// </summary>
    private async Task DetectAndCreateExternalPrintJobAsync(Guid printerId, string? fileName, CancellationToken ct)
    {
        try
        {
            using IServiceScope scope = _scopeFactory.CreateScope();
            IPrintJobCompletionService completionService = scope.ServiceProvider.GetRequiredService<IPrintJobCompletionService>();

            bool created = await completionService.EnsureExternalPrintJobExistsAsync(printerId, fileName, ct);
            if (created)
            {
                _logger.LogInformation(
                    "[FlashForgePollingService] External print detected on printer {PrinterId}, created tracking job (file: {FileName})",
                    printerId,
                    fileName ?? "unknown");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[FlashForgePollingService] Failed to create external print job for printer {PrinterId}", printerId);
        }
    }
}
