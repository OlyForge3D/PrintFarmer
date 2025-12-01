using System.Collections.Concurrent;
using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Printers;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Hubs;
using Farm.Web.Api.Services.Interfaces;
using Microsoft.AspNetCore.SignalR;

namespace Farm.Web.Api.Services;

/// <summary>
/// Background service for OctoPrint real-time status updates.
/// Implements dual-layer architecture: WebSocket primary + HTTP polling fallback (FDM-Monster pattern).
/// Maintains persistent WebSocket connections to each OctoPrint printer's /sockjs/websocket endpoint.
/// Falls back to HTTP polling every 10 seconds if WebSocket connection fails or is unavailable.
/// Broadcasts all status updates via SignalR hub for consistent client experience.
/// </summary>
public sealed class OctoPrintPollingService(
    IHubContext<PrinterHub> hub,
    IServiceScopeFactory scopeFactory,
    IUnifiedLoggingService logger,
    IOctoPrintClient octoPrintClient) : IHostedService, IDisposable
{
    private readonly IUnifiedLoggingService _logger = logger;
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly IOctoPrintClient _octoPrintClient = octoPrintClient;
    private readonly IHubContext<PrinterHub> _hub = hub;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<Guid, OctoPrintWebSocketAdapter> _webSocketAdapters = new();
    private readonly ConcurrentDictionary<Guid, PrinterPollingState> _printerStates = new();
    private readonly ConcurrentDictionary<Guid, Task> _pollingLoops = new();

    // Polling interval for HTTP fallback (10 seconds to match FDM-Monster)
    private static readonly TimeSpan PollingInterval = TimeSpan.FromSeconds(10);

    private Task? _mainLoop;

    /// <summary>
    /// Persistent state for each OctoPrint printer to track changes and avoid redundant updates.
    /// </summary>
    private sealed class PrinterPollingState
    {
        public Guid PrinterId { get; set; }
        public string? LastKnownState { get; set; }
        public double? LastKnownProgress { get; set; }
        public string? LastKnownJobName { get; set; }
        public bool LastKnownIsOnline { get; set; }
        public DateTime LastPollTime { get; set; }
        public int ConsecutiveFailures { get; set; }
        public string? LastApiState { get; set; } // "responding", "authFail", "noResponse"
        public OctoPrintWebSocketAdapter? WebSocketAdapter { get; set; }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("OctoPrintPollingService starting");
#pragma warning disable VSTHRD003 // Avoid awaiting or returning a Task representing work that was not started within this context
        _ = _mainLoop = Task.Run(() => RunAsync(_cts.Token), _cts.Token);
#pragma warning restore VSTHRD003
        return Task.CompletedTask;
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
        _cts?.Dispose();
        foreach (var adapter in _webSocketAdapters.Values)
        {
            adapter?.Dispose();
        }
        _webSocketAdapters.Clear();
        foreach (var loop in _pollingLoops.Values)
        {
            loop?.Dispose();
        }
        _pollingLoops.Clear();
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
                    // Get list of OctoPrint printers from database
                    var printerIds = await GetOctoPrintPrinterIdsAsync(ct);
                    _logger.LogDebug($"OctoPrintPollingService: Found {printerIds.Count} OctoPrint printers");

                    // Ensure WebSocket adapters and polling loops exist for all OctoPrint printers
                    foreach (var id in printerIds)
                    {
                        if (!_webSocketAdapters.ContainsKey(id))
                        {
                            var printer = await GetPrinterAsync(id, ct);
                            if (printer != null)
                            {
                                var adapter = new OctoPrintWebSocketAdapter(
                                    id,
                                    printer,
                                    _logger,
                                    _octoPrintClient,
                                    _hub);

                                _webSocketAdapters.TryAdd(id, adapter);
                                var state = _printerStates.GetOrAdd(id, _ => new PrinterPollingState 
                                { 
                                    PrinterId = id,
                                    LastKnownIsOnline = false,
                                    LastApiState = "unset",
                                    WebSocketAdapter = adapter
                                });
                                state.WebSocketAdapter = adapter;

                                _logger.LogDebug($"Created WebSocket adapter for OctoPrint printer {id}");

                                // Attempt WebSocket connection in background
                                _ = Task.Run(async () =>
                                {
                                    try
                                    {
                                        await adapter.ConnectAsync(ct);
                                    }
                                    catch (Exception ex)
                                    {
                                        _logger.LogDebug(ex, $"WebSocket connection failed for {id}, will use HTTP polling");
                                    }
                                }, ct);
                            }
                        }

                        // Ensure polling loop exists (for HTTP fallback)
                        if (!_pollingLoops.ContainsKey(id))
                        {
#pragma warning disable S6612 // Use the loop variable instead of capturing
                            var pollingLoop = Task.Run(() => PollPrinterAsync(id, ct), ct);
#pragma warning restore S6612
                            _pollingLoops.TryAdd(id, pollingLoop);
                            _logger.LogDebug($"Started HTTP polling fallback loop for OctoPrint printer {id}");
                        }
                    }

                    // Remove adapters and polling loops for printers that are no longer OctoPrint
                    var inactiveIds = _webSocketAdapters.Keys.Except(printerIds).ToList();
                    foreach (var printerId in inactiveIds)
                    {
                        _webSocketAdapters.TryRemove(printerId, out var adapter);
                        adapter?.Dispose();
                        _pollingLoops.TryRemove(printerId, out _);
                        _printerStates.TryRemove(printerId, out _);
                        _logger.LogDebug($"Stopped WebSocket and polling for OctoPrint printer {printerId}");
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
    /// Runs every 10 seconds to match FDM-Monster pattern.
    /// </summary>
    private async Task PollPrinterAsync(Guid printerId, CancellationToken ct)
    {
#pragma warning disable S6612 // Capturing printerId in lambda is intentional and safe
        var state = _printerStates.GetOrAdd(printerId, _ => new PrinterPollingState 
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
                // Get printer details
                Printer? printer = await GetPrinterAsync(printerId, ct);
                if (printer?.Backend != (int)PrinterBackend.OctoPrint)
                {
                    // Printer is no longer OctoPrint, remove from polling
                    _pollingLoops.TryRemove(printerId, out _);
                    _printerStates.TryRemove(printerId, out _);
                    if (_webSocketAdapters.TryRemove(printerId, out var adapter))
                    {
                        adapter?.Dispose();
                    }
                    return;
                }

                // Get WebSocket adapter for this printer
                if (!_webSocketAdapters.TryGetValue(printerId, out var wsAdapter) || wsAdapter == null)
                {
                    _logger.LogWarning($"OctoPrint {printerId}: WebSocket adapter not found");
                    await Task.Delay(PollingInterval, ct);
                    continue;
                }

                // Skip polling if WebSocket is connected (primary transport)
                if (wsAdapter.IsConnected)
                {
                    _logger.LogDebug($"OctoPrint {printerId}: WebSocket connected, skipping HTTP fallback poll");
                    await Task.Delay(PollingInterval, ct);
                    continue;
                }

                // Try HTTP polling fallback
                try
                {
                    var statusData = await wsAdapter.TryHttpPollingFallbackAsync(ct);
                    if (statusData != null)
                    {
                        state.LastKnownIsOnline = statusData.IsOnline;
                        state.LastKnownState = statusData.State;
                        state.LastKnownProgress = statusData.Progress;
                        state.LastKnownJobName = statusData.JobName;
                        state.ConsecutiveFailures = 0;
                        state.LastApiState = "responding";

                        // Broadcast update via SignalR
                        var update = new PrinterStatusUpdate(
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
                            SpoolInfo: null
                        );

                        await _hub.Clients.All.SendAsync("printerupdated", update, ct);
                    }
                }
                catch (Exception ex)
                {
                    state.ConsecutiveFailures++;
                    string apiState = DetermineApiState(ex);
                    state.LastApiState = apiState;

                    _logger.LogDebug(ex, $"Failed to poll OctoPrint printer {printerId} via HTTP fallback (attempt {state.ConsecutiveFailures}, apiState={apiState})");

                    // After 3 consecutive failures, mark as offline
                    if (state.ConsecutiveFailures >= 3 && state.LastKnownIsOnline)
                    {
                        _logger.LogWarning(
                            $"OctoPrint printer {printerId} marked offline after {state.ConsecutiveFailures} HTTP fallback failures " +
                            $"(apiState={apiState})");
                        state.LastKnownIsOnline = false;
                        var offlineUpdate = new PrinterStatusUpdate(
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
                            SpoolInfo: null
                        );

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
                _logger.LogError(ex, $"Unexpected error in HTTP polling fallback for OctoPrint printer {printerId}");
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }
    }



    /// <summary>
    /// Determines the API state based on exception type (FDM-Monster pattern).
    /// </summary>
    private static string DetermineApiState(Exception ex)
    {
        if (ex is HttpRequestException httpEx)
        {
            if (httpEx.StatusCode == System.Net.HttpStatusCode.Unauthorized ||
                httpEx.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                return "authFail";
            }
            return "noResponse";
        }

        if (ex is OperationCanceledException)
        {
            return "noResponse";
        }

        return "noResponse";
    }

    /// <summary>
    /// Gets the list of all OctoPrint printer IDs from the database.
    /// </summary>
    private async Task<List<Guid>> GetOctoPrintPrinterIdsAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IPrintersRepository>();
        var printers = await repo.GetByBackendAsync(PrinterBackend.OctoPrint, ct);
        return printers.Select(p => p.Id).ToList();
    }

    /// <summary>
    /// Gets a printer by ID from the database.
    /// </summary>
    private async Task<Printer?> GetPrinterAsync(Guid printerId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IPrintersRepository>();
        return await repo.FindByIdAsync(printerId, ct);
    }

}
