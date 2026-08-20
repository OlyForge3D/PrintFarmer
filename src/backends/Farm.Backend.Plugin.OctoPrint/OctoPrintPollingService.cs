using System.Collections.Concurrent;
using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Printers;
using Farm.Infrastructure.Services.Mutations;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.SignalR;
using Farm.Infrastructure.Services.Spoolman;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Farm.Backend.Plugin.OctoPrint;

/// <summary>
/// Background service for OctoPrint real-time status updates.
/// Implements dual-layer architecture: WebSocket primary + HTTP polling fallback.
/// Maintains persistent WebSocket connections to each OctoPrint printer's /sockjs/websocket endpoint.
/// Falls back to HTTP polling every 10 seconds if WebSocket connection fails or is unavailable.
/// Broadcasts all status updates via SignalR hub for consistent client experience.
/// </summary>
public sealed class OctoPrintPollingService(
    IHubContext<PrinterHub> hub,
    IServiceScopeFactory scopeFactory,
    ILogger<OctoPrintPollingService> logger,
    IPrinterStatusCacheWriter statusCacheWriter,
    IFilamentCoverageBroadcaster? coverageBroadcaster = null,
    IMutationWatermarkReader? watermarkReader = null,
    IPrinterCacheInvalidator? printerCacheInvalidator = null) : IHostedService, IDisposable
{
    private readonly ILogger<OctoPrintPollingService> _logger = logger;
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly IHubContext<PrinterHub> _hub = hub;
    private readonly IPrinterStatusCacheWriter _statusCacheWriter = statusCacheWriter;
    private readonly IFilamentCoverageBroadcaster? _coverageBroadcaster = coverageBroadcaster;
    private readonly IPrinterCacheInvalidator? _printerCacheInvalidator = printerCacheInvalidator;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<Guid, OctoPrintWebSocketAdapter> _webSocketAdapters = new();
    private readonly ConcurrentDictionary<Guid, PrinterPollingState> _printerStates = new();
    private readonly ConcurrentDictionary<Guid, Task> _pollingLoops = new();

    /// <summary>
    /// Per-printer gate serializing "is the adapter missing?" checks against adapter construction.
    /// Without this, the 30-second reconciliation loop and the 5-second <see cref="PollPrinterAsync"/>
    /// fallback loop can both observe a missing adapter after an invalidation and each construct one,
    /// with the loser's live WebSocket connection silently overwritten (and never disposed) in
    /// <see cref="_webSocketAdapters"/>. See <see cref="EnsureWebSocketAdapter"/>.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, object> _adapterCreationLocks = new();

    // Polling interval for HTTP fallback
    private static readonly TimeSpan PollingInterval = TimeSpan.FromSeconds(5);

    private Task? _mainLoop;

    /// <summary>
    /// Persistent state for each OctoPrint printer to track changes and avoid redundant updates.
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

        public string? LastApiState { get; set; } // "responding", "authFail", "noResponse"

        public OctoPrintWebSocketAdapter? WebSocketAdapter { get; set; }

        /// <summary>
        /// ServerUrl the adapter was created with, used to detect credential changes.
        /// </summary>
        public string? CreatedWithServerUrl { get; set; }

        /// <summary>
        /// API key the adapter was created with, used to detect credential changes.
        /// </summary>
        public string? CreatedWithApiKey { get; set; }

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

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("OctoPrintPollingService starting");
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
    /// <remarks>
    /// Unlike the other backends, OctoPrint's primary transport is a long-lived
    /// <see cref="OctoPrintWebSocketAdapter"/> that is otherwise only torn down/recreated by the
    /// 30-second reconciliation loop's credential-change comparison. Clearing
    /// <see cref="PrinterPollingState.CachedPrinter"/> alone would leave that adapter connected
    /// with the printer's old URL/API key for up to 30 seconds after an edit. Tear the adapter
    /// down here too so <see cref="PollPrinterAsync"/> recreates it (with the new connection
    /// details) on its very next tick, matching the immediate-invalidation behavior of the other
    /// three backends.
    /// </remarks>
    private void OnPrinterInvalidated(Guid printerId)
    {
        if (_printerStates.TryGetValue(printerId, out PrinterPollingState? state))
        {
            state.CachedPrinter = null;

            // Force the next reconciliation pass (or on-demand recreation in PollPrinterAsync)
            // to treat this printer as needing a fresh adapter, even if it hasn't run yet.
            state.CreatedWithServerUrl = null;
            state.CreatedWithApiKey = null;
        }

        if (_webSocketAdapters.TryRemove(printerId, out OctoPrintWebSocketAdapter? adapter))
        {
            try
            {
                adapter.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "OctoPrint {PrinterId}: error disposing WebSocket adapter during invalidation", printerId);
            }
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("OctoPrintPollingService stopping");
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

        // Signal cancellation to background loops first, then dispose adapters and clear collections.
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // ignore - already disposed
        }

        foreach (OctoPrintWebSocketAdapter adapter in _webSocketAdapters.Values)
        {
            try
            {
                adapter?.Dispose();
            }
            catch
            {
            }
        }

        _webSocketAdapters.Clear();

        // Do not call Dispose() on running Task instances. They will be observed by the runtime when completed.
        _pollingLoops.Clear();

        try
        {
            _cts.Dispose();
        }
        catch
        {
        }
    }

    /// <summary>
    /// Creates a new WebSocket adapter for the given printer, stores it and its polling state,
    /// and starts a background connection attempt. Shared by the 30-second reconciliation loop
    /// and by <see cref="PollPrinterAsync"/>'s on-demand recreation after an invalidation removed
    /// the previous adapter, so both paths construct the adapter identically.
    /// </summary>
    private OctoPrintWebSocketAdapter CreateWebSocketAdapter(Guid id, Printer printer, CancellationToken ct)
    {
        // Get the OctoPrint client from a scoped context
        // (scoped services cannot be injected directly into singletons)
        using IServiceScope scope = _scopeFactory.CreateScope();
        IOctoPrintClient octoPrintClient = scope.ServiceProvider.GetRequiredService<IOctoPrintClient>();

        var adapter = new OctoPrintWebSocketAdapter(
            id,
            printer,
            _logger,
            octoPrintClient,
            _hub,
            _statusCacheWriter,
            _coverageBroadcaster,
            watermarkReader);

        _webSocketAdapters[id] = adapter;
        PrinterPollingState state = _printerStates.GetOrAdd(id, printerId => new PrinterPollingState
        {
            PrinterId = printerId,
            LastKnownIsOnline = false,
            LastApiState = "unset",
            WebSocketAdapter = adapter
        });
        state.WebSocketAdapter = adapter;
        state.CreatedWithServerUrl = printer.ServerUrl;
        state.CreatedWithApiKey = printer.Credential?.ApiKey;
        state.CachedPrinter = printer;

        _logger.LogDebug("Created WebSocket adapter for OctoPrint printer {Id}", id);

        // Attempt WebSocket connection in background
        _ = Task.Run(
            async () =>
        {
            try
            {
                await adapter.ConnectAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "WebSocket connection failed for {Id}, will use HTTP polling", id);
            }
        }, ct);

        return adapter;
    }

    /// <summary>
    /// Ensures exactly one up-to-date WebSocket adapter exists per printer, even when the 30-second
    /// reconciliation loop and <see cref="PollPrinterAsync"/>'s per-tick check race to recreate a
    /// missing or stale adapter after an invalidation or a credential change. Holds a per-printer
    /// lock across the "does one exist and is it current?" check, any teardown of a stale adapter,
    /// and the construction itself, so only one caller ever calls <see cref="CreateWebSocketAdapter"/>
    /// for a given printer at a time; the loser observes the winner's adapter already present in
    /// <see cref="_webSocketAdapters"/> (never a stale one mid-teardown) and returns it instead of
    /// constructing — and leaking — a second live connection.
    /// </summary>
    private OctoPrintWebSocketAdapter EnsureWebSocketAdapter(Guid id, Printer printer, CancellationToken ct)
    {
        object gate = _adapterCreationLocks.GetOrAdd(id, static _ => new object());
        lock (gate)
        {
            if (_webSocketAdapters.TryGetValue(id, out OctoPrintWebSocketAdapter? existing) && existing is not null)
            {
                bool credentialsChanged = _printerStates.TryGetValue(id, out PrinterPollingState? existingState)
                    && (printer.ServerUrl != existingState.CreatedWithServerUrl
                        || printer.Credential?.ApiKey != existingState.CreatedWithApiKey);

                if (!credentialsChanged)
                {
                    return existing;
                }

                _logger.LogInformation("OctoPrint {Id}: Credentials changed, recreating adapter", id);
                _webSocketAdapters.TryRemove(id, out _);
                _pollingLoops.TryRemove(id, out _);
                _printerStates.TryRemove(id, out _);

                try
                {
                    existing.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "OctoPrint {Id}: error disposing stale WebSocket adapter before recreation", id);
                }
            }

            return CreateWebSocketAdapter(id, printer, ct);
        }
    }

    /// <summary>
    /// Main loop that continuously monitors and manages WebSocket connections for all OctoPrint printers.
    /// Discovers OctoPrint printers every 30 seconds and manages WebSocket + HTTP fallback for each.
    /// </summary>
    private async Task RunAsync(CancellationToken ct)
    {
        _logger.LogInformation("OctoPrintPollingService main loop started");
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    // Get list of OctoPrint printers (fully decrypted) from database
                    List<Printer> printers = await GetOctoPrintPrintersAsync(ct);
                    Dictionary<Guid, Printer> printersById = printers.ToDictionary(p => p.Id);
                    List<Guid> printerIds = printers.Select(p => p.Id).ToList();
                    _logger.LogDebug("OctoPrintPollingService: Found {PrinterIdsCount} OctoPrint printers", printerIds.Count);

                    // Ensure WebSocket adapters and polling loops exist for all OctoPrint printers.
                    // EnsureWebSocketAdapter atomically detects a missing adapter or a credential
                    // change (ServerUrl, API key) and recreates it under a single per-printer lock,
                    // so this loop can never race PollPrinterAsync's own on-demand recreation below.
                    foreach (Guid id in printerIds)
                    {
                        printersById.TryGetValue(id, out Printer? current);

                        if (current != null)
                        {
                            EnsureWebSocketAdapter(id, current, ct);
                        }

                        // Ensure polling loop exists (for HTTP fallback)
                        if (!_pollingLoops.ContainsKey(id))
                        {
#pragma warning disable S6612 // Use the loop variable instead of capturing
                            var pollingLoop = Task.Run(() => PollPrinterAsync(id, ct), ct);
#pragma warning restore S6612
                            _pollingLoops.TryAdd(id, pollingLoop);
                            _logger.LogDebug("Started HTTP polling fallback loop for OctoPrint printer {Id}", id);
                        }

                        // Refresh the cached printer row used by PollPrinterAsync's per-tick HTTP
                        // fallback loop, so it doesn't need its own per-tick database read (issue #1763).
                        if (current != null)
                        {
                            PrinterPollingState cacheState = _printerStates.GetOrAdd(id, printerId => new PrinterPollingState
                            {
                                PrinterId = printerId,
                                LastKnownIsOnline = false,
                                LastApiState = "unset"
                            });
                            cacheState.CachedPrinter = current;
                        }
                    }

                    // Remove adapters and polling loops for printers that are no longer OctoPrint
                    var inactiveIds = _webSocketAdapters.Keys.Except(printerIds).ToList();
                    foreach (Guid printerId in inactiveIds)
                    {
                        _webSocketAdapters.TryRemove(printerId, out OctoPrintWebSocketAdapter? adapter);
                        adapter?.Dispose();
                        _pollingLoops.TryRemove(printerId, out _);
                        _printerStates.TryRemove(printerId, out _);
                        _adapterCreationLocks.TryRemove(printerId, out _);
                        _logger.LogDebug("Stopped WebSocket and polling for OctoPrint printer {PrinterId}", printerId);
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
                    _logger.LogError(ex, "Error in OctoPrintPollingService main loop");
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("OctoPrintPollingService main loop cancelled");
        }
    }

    /// <summary>
    /// HTTP polling fallback loop for OctoPrint printer.
    /// Only polls if WebSocket is not connected (fallback mechanism).
    /// Runs every 5 seconds.
    /// </summary>
    private async Task PollPrinterAsync(Guid printerId, CancellationToken ct)
    {
#pragma warning disable S6612 // Capturing printerId in lambda is intentional and safe
        PrinterPollingState state = _printerStates.GetOrAdd(printerId, _ => new PrinterPollingState
        {
            PrinterId = printerId,
            LastKnownIsOnline = false,
            LastApiState = "unset"
        });
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
                    printer = await GetPrinterAsync(printerId, ct);
                    state.CachedPrinter = printer;
                }

                if (printer?.Backend != (int)PrinterBackend.OctoPrint)
                {
                    // Printer is no longer OctoPrint, remove from polling
                    _pollingLoops.TryRemove(printerId, out _);
                    _printerStates.TryRemove(printerId, out _);
                    _adapterCreationLocks.TryRemove(printerId, out _);
                    if (_webSocketAdapters.TryRemove(printerId, out OctoPrintWebSocketAdapter? adapter))
                    {
                        adapter?.Dispose();
                    }

                    return;
                }

                // Get WebSocket adapter for this printer. It may be missing here because an
                // invalidation (printer edited) just tore down the old adapter along with the
                // stale CachedPrinter above — recreate it immediately from the printer row we
                // just resolved, rather than waiting for the next 30-second reconciliation pass
                // to notice the missing adapter and recreate it (issue #1763 follow-up).
                if (!_webSocketAdapters.TryGetValue(printerId, out OctoPrintWebSocketAdapter? wsAdapter) || wsAdapter == null)
                {
                    if (printer != null)
                    {
                        _logger.LogInformation("OctoPrint {PrinterId}: WebSocket adapter missing, recreating", printerId);
                        wsAdapter = EnsureWebSocketAdapter(printerId, printer, ct);
                    }
                    else
                    {
                        _logger.LogWarning("OctoPrint {PrinterId}: WebSocket adapter not found", printerId);
                        await Task.Delay(PollingInterval, ct);
                        continue;
                    }
                }

                // Skip polling if WebSocket is connected (primary transport)
                if (wsAdapter.IsConnected)
                {
                    _logger.LogDebug("OctoPrint {PrinterId}: WebSocket connected, skipping HTTP fallback poll", printerId);
                    await Task.Delay(PollingInterval, ct);
                    continue;
                }

                // Try HTTP polling fallback
                try
                {
                    OctoPrintStatusData? statusData = await wsAdapter.TryHttpPollingFallbackAsync(ct);
                    if (statusData != null)
                    {
                        // Check for state transition from printing to idle/operational for job completion sync
                        string? previousState = state.LastKnownState;
                        string? previousJobName = state.LastKnownJobName;
                        bool stateChanged = statusData.State != previousState;
#pragma warning disable S1244 // Explicit tolerance is appropriate for progress telemetry.
                        bool progressChanged = state.LastKnownProgress is null || statusData.Progress is null
                            ? state.LastKnownProgress != statusData.Progress
                            : Math.Abs(state.LastKnownProgress.Value - statusData.Progress.Value) > 0.01;
#pragma warning restore S1244

                        // Update state tracking (including PreviousState for transition detection)
                        state.PreviousState = previousState;
                        state.LastKnownIsOnline = statusData.IsOnline;
                        state.LastKnownState = statusData.State;
                        state.LastKnownProgress = statusData.Progress;
                        state.LastKnownJobName = statusData.JobName;
                        state.ConsecutiveFailures = 0;
                        state.LastApiState = "responding";

                        // Check for print completion/failure transitions
                        if (stateChanged && previousState != null && statusData.State != null)
                        {
                            await CheckAndSyncJobCompletionAsync(
                                printerId,
                                previousState,
                                statusData.State,
                                previousJobName,
                                ct);
                        }

                        // External print detection: when printer transitions TO "printing" from a
                        // non-printing state, check if a PrintFarmer job exists. If not, create a
                        // synthetic job so the print lifecycle is tracked and completion works.
                        if (stateChanged && PrintJobCompletionService.IsPrintingState(statusData.State)
                                         && !PrintJobCompletionService.IsPrintingState(previousState)
                                         && !state.ExternalJobCreatedForCurrentPrint)
                        {
                            await DetectAndCreateExternalPrintJobAsync(printerId, statusData.JobName, ct);
                            state.ExternalJobCreatedForCurrentPrint = true;
                        }

                        // Reset external job tracking when printer leaves "printing" state
                        if (stateChanged && !PrintJobCompletionService.IsPrintingState(statusData.State))
                        {
                            state.ExternalJobCreatedForCurrentPrint = false;
                        }

                        // Resolve spool info from DB assignment
                        PrinterSpoolInfoDto? spoolInfo = null;
                        try
                        {
                            using IServiceScope spoolScope = _scopeFactory.CreateScope();
                            ManagedSpoolProviderHelper spoolProvider = spoolScope.ServiceProvider.GetRequiredService<ManagedSpoolProviderHelper>();
                            spoolInfo = await spoolProvider.GetManagedSpoolInfoAsync(printer, ct);
                        }
                        catch (Exception spoolEx)
                        {
                            _logger.LogDebug(spoolEx, "OctoPrint {PrinterId}: Failed to resolve spool info", printerId);
                        }

                        // Create cache update (PrinterStatusDto - no HomedAxes)
                        var cacheUpdate = new PrinterStatusDto(
                            Id: printerId,
                            IsOnline: statusData.IsOnline,
                            State: PrinterStateNormalizer.NormalizeState(statusData.State),
                            Progress: statusData.Progress,
                            JobName: statusData.JobName,
                            ThumbnailUrl: statusData.ThumbnailUrl,
                            CameraStreamUrl: statusData.CameraStreamUrl,
                            CameraSnapshotUrl: null,
                            X: statusData.X,
                            Y: statusData.Y,
                            Z: statusData.Z,
                            HotendTemp: statusData.HotendTemp,
                            BedTemp: statusData.BedTemp,
                            HotendTarget: statusData.HotendTarget,
                            BedTarget: statusData.BedTarget,
                            SpoolInfo: spoolInfo,
                            PrintTimeLeftSeconds: statusData.PrintTimeLeftSeconds);

                        // Update cache before broadcasting to clients
                        _statusCacheWriter.UpdateStatus(cacheUpdate, statusData.OriginWatermark);

                        // Create SignalR update (PrinterStatusUpdate - includes HomedAxes)
                        var signalRUpdate = new PrinterStatusUpdate(
                            Id: printerId,
                            IsOnline: statusData.IsOnline,
                            State: PrinterStateNormalizer.NormalizeState(statusData.State),
                            Progress: statusData.Progress,
                            JobName: statusData.JobName,
                            ThumbnailUrl: statusData.ThumbnailUrl,
                            CameraStreamUrl: statusData.CameraStreamUrl,
                            X: statusData.X,
                            Y: statusData.Y,
                            Z: statusData.Z,
                            HotendTemp: statusData.HotendTemp,
                            BedTemp: statusData.BedTemp,
                            HotendTarget: statusData.HotendTarget,
                            BedTarget: statusData.BedTarget,
                            HomedAxes: null,
                            SpoolInfo: spoolInfo,
                            FileName: PrinterStatusDto.ExtractFileName(statusData.JobName));

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
                    }
                }
                catch (Exception ex)
                {
                    state.ConsecutiveFailures++;
                    string apiState = DetermineApiState(ex);
                    state.LastApiState = apiState;

                    _logger.LogDebug(ex, "Failed to poll OctoPrint printer {PrinterId} via HTTP fallback (attempt {StateConsecutiveFailures}, apiState={ApiState})", printerId, state.ConsecutiveFailures, apiState);

                    // After 3 consecutive failures, mark as offline
                    if (state.ConsecutiveFailures >= 3 && state.LastKnownIsOnline)
                    {
                        _logger.LogWarning(
                            "OctoPrint printer {PrinterId} marked offline after {StateConsecutiveFailures} HTTP fallback failures (apiState={ApiState})",
                            printerId, state.ConsecutiveFailures, apiState);
                        state.LastKnownIsOnline = false;

                        // Create cache update (PrinterStatusDto - no HomedAxes)
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

                        // Update cache before broadcasting to clients
                        _statusCacheWriter.UpdateStatus(offlineCacheUpdate, originWatermark: null);

                        // Create SignalR update (PrinterStatusUpdate - includes HomedAxes)
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
                _logger.LogError(ex, "Unexpected error in HTTP polling fallback for OctoPrint printer {PrinterId}", printerId);
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }
    }

    /// <summary>
    /// Determines the API state based on exception type.
    /// </summary>
    private static string DetermineApiState(Exception ex)
    {
        if (ex is HttpRequestException httpEx)
        {
            return httpEx.StatusCode == System.Net.HttpStatusCode.Unauthorized ||
                httpEx.StatusCode == System.Net.HttpStatusCode.Forbidden
                ? "authFail"
                : "noResponse";
        }

        return "noResponse"; // All other exceptions including OperationCanceledException
    }

    /// <summary>
    /// Gets all OctoPrint printers (fully decrypted) from the database.
    /// </summary>
    private async Task<List<Printer>> GetOctoPrintPrintersAsync(CancellationToken ct)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        IPrintersRepository repo = scope.ServiceProvider.GetRequiredService<IPrintersRepository>();
        return await repo.GetByBackendAsync(PrinterBackend.OctoPrint, ct);
    }

    /// <summary>
    /// Gets a printer by ID from the database.
    /// </summary>
    private async Task<Printer?> GetPrinterAsync(Guid printerId, CancellationToken ct)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        IPrintersRepository repo = scope.ServiceProvider.GetRequiredService<IPrintersRepository>();
        return await repo.FindByIdAsync(printerId, ct);
    }

    /// <summary>
    /// Checks for print completion/failure state transitions and synchronizes job status in database.
    /// Called when printer state changes from "printing" to operational/finishing (completion) or error (failure).
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

            _logger.LogInformation("[OctoPrintPollingService] Detected state transition for printer {PrinterId}: {PreviousState} -> {NewState}", printerId, previousState, newState);

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
                    _logger.LogInformation("[OctoPrintPollingService] Print job marked as completed for printer {PrinterId}", printerId);
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
                    _logger.LogWarning("[OctoPrintPollingService] Print job marked as failed for printer {PrinterId} (state: {NewState})", printerId, newState);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[OctoPrintPollingService] Failed to sync job completion for printer {PrinterId}", printerId);
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
                    "[OctoPrintPollingService] External print detected on printer {PrinterId}, created tracking job (file: {FileName})",
                    printerId,
                    fileName ?? "unknown");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[OctoPrintPollingService] Failed to create external print job for printer {PrinterId}", printerId);
        }
    }
}
