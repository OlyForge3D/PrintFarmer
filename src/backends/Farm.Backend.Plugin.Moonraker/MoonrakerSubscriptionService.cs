using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Farm.Infrastructure;
using Farm.Infrastructure.Contracts.Printers.Moonraker;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Printers;
using Farm.Infrastructure.Repositories.UnitOfWork;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.SignalR;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Farm.Backend.Plugin.Moonraker;

public sealed class MoonrakerSubscriptionService(
    IHubContext<PrinterHub> hub,
    IServiceScopeFactory scopeFactory,
    ILogger<MoonrakerSubscriptionService> logger,
    IHttpClientFactory httpClientFactory,
    IPrinterStatusCacheWriter statusCacheWriter) : IHostedService, IDisposable, IPrinterConnectionHealthProvider, IPrinterStatusRefreshService
{
    private readonly ILogger<MoonrakerSubscriptionService> _logger = logger;
    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;
    private readonly IPrinterStatusCacheWriter _statusCacheWriter = statusCacheWriter;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<Guid, Task> _loops = new();
    private readonly ConcurrentDictionary<Guid, ConnectionMetrics> _connectionMetrics = new();

    // Persistent state tracking for each printer
    private readonly ConcurrentDictionary<Guid, PrinterState> _printerStates = new();
    private readonly ConcurrentDictionary<Guid, int> _parseErrorCounts = new();
    private readonly ConcurrentDictionary<Guid, DateTime> _lastHttpPollTimes = new();
    private readonly ConcurrentDictionary<Guid, DateTime> _lastStatusUpdateTimes = new();

    // Track polling strategy per printer based on Klippy state
    private readonly ConcurrentDictionary<Guid, PollingMode> _pollingModes = new();

    // Track Klippy ready state per printer (for IsOnline determination)
    private readonly ConcurrentDictionary<Guid, bool> _klippyReadyState = new();

    // Connection health tracking for diagnostics API
    private readonly ConcurrentDictionary<Guid, PrinterConnectionHealth> _connectionHealth = new();

    // Grace timers for delaying offline broadcasts (cancel if printer recovers quickly)
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _offlineGraceTimers = new();

    private enum PollingMode
    {
        WebSocketRealTime,  // Use WebSocket for real-time updates (normal operation)
        HttpPollingOnly,    // Use HTTP polling only (Klippy disconnected/shutdown)
        WebSocketWithFallback // Use WebSocket but ready to fallback (transition states)
    }

    private Task? _mainLoop;

    // Connection configuration constants
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);
    private const int MaxReconnectAttempts = 10;
    private const int MaxParseErrorsBeforeFallback = 5;
    private static readonly TimeSpan HttpPollInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StaleConnectionThreshold = TimeSpan.FromSeconds(60); // Trigger fallback if no status updates for 60 seconds
    private static readonly TimeSpan OfflineGracePeriod = TimeSpan.FromSeconds(5); // Wait before declaring offline on klippy disconnect
    private const int ConsecutiveFailuresBeforeOffline = 2; // Require N failures before broadcasting offline

    // Client identification for Moonraker
    private const string ClientName = "PrintFarmer";
    private const string ClientVersion = "1.0.0";

    /// <summary>
    /// Starts the Moonraker subscription service background tasks.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token (unused in this implementation).</param>
    /// <returns>A completed task.</returns>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("MoonrakerSubscriptionService starting");
        _mainLoop = Task.Run(() => RunAsync(_cts.Token), _cts.Token);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops the Moonraker subscription service and cancels all background tasks.
    /// Handles cleanup of running subscription loops and resource disposal.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token (unused in this implementation).</param>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // Signal cancellation to background loops (ignore if already disposed)
        try
        {
            await _cts.CancelAsync();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed/cancelled – safe to ignore during shutdown
        }

        List<Task> tasks = new(_loops.Values);
        if (_mainLoop is not null)
        {
            tasks.Add(_mainLoop);
        }

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown
        }
        catch (AggregateException aex) when (aex.InnerExceptions.All(e => e is OperationCanceledException))
        {
            // Also fine during shutdown
        }
        catch (Exception ex)
        {
            // Don't fail stop on background task errors
            _logger.LogDebug(ex, "Ignoring background task error during StopAsync");
        }
    }

    /// <summary>
    /// Disposes the service and releases the cancellation token source.
    /// </summary>
    public void Dispose()
    {
        try
        {
            // Only cancel if not already cancelled to avoid VSTHRD103
            if (!_cts.IsCancellationRequested)
            {
                _cts.Cancel();
            }
        }
        catch
        { /* ignore during dispose */
        }

        _cts.Dispose();
    }

    /// <inheritdoc/>
    public async Task RefreshPrinterStatusAsync(Guid printerId, int delayMs = 750, CancellationToken ct = default)
    {
        try
        {
            if (delayMs > 0)
            {
                await Task.Delay(delayMs, ct);
            }

            await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
            IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            Printer? printer = await unitOfWork.Printers.FindByIdAsync(printerId, ct);

            if (printer is null || printer.Backend != (int)PrinterBackend.Moonraker)
            {
                return;
            }

            IMoonrakerClient moonrakerClient = scope.ServiceProvider.GetRequiredService<IMoonrakerClient>();
            PrinterCompositeStatus compositeStatus = await moonrakerClient.GetCompositeStatusAsync(printer.BackendUrl, ct);

            if (compositeStatus is not { IsOnline: true })
            {
                return;
            }

            PrinterSpoolInfoDto? spoolInfo = await GetSpoolInfoAsync(printer.BackendUrl, ct);

            PrinterStatusUpdate statusUpdate = new(
                printerId,
                compositeStatus.IsOnline,
                PrinterStateNormalizer.NormalizeState(compositeStatus.State),
                compositeStatus.Progress,
                compositeStatus.JobName,
                compositeStatus.ThumbnailUrl,
                compositeStatus.CameraStreamUrl,
                compositeStatus.X,
                compositeStatus.Y,
                compositeStatus.Z,
                compositeStatus.HotendTemp,
                compositeStatus.BedTemp,
                compositeStatus.HotendTarget,
                compositeStatus.BedTarget,
                null,
                spoolInfo,
                FileName: PrinterStatusDto.ExtractFileName(compositeStatus.JobName));

            await hub!.Clients.All.SendAsync("printerupdated", statusUpdate, ct);
            _lastHttpPollTimes[printerId] = DateTime.UtcNow;

            _logger.LogInformation(
                "Post-dispatch immediate refresh for printer {PrinterId}: State={State}",
                printerId, compositeStatus.State);
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Post-dispatch status refresh failed for printer {PrinterId}", printerId);
        }
    }

    /// <summary>
    /// Main background loop that continuously enumerates printers and checks for stale connections.
    /// </summary>
    /// <param name="ct">Cancellation token to stop the loop.</param>
    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await EnumerateAndStartSubscriptionsAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error enumerating printers for subscription");
            }

            try
            {
                await CheckForStaleConnectionsAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking for stale connections");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Enumerates all enabled Moonraker-backed printers from the database and starts subscription loops for each.
    /// Restarts subscription loops for printers that have exhausted reconnection attempts.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    private async Task EnumerateAndStartSubscriptionsAsync(CancellationToken ct)
    {
        // Using an async scope while awaiting EF Core ToListAsync is intentional here.
        // The scope lifetime matches the query and is disposed immediately after.
#pragma warning disable IDISP013 // Await in using
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<Farm.Infrastructure.Repositories.UnitOfWork.IUnitOfWork>();
        IPrintersRepository printersRepo = unitOfWork.Printers;

        // Only subscribe to ENABLED Moonraker-backed printers
        // Note: Only Moonraker supports real-time WebSocket subscriptions
        // PrusaLink and SDCP are polled via HTTP on-demand, not continuously
        List<Printer> allPrinters = await printersRepo.GetByBackendAsync(PrinterBackend.Moonraker, ct);
        List<Printer> enabledPrinters = allPrinters.Where(p => p.IsEnabled).ToList();

        foreach (Printer? p in enabledPrinters)
        {
            // Check if there's an existing subscription loop for this printer
            if (_loops.TryGetValue(p.Id, out Task? existingTask))
            {
                // If the existing task has completed (either successfully or via exhausted retries),
                // remove it so we can start a fresh subscription loop with reset retry count
                if (existingTask.IsCompleted)
                {
                    _loops.TryRemove(p.Id, out _);
                    _connectionMetrics.TryRemove(p.Id, out _); // Reset metrics (reconnect attempts) too
                    _logger.LogInformation(
                        "Restarting subscription loop for printer {PName} ({PId}) - previous loop completed", p.Name, p.Id);
                }
            }

            _ = _loops.GetOrAdd(p.Id, _ => Task.Run(() => SubscribePrinterLoopAsync(p, ct), ct));
        }
#pragma warning restore IDISP013
    }

    /// <summary>
    /// Checks for stale WebSocket connections that haven't received updates within the threshold.
    /// Triggers HTTP polling fallback for stale connections.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    private async Task CheckForStaleConnectionsAsync(CancellationToken ct)
    {
        DateTime now = DateTime.UtcNow;
        DateTime staleThreshold = now - StaleConnectionThreshold;

        foreach ((Guid printerId, DateTime lastUpdate) in _lastStatusUpdateTimes.ToList())
        {
            if (lastUpdate < staleThreshold)
            {
                _logger.LogWarning("Detected stale connection for printer {PrinterId}, last update was {LastUpdate:O}. Triggering HTTP polling fallback.", printerId, lastUpdate);

                // Find the printer to trigger fallback
                await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
                IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<Farm.Infrastructure.Repositories.UnitOfWork.IUnitOfWork>();
                Printer? printer = await unitOfWork.Printers.FindByIdAsync(printerId, ct);

                if (printer != null)
                {
                    await TriggerHttpPollingFallbackAsync(printer, ct);
                }
            }
        }
    }

    /// <summary>
    /// Converts an HTTP base URL to a WebSocket URI for Moonraker connection.
    /// Handles URL normalization, port assignment (default 7125), and scheme conversion (http/https to ws/wss).
    /// </summary>
    /// <param name="httpBase">The HTTP base URL of the Moonraker server.</param>
    /// <returns>The WebSocket URI to connect to.</returns>
    /// <exception cref="ArgumentException">Thrown when httpBase is null or whitespace.</exception>
    private static Uri BuildWsUri(string httpBase)
    {
        if (string.IsNullOrWhiteSpace(httpBase))
        {
            throw new ArgumentException("Missing base URL", nameof(httpBase));
        }

        string trimmed = httpBase.TrimEnd('/');
        if (!trimmed.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = "http://" + trimmed;
        }

        UriBuilder ub = new(trimmed);
        if (ub.Port == -1)
        {
            ub.Port = 7125;
        }

        ub.Scheme = ub.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws";
        ub.Path = "/websocket";
        return ub.Uri;
    }

    /// <summary>
    /// Main WebSocket subscription loop for a single printer.
    /// Handles connection establishment, identification, object subscription, heartbeat, and message processing.
    /// Implements exponential backoff and automatic reconnection on failure.
    /// </summary>
    /// <param name="printer">The printer to subscribe to.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task SubscribePrinterLoopAsync(Printer printer, CancellationToken ct)
    {
        Guid id = printer.Id;
        ConnectionMetrics metrics = _connectionMetrics.GetOrAdd(id, _ => new ConnectionMetrics());

        _logger.LogInformation("Starting subscription loop for printer {PrinterName} ({PrinterId})", printer.Name, id);

        while (!ct.IsCancellationRequested && metrics.ReconnectAttempts < MaxReconnectAttempts)
        {
            ClientWebSocket? ws = null;
            CancellationTokenSource? heartbeatCts = null;
            Task? heartbeatTask = null;

            try
            {
                // Re-check backend on each iteration in case it changed
                if (!await ValidatePrinterBackendAsync(id, ct))
                {
                    _logger.LogInformation("Printer {Id} no longer uses Moonraker backend, stopping subscription", id);
                    return;
                }

                // Apply exponential backoff if this is a retry
                if (metrics.ReconnectAttempts > 0)
                {
                    TimeSpan backoffDelay = metrics.GetNextBackoffDelay();
                    _logger.LogInformation("Backing off for {BackoffDelayTotalSeconds}s before reconnecting to printer {PrinterName} (attempt {Value2}/{MaxReconnectAttempts})", backoffDelay.TotalSeconds, printer.Name, metrics.ReconnectAttempts + 1, MaxReconnectAttempts);

                    try
                    {
                        await Task.Delay(backoffDelay, ct);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        return;
                    }
                }

                // Establish WebSocket connection
                Uri uri = BuildWsUri(printer.BackendUrl);
                ws = new ClientWebSocket();

                _logger.LogDebug("Connecting to Moonraker WebSocket at {Uri} for printer {PrinterName}", uri, printer.Name);
                await ws.ConnectAsync(uri, ct);

                _logger.LogInformation("WebSocket connected to printer {PrinterName} ({PrinterId})", printer.Name, id);

                // Step 1: Identify this connection to Moonraker
                await SendConnectionIdentificationAsync(ws, ct);

                // Step 2: Subscribe to printer objects
                await SendObjectSubscriptionAsync(ws, ct);

                // Step 2b: Query initial toolhead data (especially homed_axes) since Moonraker only sends incremental updates
                try
                {
                    _logger.LogDebug("Querying initial toolhead data for printer {PrinterName} to get homed_axes", printer.Name);
                    await QueryAndCacheToolheadDataAsync(printer.Id, printer.BackendUrl, ct);
                }
                catch (Exception ex)
                {
                    // Don't fail startup over this - we'll query on-demand later
                    _logger.LogWarning(ex, "Failed to query initial toolhead data for printer {PrinterName}", printer.Name);
                }

                // Step 3: Start heartbeat mechanism
                heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                heartbeatTask = StartHeartbeatAsync(ws, printer, heartbeatCts.Token);

                // Connection successful - reset metrics
                metrics.Reset();
                CancelOfflineGraceTimer(id);
                RecordHealthTransition(id, printer.Name, PrinterConnectionState.Connected, "WebSocket connected");

                // Initialize status update tracking
                _lastStatusUpdateTimes[id] = DateTime.UtcNow;

                _logger.LogInformation("Successfully established monitored connection to printer {PrinterName}", printer.Name);

                // Step 4: Message processing loop
                await ProcessWebSocketMessagesAsync(ws, printer, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                _logger.LogDebug("Subscription loop for printer {PrinterName} cancelled during shutdown", printer.Name);
                break;
            }
            catch (Exception ex)
            {
                metrics.IncrementAttempts();

                if (MoonrakerErrors.IsFatalError(ex))
                {
                    _logger.LogError(ex, "Fatal error in subscription loop for printer {PrinterName}, stopping reconnection attempts", printer.Name);
                    break;
                }
                else if (MoonrakerErrors.IsTransientError(ex))
                {
                    _logger.LogWarning(ex, "Transient error for printer {PrinterName}, will retry (attempt {MetricsReconnectAttempts}/{MaxReconnectAttempts})", printer.Name, metrics.ReconnectAttempts, MaxReconnectAttempts);
                }
                else
                {
                    _logger.LogError(ex, "Unexpected error for printer {PrinterName} (attempt {MetricsReconnectAttempts}/{MaxReconnectAttempts})", printer.Name, metrics.ReconnectAttempts, MaxReconnectAttempts);
                }

                // Only broadcast offline after consecutive failures threshold
                metrics.RecordDisconnect();
                RecordHealthTransition(id, printer.Name, PrinterConnectionState.Reconnecting, $"WS error: {ex.GetType().Name}");
                if (metrics.ConsecutiveFailures >= ConsecutiveFailuresBeforeOffline)
                {
                    await SendOfflineStatusAsync(id, ct);
                    RecordHealthTransition(id, printer.Name, PrinterConnectionState.Offline, $"Failed {metrics.ConsecutiveFailures} consecutive times");
                }
            }
            finally
            {
                // Cleanup resources
                if (heartbeatCts != null && !heartbeatCts.IsCancellationRequested)
                {
                    await heartbeatCts.CancelAsync();
                }

                try
                {
                    await (heartbeatTask ?? Task.CompletedTask);
                }
                catch
                {
                }

                heartbeatCts?.Dispose();

                try
                {
                    ws?.Dispose();
                }
                catch
                {
                }
            }
        }

        if (metrics.ReconnectAttempts >= MaxReconnectAttempts)
        {
            _logger.LogError("Exhausted all reconnection attempts ({MaxReconnectAttempts}) for printer {PrinterName}, giving up", MaxReconnectAttempts, printer.Name);
            await SendOfflineStatusAsync(id, ct);
            RecordHealthTransition(id, printer.Name, PrinterConnectionState.Offline, $"Exhausted {MaxReconnectAttempts} reconnection attempts");
        }

        _logger.LogInformation("Subscription loop ended for printer {PrinterName} ({PrinterId})", printer.Name, id);
    }

    // Helper methods for improved connection management

    /// <summary>
    /// Validates that a printer still exists in the database and uses the Moonraker backend.
    /// </summary>
    /// <param name="printerId">The ID of the printer to validate.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the printer exists and uses Moonraker backend; otherwise false.</returns>
    private async Task<bool> ValidatePrinterBackendAsync(Guid printerId, CancellationToken ct)
    {
        try
        {
            await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
            IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<Farm.Infrastructure.Repositories.UnitOfWork.IUnitOfWork>();
            Printer? current = await unitOfWork.Printers.FindByIdAsync(printerId, ct);

            if (current is null)
            {
                _logger.LogInformation("Printer {PrinterId} was removed from database", printerId);
                return false;
            }

            if (current.Backend != (int)Farm.Infrastructure.Domain.PrinterBackend.Moonraker)
            {
                _logger.LogInformation("Printer {PrinterId} backend changed from Moonraker (Backend={CurrentBackend})", printerId, current.Backend);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to validate printer backend for {PrinterId}: {Message}", printerId, ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Sends connection identification to Moonraker to identify this client.
    /// </summary>
    /// <param name="ws">The WebSocket connection.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task SendConnectionIdentificationAsync(ClientWebSocket ws, CancellationToken ct)
    {
        try
        {
            JsonRpcRequest identifyRequest = new()
            {
                Method = "server.connection.identify",
                Params = new
                {
                    client_name = ClientName,
                    version = ClientVersion,
                    type = "web_client"
                },
                Id = 100
            };

            string identifyJson = JsonSerializer.Serialize(identifyRequest);
            byte[] identifyBytes = Encoding.UTF8.GetBytes(identifyJson);
            await ws.SendAsync(identifyBytes, WebSocketMessageType.Text, endOfMessage: true, ct);

            _logger.LogDebug("Sent connection identification to Moonraker: {ClientName} v{ClientVersion}", ClientName, ClientVersion);
        }
        catch (Exception ex)
        {
            // Don't throw - identification is optional
            _logger.LogWarning(ex, "Failed to send connection identification");
        }
    }

    /// <summary>
    /// Sends subscription request to Moonraker for all available printer objects (like Mainsail does).
    /// Queries the actual list of available objects and subscribes to all of them (minus blocklist),
    /// ensuring full state coverage for any Moonraker configuration without hardcoding object names.
    /// </summary>
    /// <param name="ws">The WebSocket connection.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task SendObjectSubscriptionAsync(ClientWebSocket ws, CancellationToken ct)
    {
        // Step 1: Query the list of all available objects from Moonraker
        JsonRpcRequest listRequest = new()
        {
            Method = "printer.objects.list",
            Params = new { },
            Id = 102
        };

        string listJson = JsonSerializer.Serialize(listRequest);
        byte[] listBytes = Encoding.UTF8.GetBytes(listJson);
        await ws.SendAsync(listBytes, WebSocketMessageType.Text, endOfMessage: true, ct);

        _logger.LogDebug("Sent printer.objects.list request to Moonraker to discover available objects");

        // Step 2: Read the response to get the list of available objects
        byte[] buffer = new byte[64 * 1024];
        string? objectsListJson = null;

        // Simple receive loop to get the objects.list response (ID 102)
        TimeSpan receiveTimeout = TimeSpan.FromSeconds(5);
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(receiveTimeout);

        try
        {
            StringBuilder sb = new();
            while (ws.State == WebSocketState.Open && !cts.Token.IsCancellationRequested)
            {
                WebSocketReceiveResult result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
                if (result.MessageType == WebSocketMessageType.Text)
                {
                    sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                    if (result.EndOfMessage)
                    {
                        string message = sb.ToString();
                        sb.Clear();

                        // Check if this is the objects.list response (ID 102)
                        using JsonDocument doc = JsonDocument.Parse(message);
                        if (doc.RootElement.TryGetProperty("id", out JsonElement idElem) && idElem.GetInt32() == 102)
                        {
                            objectsListJson = message;
                            _logger.LogDebug("Received objects.list response from Moonraker");
                            break;
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Timeout waiting for objects.list response");
            throw;
        }

        // Step 3: Parse the objects list and build subscription params
        Dictionary<string, string[]?> subscriptionObjects = new Dictionary<string, string[]?>();

        if (!string.IsNullOrEmpty(objectsListJson))
        {
            using JsonDocument doc = JsonDocument.Parse(objectsListJson);
            if (doc.RootElement.TryGetProperty("result", out JsonElement resultElem) &&
                resultElem.TryGetProperty("objects", out JsonElement objectsElem) &&
                objectsElem.ValueKind == JsonValueKind.Array)
            {
                string[] blocklist = new[] { "menu" }; // Objects to skip (same as Mainsail)
                List<string> subscribedObjects = [];
                List<string> skippedObjects = [];

                foreach (JsonElement objElem in objectsElem.EnumerateArray())
                {
                    if (objElem.ValueKind == JsonValueKind.String)
                    {
                        string objectName = objElem.GetString() ?? string.Empty;
                        string objectType = objectName.Split(' ')[0]; // Get base type (e.g., "extruder" from "extruder 0")

                        // Skip blocklisted object types
                        if (!blocklist.Contains(objectType))
                        {
                            subscriptionObjects[objectName] = null; // null = subscribe to all properties
                            subscribedObjects.Add(objectName);
                        }
                        else
                        {
                            skippedObjects.Add(objectName);
                        }
                    }
                }

                _logger.LogInformation(
                    "Discovered {ObjectCount} objects from Moonraker, subscribing to {SubscriptionCount}",
                    objectsElem.GetArrayLength().ToString(),
                    subscribedObjects.Count.ToString());

                _logger.LogDebug("Subscribing to objects: {SubscribedObjects}", string.Join(", ", subscribedObjects));

                if (skippedObjects.Count > 0)
                {
                    _logger.LogDebug("Skipped blocklisted objects: {SkippedObjects}", string.Join(", ", skippedObjects));
                }
            }
        }

        // Step 4: Send the subscription request with discovered objects
        ObjectSubscriptionRequest subscriptionParams = new()
        {
            Objects = subscriptionObjects
        };

        JsonRpcRequest subscriptionRequest = new()
        {
            Method = "printer.objects.subscribe",
            Params = subscriptionParams,
            Id = 101
        };

        string subJson = JsonSerializer.Serialize(subscriptionRequest);
        byte[] subBytes = Encoding.UTF8.GetBytes(subJson);
        await ws.SendAsync(subBytes, WebSocketMessageType.Text, endOfMessage: true, ct);

        _logger.LogInformation(
            "Sent subscription request for {ObjectCount} objects to Moonraker: {ObjectList}",
            subscriptionObjects.Count.ToString(),
            string.Join(", ", subscriptionObjects.Keys));
    }

    /// <summary>
    /// Starts a heartbeat mechanism that sends ping frames to the WebSocket at regular intervals.
    /// Keeps the connection alive and detects broken connections.
    /// </summary>
    /// <param name="ws">The WebSocket connection.</param>
    /// <param name="printer">The printer being monitored (for logging).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task representing the heartbeat loop.</returns>
    private Task StartHeartbeatAsync(ClientWebSocket ws, Printer printer, CancellationToken ct)
    {
        return Task.Run(
            async () =>
        {
            _logger.LogDebug("Starting heartbeat for printer {PrinterName}", printer.Name);

            try
            {
                while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
                {
                    await Task.Delay(HeartbeatInterval, ct);

                    if (ws.State != WebSocketState.Open)
                    {
                        break;
                    }

                    // Send ping frame
                    try
                    {
                        byte[] pingData = Encoding.UTF8.GetBytes($"ping-{DateTime.UtcNow:O}");
                        await ws.SendAsync(pingData, WebSocketMessageType.Text, endOfMessage: true, ct);
                        _logger.LogDebug("Sent heartbeat ping to printer {PrinterName}", printer.Name);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Heartbeat ping failed for printer {PrinterName}", printer.Name);
                        break;
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                _logger.LogDebug("Heartbeat cancelled for printer {PrinterName}", printer.Name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Heartbeat error for printer {PrinterName}", printer.Name);
            }
        }, ct);
    }

    /// <summary>
    /// Main message processing loop for a WebSocket connection.
    /// Receives JSON-RPC messages and dispatches them to appropriate handlers.
    /// </summary>
    /// <param name="ws">The WebSocket connection.</param>
    /// <param name="printer">The printer being monitored.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task ProcessWebSocketMessagesAsync(ClientWebSocket ws, Printer printer, CancellationToken ct)
    {
        byte[] buffer = new byte[64 * 1024];
        StringBuilder sb = new();

        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            _ = sb.Clear();
            WebSocketReceiveResult result;

            try
            {
                do
                {
                    result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        _logger.LogInformation("WebSocket close received from printer {PrinterName}", printer.Name);
                        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None);
                        return;
                    }

                    _ = sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                }
                while (!result.EndOfMessage);
            }
            catch (WebSocketException wsEx)
            {
                _logger.LogWarning(wsEx, "WebSocket receive error for printer {PrinterName}: {Error}", printer.Name, wsEx.Message);
                throw;
            }

            if (sb.Length == 0)
            {
                continue;
            }

            try
            {
                await ProcessJsonRpcMessageAsync(sb.ToString(), printer, ct);
            }
            catch (JsonException jsonEx)
            {
                _logger.LogWarning(
                    jsonEx,
                    "Failed to parse JSON message from printer {PrinterName}: {Message}",
                    printer.Name,
                    sb.ToString()[..Math.Min(200, sb.Length)]);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing message from printer {PrinterName}", printer.Name);
            }
        }
    }

    /// <summary>
    /// Parses a JSON-RPC message and determines whether it's a response or notification.
    /// Dispatches to appropriate handler methods.
    /// </summary>
    /// <param name="message">The JSON-RPC message string.</param>
    /// <param name="printer">The printer being monitored.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task ProcessJsonRpcMessageAsync(string message, Printer printer, CancellationToken ct)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(message);
            JsonElement root = doc.RootElement;

            // Reset parse error count on successful JSON parsing - this indicates WebSocket connection is healthy
            _ = _parseErrorCounts.TryRemove(printer.Id, out _);

            // Check if this is a JSON-RPC response (has "id" field)
            if (root.TryGetProperty("id", out _))
            {
                // Response handling extracted to reduce nesting
                await HandleJsonRpcResponseAsync(root, message, printer, ct);
            }

            // Check if this is a JSON-RPC notification (has "method" field but no "id")
            else if (root.TryGetProperty("method", out JsonElement methodProp))
            {
                // Notification handling extracted to reduce nesting
                await HandleJsonRpcNotificationAsync(methodProp, root, printer, ct);
            }
            else
            {
                _logger.LogWarning("Received unknown JSON-RPC message from printer {PrinterName}: {Message}", printer.Name, message);
            }
        }
        catch (JsonException ex)
        {
            _logger.LogError("Failed to parse JSON message from printer {PrinterName}: {Message}. Message: {Message1}", printer.Name, ex.Message, message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing message from printer {PrinterName}. Message: {Message}", printer.Name, message);
        }
    }

    /// <summary>
    /// Handles JSON-RPC response messages from Moonraker.
    /// Processes subscription acknowledgements and handles JSON-RPC errors.
    /// Triggers HTTP polling fallback on repeated parse errors.
    /// </summary>
    /// <param name="root">The parsed JSON root element.</param>
    /// <param name="message">The original JSON-RPC message string.</param>
    /// <param name="printer">The printer being monitored.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task HandleJsonRpcResponseAsync(JsonElement root, string message, Printer printer, CancellationToken ct)
    {
        try
        {
            JsonRpcResponse? jsonRpcResponse = JsonSerializer.Deserialize<JsonRpcResponse>(message);
            if (jsonRpcResponse?.Error != null)
            {
                _logger.LogWarning("JSON-RPC error from printer {PrinterName}: {Message} (Code: {Code})", printer.Name, jsonRpcResponse.Error.Message, jsonRpcResponse.Error.Code);

                // Track JSON-RPC parse errors (code -32700) and trigger fallback if threshold exceeded
                if (jsonRpcResponse.Error.Code == -32700)
                {
                    _ = _parseErrorCounts.AddOrUpdate(printer.Id, 1, (key, value) => value + 1);
                    int errorCount = _parseErrorCounts[printer.Id];

                    if (errorCount >= MaxParseErrorsBeforeFallback)
                    {
                        _logger.LogWarning("JSON-RPC parse error threshold ({MaxParseErrorsBeforeFallback}) exceeded for printer {PrinterName}. Triggering HTTP polling fallback.", MaxParseErrorsBeforeFallback, printer.Name);
                        await TriggerHttpPollingFallbackAsync(printer, ct);
                    }
                }

                return;
            }

            // Handle subscription acknowledgement which carries current state
            if (root.TryGetProperty("result", out JsonElement res) && res.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("id", out JsonElement idProp) && idProp.GetInt32() == 101 &&
                res.TryGetProperty("status", out JsonElement statusObj))
            {
                _logger.LogDebug("Processing initial status from subscription acknowledgement for printer {PrinterName}", printer.Name);
                await ProcessStatusUpdateAsync(statusObj, printer.Id, printer.BackendUrl, null, null, ct);
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning("Failed to parse JSON-RPC response from printer {PrinterName}: {Message}", printer.Name, ex.Message);

            // Track parse errors and trigger fallback if threshold exceeded
            _ = _parseErrorCounts.AddOrUpdate(printer.Id, 1, (key, value) => value + 1);
            int errorCount = _parseErrorCounts[printer.Id];

            if (errorCount >= MaxParseErrorsBeforeFallback)
            {
                _logger.LogWarning("Parse error threshold ({MaxParseErrorsBeforeFallback}) exceeded for printer {PrinterName}. Triggering HTTP polling fallback.", MaxParseErrorsBeforeFallback, printer.Name);
                await TriggerHttpPollingFallbackAsync(printer, ct);
            }
        }
    }

    /// <summary>
    /// Handles JSON-RPC notification messages from Moonraker.
    /// Processes status updates, Klippy state changes (ready, disconnected, shutdown).
    /// </summary>
    /// <param name="methodProp">The JSON element containing the method name.</param>
    /// <param name="root">The parsed JSON root element.</param>
    /// <param name="printer">The printer being monitored.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task HandleJsonRpcNotificationAsync(JsonElement methodProp, JsonElement root, Printer printer, CancellationToken ct)
    {
        string? method = methodProp.GetString();
        _logger.LogDebug("Received notification {Method} from printer {PrinterName}", method, printer.Name);

        switch (method)
        {
            case "notify_status_update":
                if (root.TryGetProperty("params", out JsonElement p) && p.ValueKind == JsonValueKind.Array && p.GetArrayLength() > 0)
                {
                    _logger.LogDebug(
                        "Processing notify_status_update for printer {PrinterName}. Status data: {StatusData}",
                        printer.Name,
                        p[0].GetRawText());

                    await ProcessStatusUpdateAsync(p[0], printer.Id, printer.BackendUrl, null, null, ct);
                }

                break;

            case "notify_klippy_disconnected":
                _logger.LogWarning("Klippy disconnected for printer {PrinterName}, starting offline grace period", printer.Name);
                _klippyReadyState[printer.Id] = false;
                SetPollingMode(printer.Id, PollingMode.HttpPollingOnly, "Klippy disconnected");
                RecordHealthTransition(printer.Id, printer.Name, PrinterConnectionState.Reconnecting, "Klippy disconnected");
                StartOfflineGraceTimer(printer.Id, printer.Name, ct);
                break;

            case "notify_klippy_ready":
                _logger.LogInformation("Klippy ready for printer {PrinterName}, switching to WebSocket real-time mode", printer.Name);
                _klippyReadyState[printer.Id] = true;
                SetPollingMode(printer.Id, PollingMode.WebSocketRealTime, "Klippy ready");
                CancelOfflineGraceTimer(printer.Id);
                RecordHealthTransition(printer.Id, printer.Name, PrinterConnectionState.Connected, "Klippy ready");
                break;

            case "notify_klippy_shutdown":
                _logger.LogWarning("Klippy shutdown for printer {PrinterName}, switching to HTTP polling mode", printer.Name);
                _klippyReadyState[printer.Id] = false;
                SetPollingMode(printer.Id, PollingMode.HttpPollingOnly, "Klippy shutdown");
                await SendShutdownStatusAsync(printer.Id, ct);
                break;

            default:
                _logger.LogDebug("Unhandled notification method {Method} from printer {PrinterName}", method, printer.Name);
                break;
        }
    }

    /// <summary>
    /// Processes a status update from Moonraker containing incremental printer state changes.
    /// Dispatches to specific handlers for toolhead, extruder, bed, and state updates.
    /// Sends individual focused events and a consolidated status update via SignalR.
    /// </summary>
    /// <param name="statusObj">The status JSON element from Moonraker.</param>
    /// <param name="printerId">The ID of the printer being updated.</param>
    /// <param name="serverUrl">The Moonraker server URL (for fetching additional data).</param>
    /// <param name="cameraStreamUrl">The camera stream URL from printer configuration.</param>
    /// <param name="thumbnailUrl">The thumbnail URL from printer configuration.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task ProcessStatusUpdateAsync(JsonElement statusObj, Guid printerId, string serverUrl, string? cameraStreamUrl, string? thumbnailUrl, CancellationToken ct)
    {
        // Get or create persistent state for this printer
        PrinterState state = _printerStates.GetOrAdd(printerId, _ => new PrinterState());

        // Store camera and thumbnail URLs if provided
        if (!string.IsNullOrEmpty(cameraStreamUrl))
        {
            state.CameraStreamUrl = cameraStreamUrl;
        }

        if (!string.IsNullOrEmpty(thumbnailUrl))
        {
            state.ThumbnailUrl = thumbnailUrl;
        }

        _logger.LogDebug("Processing status update for printer {PrinterId}. Raw status: {StatusObjGetRawText}", printerId, statusObj.GetRawText());

        // Initialize klippy ready state from webhooks if not yet set
        // This handles the initial subscription response which contains the current klippy state
        if (!_klippyReadyState.ContainsKey(printerId) &&
            statusObj.TryGetProperty("webhooks", out JsonElement wh) &&
            wh.TryGetProperty("state", out JsonElement ws) && ws.ValueKind == JsonValueKind.String)
        {
            string? webhooksState = ws.GetString();
            bool isReady = webhooksState == "ready";
            _klippyReadyState[printerId] = isReady;
            _logger.LogInformation("Initialized klippyReadyState for printer {PrinterId}: {IsReady} (webhooks.state={WebhooksState})", printerId, isReady, webhooksState);
        }

        // Process each object type separately by dispatching to handler methods
        // This aligns with Moonraker's incremental update model where each notification
        // contains only the objects that have changed
        if (statusObj.TryGetProperty("toolhead", out JsonElement th))
        {
            await HandleToolheadUpdateAsync(printerId, state, th, ct);
        }

        if (statusObj.TryGetProperty("extruder", out JsonElement ex))
        {
            await HandleExtruderUpdateAsync(printerId, state, ex, ct);
        }

        if (statusObj.TryGetProperty("heater_bed", out JsonElement hb))
        {
            await HandleHeaterBedUpdateAsync(printerId, state, hb, ct);
        }

        // MMU (Happy Hare) status updates
        if (statusObj.TryGetProperty("mmu", out JsonElement mmu))
        {
            HandleMmuUpdate(printerId, state, mmu);
        }

        // Qidibox filament box detection and updates
        // Qidibox uses "box_stepper slotN" objects + "save_variables" for slot data
        await HandleQidiboxUpdatesAsync(printerId, state, statusObj, serverUrl, ct);

        // AFC (BoxTurtle/NightOwl/QuattroBox) filament changer detection and updates
        // AFC uses "AFC" Moonraker object + "AFC_stepper <lane>" per-lane objects
        HandleAfcUpdates(printerId, state, statusObj);

        // State/progress updates can come from multiple sources (display_status, print_stats, webhooks)
        if (statusObj.TryGetProperty("display_status", out _) ||
            statusObj.TryGetProperty("print_stats", out _) ||
            statusObj.TryGetProperty("webhooks", out _))
        {
            await HandleStateUpdateAsync(printerId, state, statusObj, ct);
        }

        // Get spool information for consolidated update
        PrinterSpoolInfoDto? spoolInfo = await GetSpoolInfoAsync(serverUrl, ct);

        // Send consolidated status update with persistent state for offline status and overall sync
        await EmitConsolidatedStatusAsync(printerId, state, spoolInfo, ct);

        // Track successful status update time
        _lastStatusUpdateTimes[printerId] = DateTime.UtcNow;
    }

    /// <summary>
    /// Handles toolhead position and homed axes updates.
    /// Extracts X, Y, Z coordinates and homed_axes state, updates persistent state, and emits a toolhead update event.
    /// </summary>
    /// <param name="printerId">The ID of the printer.</param>
    /// <param name="state">The persistent printer state to update.</param>
    /// <param name="th">The toolhead JSON element from the status update.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task HandleToolheadUpdateAsync(Guid printerId, PrinterState state, JsonElement th, CancellationToken ct)
    {
        double? x = null, y = null, z = null;
        string? homedAxes = null;

        // Extract position
        if (th.TryGetProperty("position", out JsonElement pos) &&
            pos.ValueKind == JsonValueKind.Array && pos.GetArrayLength() >= 3)
        {
            try
            {
                x = pos[0].GetDouble();
            }
            catch
            {
            }

            try
            {
                y = pos[1].GetDouble();
            }
            catch
            {
            }

            try
            {
                z = pos[2].GetDouble();
            }
            catch
            {
            }
        }

        // Extract homed_axes
        if (th.TryGetProperty("homed_axes", out JsonElement ha) && ha.ValueKind == JsonValueKind.String)
        {
            // Klipper reports homed_axes as a string (e.g., "xyz") or an empty string when not homed.
            // Treat empty string as a *known* state so the UI can show "not homed" rather than "unknown".
            homedAxes = ha.GetString() ?? string.Empty;
        }

        // Update persistent state
        if (x.HasValue)
        {
            state.X = x;
        }

        if (y.HasValue)
        {
            state.Y = y;
        }

        if (z.HasValue)
        {
            state.Z = z;
        }

        // Persist homed axes state whenever the field is present, even when it's empty.
        // This allows downstream consumers to distinguish "known but not homed" ("") from "unknown" (null).
        if (homedAxes is not null)
        {
            state.HomedAxes = homedAxes;
        }

        // Emit separate toolhead event
        try
        {
            PrinterToolheadUpdate update = new PrinterToolheadUpdate(printerId, x, y, z, homedAxes);
            _logger.LogDebug("Emitting toolhead update for printer {PrinterId}: X={X}, Y={Y}, Z={Z}, HomedAxes={HomedAxes}", printerId, x, y, z, homedAxes);
            await hub!.Clients.All.SendAsync("toolheadupdate", update, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to emit toolhead update for printer {PrinterId}", printerId);
        }
    }

    /// <summary>
    /// Handles extruder temperature and target updates.
    /// Extracts temperature and target values, updates persistent state, and emits an extruder update event.
    /// </summary>
    /// <param name="printerId">The ID of the printer.</param>
    /// <param name="state">The persistent printer state to update.</param>
    /// <param name="ex">The extruder JSON element from the status update.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task HandleExtruderUpdateAsync(Guid printerId, PrinterState state, JsonElement ex, CancellationToken ct)
    {
        double? temperature = null, target = null;

        if (ex.TryGetProperty("temperature", out JsonElement t) && t.ValueKind is JsonValueKind.Number)
        {
            try
            {
                temperature = t.GetDouble();
            }
            catch
            {
            }
        }

        if (ex.TryGetProperty("target", out JsonElement tt) && tt.ValueKind is JsonValueKind.Number)
        {
            try
            {
                target = tt.GetDouble();
            }
            catch
            {
            }
        }

        // Update persistent state
        if (temperature.HasValue)
        {
            state.HotendTemp = temperature;
        }

        if (target.HasValue)
        {
            state.HotendTarget = target;
        }

        // Emit separate extruder event
        try
        {
            PrinterExtruderUpdate update = new PrinterExtruderUpdate(printerId, temperature, target);
            _logger.LogDebug("Emitting extruder update for printer {PrinterId}: Temp={Temperature}, Target={Target}", printerId, temperature, target);
            await hub!.Clients.All.SendAsync("extruderupdate", update, ct);
        }
        catch (Exception extruderEx)
        {
            _logger.LogError(extruderEx, "Failed to emit extruder update for printer {PrinterId}", printerId);
        }
    }

    /// <summary>
    /// Handles heated bed temperature and target updates.
    /// Extracts temperature and target values, updates persistent state, and emits a heater bed update event.
    /// </summary>
    /// <param name="printerId">The ID of the printer.</param>
    /// <param name="state">The persistent printer state to update.</param>
    /// <param name="hb">The heater_bed JSON element from the status update.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task HandleHeaterBedUpdateAsync(Guid printerId, PrinterState state, JsonElement hb, CancellationToken ct)
    {
        double? temperature = null, target = null;

        if (hb.TryGetProperty("temperature", out JsonElement t) && t.ValueKind is JsonValueKind.Number)
        {
            try
            {
                temperature = t.GetDouble();
            }
            catch
            {
            }
        }

        if (hb.TryGetProperty("target", out JsonElement tt) && tt.ValueKind is JsonValueKind.Number)
        {
            try
            {
                target = tt.GetDouble();
            }
            catch
            {
            }
        }

        // Update persistent state
        if (temperature.HasValue)
        {
            state.BedTemp = temperature;
        }

        if (target.HasValue)
        {
            state.BedTarget = target;
        }

        // Emit separate heater bed event
        try
        {
            PrinterHeaterBedUpdate update = new PrinterHeaterBedUpdate(printerId, temperature, target);
            _logger.LogDebug("Emitting heater bed update for printer {PrinterId}: Temp={Temperature}, Target={Target}", printerId, temperature, target);
            await hub!.Clients.All.SendAsync("heaterbedupdate", update, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to emit heater bed update for printer {PrinterId}", printerId);
        }
    }

    /// <summary>
    /// Handles MMU (Happy Hare) status updates from Moonraker.
    /// Parses gate status, materials, colors, active tool/gate, and action state.
    /// This is a synchronous handler since it only updates in-memory state.
    /// </summary>
    /// <param name="printerId">The ID of the printer.</param>
    /// <param name="state">The persistent printer state to update.</param>
    /// <param name="mmu">The mmu JSON element from the status update.</param>
    private void HandleMmuUpdate(Guid printerId, PrinterState state, JsonElement mmu)
    {
        // Once we receive any mmu object, mark as detected
        state.MmuDetected = true;
        state.MmuType = MmuProtocol.HappyHare;
        state.MmuDirty = true;

        if (mmu.TryGetProperty("enabled", out JsonElement enabled) && enabled.ValueKind == JsonValueKind.True)
        {
            state.MmuEnabled = true;
        }
        else if (mmu.TryGetProperty("enabled", out JsonElement disabled) && disabled.ValueKind == JsonValueKind.False)
        {
            state.MmuEnabled = false;
        }

        if (mmu.TryGetProperty("is_homed", out JsonElement homed))
        {
            state.MmuIsHomed = homed.ValueKind == JsonValueKind.True;
        }

        if (mmu.TryGetProperty("tool", out JsonElement tool) && tool.ValueKind == JsonValueKind.Number)
        {
            state.MmuActiveTool = tool.GetInt32();
        }

        if (mmu.TryGetProperty("gate", out JsonElement gate) && gate.ValueKind == JsonValueKind.Number)
        {
            state.MmuActiveGate = gate.GetInt32();
        }

        if (mmu.TryGetProperty("filament", out JsonElement filament) && filament.ValueKind == JsonValueKind.String)
        {
            state.MmuFilamentState = filament.GetString();
        }

        if (mmu.TryGetProperty("action", out JsonElement action) && action.ValueKind == JsonValueKind.String)
        {
            state.MmuAction = action.GetString();
        }

        if (mmu.TryGetProperty("num_gates", out JsonElement numGates) && numGates.ValueKind == JsonValueKind.Number)
        {
            state.MmuNumGates = numGates.GetInt32();
        }

        if (mmu.TryGetProperty("has_bypass", out JsonElement hasBypass))
        {
            state.MmuHasBypass = hasBypass.ValueKind == JsonValueKind.True;
        }

        if (mmu.TryGetProperty("endless_spool", out JsonElement endlessSpool) && endlessSpool.ValueKind == JsonValueKind.Number)
        {
            state.MmuEndlessSpool = endlessSpool.GetInt32() > 0;
        }

        if (mmu.TryGetProperty("clog_detection", out JsonElement clogDetection) && clogDetection.ValueKind == JsonValueKind.Number)
        {
            state.MmuClogDetection = clogDetection.GetInt32() > 0;
        }

        // Per-gate arrays
        if (mmu.TryGetProperty("gate_status", out JsonElement gateStatus) && gateStatus.ValueKind == JsonValueKind.Array)
        {
            state.MmuGateStatus = ParseIntArray(gateStatus);
        }

        if (mmu.TryGetProperty("gate_material", out JsonElement gateMaterial) && gateMaterial.ValueKind == JsonValueKind.Array)
        {
            state.MmuGateMaterial = ParseStringArray(gateMaterial);
        }

        if (mmu.TryGetProperty("gate_color", out JsonElement gateColor) && gateColor.ValueKind == JsonValueKind.Array)
        {
            state.MmuGateColor = ParseStringArray(gateColor);
        }

        if (mmu.TryGetProperty("gate_filament_name", out JsonElement gateFilamentName) && gateFilamentName.ValueKind == JsonValueKind.Array)
        {
            state.MmuGateFilamentName = ParseStringArray(gateFilamentName);
        }

        if (mmu.TryGetProperty("gate_spool_id", out JsonElement gateSpoolId) && gateSpoolId.ValueKind == JsonValueKind.Array)
        {
            state.MmuGateSpoolId = ParseIntArray(gateSpoolId);
        }

        _logger.LogDebug(
            "MMU update for printer {PrinterId}: Tool={Tool}, Gate={Gate}, Action={Action}, NumGates={NumGates}",
            printerId,
            state.MmuActiveTool,
            state.MmuActiveGate,
            state.MmuAction,
            state.MmuNumGates);
    }

    private static int[] ParseIntArray(JsonElement arr)
    {
        int[] result = new int[arr.GetArrayLength()];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = arr[i].ValueKind == JsonValueKind.Number ? arr[i].GetInt32() : 0;
        }

        return result;
    }

    private static string[] ParseStringArray(JsonElement arr)
    {
        string[] result = new string[arr.GetArrayLength()];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = arr[i].ValueKind == JsonValueKind.String ? arr[i].GetString() ?? string.Empty : string.Empty;
        }

        return result;
    }

    // ── Qidibox filament box handling ──

    /// <summary>
    /// Detects and handles Qidibox filament box updates from Moonraker.
    /// Qidibox uses "box_stepper slotN" objects for filament presence and
    /// "save_variables" for filament type/color assignments.
    /// The filament/color name dictionaries are fetched once from officiall_filas_list.cfg.
    /// </summary>
    private async Task HandleQidiboxUpdatesAsync(
        Guid printerId,
        PrinterState state,
        JsonElement statusObj,
        string serverUrl,
        CancellationToken ct)
    {
        // Skip detection scan if Qidibox is already detected
        bool hasBoxStepper = false;
        bool hasBoxCount = false;
        bool hasSaveVariables = false;
        JsonElement variables = default;

        if (!state.QidiboxDetected)
        {
            // Detect Qidibox by presence of "box_stepper slot0" in the status
            foreach (JsonProperty prop in statusObj.EnumerateObject())
            {
                if (prop.Name.StartsWith("box_stepper slot", StringComparison.Ordinal))
                {
                    hasBoxStepper = true;
                    break;
                }
            }
        }

        // Check save_variables (needed for both detection and ongoing updates)
        hasSaveVariables = statusObj.TryGetProperty("save_variables", out JsonElement saveVarsElem);
        if (hasSaveVariables &&
            saveVarsElem.TryGetProperty("variables", out variables) &&
            variables.TryGetProperty("box_count", out _))
        {
            hasBoxCount = true;
        }

        if (!hasBoxStepper && !hasBoxCount && !state.QidiboxDetected)
        {
            return;
        }

        // Mark Qidibox as detected (and set MMU type)
        if (!state.QidiboxDetected && (hasBoxStepper || hasBoxCount))
        {
            state.QidiboxDetected = true;
            state.MmuDetected = true;
            state.MmuEnabled = true;
            state.MmuType = MmuProtocol.Qidibox;
            state.MmuDirty = true;
            _logger.LogInformation(
                "Qidibox filament box detected for printer {PrinterId}",
                printerId);
        }

        // Fetch the filament/color dictionary once
        if (state.QidiboxDetected && !state.QidiboxDictFetched)
        {
            await FetchQidiboxDictionaryAsync(state, serverUrl, ct);
        }

        // Parse box_count from save_variables
        if (hasBoxCount && variables.ValueKind == JsonValueKind.Object)
        {
            if (variables.TryGetProperty("box_count", out JsonElement boxCountElem) &&
                boxCountElem.ValueKind == JsonValueKind.Number)
            {
                int boxCount = boxCountElem.GetInt32();
                if (boxCount > 0)
                {
                    state.QidiboxBoxCount = boxCount;
                    state.MmuNumGates = boxCount * 4;
                }
            }

            // Parse active slot from last_load_slot (e.g., "slot0" → 0, "slot-1" → -1)
            if (variables.TryGetProperty("last_load_slot", out JsonElement lastSlotElem) &&
                lastSlotElem.ValueKind == JsonValueKind.String)
            {
                string? lastSlot = lastSlotElem.GetString();
                if (lastSlot is not null && lastSlot.StartsWith("slot", StringComparison.Ordinal))
                {
                    if (int.TryParse(lastSlot.AsSpan(4), out int slotIndex))
                    {
                        state.MmuActiveTool = slotIndex;
                        state.MmuActiveGate = slotIndex >= 0 ? slotIndex : -1;
                    }
                }
            }

            // Build per-slot arrays from save_variables
            int numSlots = state.MmuNumGates > 0 ? state.MmuNumGates : 4;
            EnsureSlotArrays(state, numSlots);

            for (int i = 0; i < numSlots; i++)
            {
                // Filament type
                if (variables.TryGetProperty($"filament_slot{i}", out JsonElement filTypeElem) &&
                    filTypeElem.ValueKind == JsonValueKind.Number)
                {
                    int filType = filTypeElem.GetInt32();
                    if (state.QidiboxFilamentDict.TryGetValue(filType, out string? filName))
                    {
                        state.MmuGateMaterial![i] = filName;
                        state.MmuGateFilamentName![i] = filName;
                    }
                    else
                    {
                        state.MmuGateMaterial![i] = $"Type {filType}";
                        state.MmuGateFilamentName![i] = $"Type {filType}";
                    }
                }

                // Color
                if (variables.TryGetProperty($"color_slot{i}", out JsonElement colorElem) &&
                    colorElem.ValueKind == JsonValueKind.Number)
                {
                    int colorIdx = colorElem.GetInt32();
                    if (state.QidiboxColorDict.TryGetValue(colorIdx, out string? colorHex))
                    {
                        state.MmuGateColor![i] = colorHex;
                    }
                    else
                    {
                        state.MmuGateColor![i] = "#808080";
                    }
                }
            }
        }

        // Parse individual box_stepper slotN runout_button values
        foreach (JsonProperty prop in statusObj.EnumerateObject())
        {
            if (!prop.Name.StartsWith("box_stepper slot", StringComparison.Ordinal))
            {
                continue;
            }

            // Extract slot index from "box_stepper slotN"
            if (!int.TryParse(prop.Name.AsSpan("box_stepper slot".Length), out int slotIdx))
            {
                continue;
            }

            int numSlots = state.MmuNumGates > 0 ? state.MmuNumGates : 4;
            if (slotIdx < 0 || slotIdx >= numSlots)
            {
                continue;
            }

            EnsureSlotArrays(state, numSlots);

            // runout_button: 0 = filament present, 1 = no filament, null = slot inactive
            if (prop.Value.TryGetProperty("runout_button", out JsonElement runoutElem))
            {
                if (runoutElem.ValueKind == JsonValueKind.Number)
                {
                    int runout = runoutElem.GetInt32();
                    state.MmuGateStatus![slotIdx] = runout == 0 ? 1 : 0; // 1=Available, 0=Empty
                }
                else if (runoutElem.ValueKind == JsonValueKind.Null)
                {
                    state.MmuGateStatus![slotIdx] = -1; // Disabled (slot doesn't physically exist)
                }
            }
        }

        if (state.QidiboxDetected)
        {
            // Set filament state based on active tool — only mark dirty when values change
            string newFilamentState = state.MmuActiveTool >= 0 ? "Loaded" : "Unloaded";
            if (state.MmuFilamentState != newFilamentState || state.MmuAction != "Idle")
            {
                state.MmuFilamentState = newFilamentState;
                state.MmuAction = "Idle";
                state.MmuDirty = true;
            }

            _logger.LogDebug(
                "Qidibox update for printer {PrinterId}: BoxCount={BoxCount}, ActiveTool={Tool}, Slots={Slots}",
                printerId,
                state.QidiboxBoxCount,
                state.MmuActiveTool,
                state.MmuNumGates);
        }
    }

    /// <summary>
    /// Ensures the slot arrays on PrinterState are initialized to the given size.
    /// Preserves existing data when resizing via Array.Copy.
    /// </summary>
    private static void EnsureSlotArrays(PrinterState state, int numSlots)
    {
        if (state.MmuGateStatus == null || state.MmuGateStatus.Length != numSlots)
        {
            int[] newArr = new int[numSlots];
            Array.Fill(newArr, 2); // Default: Unknown
            if (state.MmuGateStatus is not null)
            {
                Array.Copy(state.MmuGateStatus, newArr, Math.Min(state.MmuGateStatus.Length, numSlots));
            }

            state.MmuGateStatus = newArr;
        }

        if (state.MmuGateMaterial == null || state.MmuGateMaterial.Length != numSlots)
        {
            string[] newArr = new string[numSlots];
            if (state.MmuGateMaterial is not null)
            {
                Array.Copy(state.MmuGateMaterial, newArr, Math.Min(state.MmuGateMaterial.Length, numSlots));
            }

            state.MmuGateMaterial = newArr;
        }

        if (state.MmuGateColor == null || state.MmuGateColor.Length != numSlots)
        {
            string[] newArr = new string[numSlots];
            if (state.MmuGateColor is not null)
            {
                Array.Copy(state.MmuGateColor, newArr, Math.Min(state.MmuGateColor.Length, numSlots));
            }

            state.MmuGateColor = newArr;
        }

        if (state.MmuGateFilamentName == null || state.MmuGateFilamentName.Length != numSlots)
        {
            string[] newArr = new string[numSlots];
            if (state.MmuGateFilamentName is not null)
            {
                Array.Copy(state.MmuGateFilamentName, newArr, Math.Min(state.MmuGateFilamentName.Length, numSlots));
            }

            state.MmuGateFilamentName = newArr;
        }

        if (state.MmuGateSpoolId == null || state.MmuGateSpoolId.Length != numSlots)
        {
            int[] newArr = new int[numSlots];
            Array.Fill(newArr, -1);
            if (state.MmuGateSpoolId is not null)
            {
                Array.Copy(state.MmuGateSpoolId, newArr, Math.Min(state.MmuGateSpoolId.Length, numSlots));
            }

            state.MmuGateSpoolId = newArr;
        }
    }

    // ── AFC (BoxTurtle / NightOwl / QuattroBox) handling ──

    /// <summary>
    /// Detects and handles AFC Klipper add-on updates from Moonraker.
    /// AFC (Armored Turtle Filament Changer) uses the "AFC" Moonraker object with lanes
    /// and per-lane "AFC_stepper &lt;name&gt;" objects for individual lane status.
    /// Supports BoxTurtle, NightOwl, and QuattroBox unit types.
    /// </summary>
    private void HandleAfcUpdates(
        Guid printerId,
        PrinterState state,
        JsonElement statusObj)
    {
        // Detect AFC by presence of "AFC" key in status
        bool hasAfcObject = statusObj.TryGetProperty("AFC", out JsonElement afcElem);

        // Also detect via AFC_stepper objects (per-lane status)
        // Skip scanning if we already know this printer has no AFC
        bool hasAfcSteppers = false;
        if (!state.AfcDetected)
        {
            foreach (JsonProperty prop in statusObj.EnumerateObject())
            {
                if (prop.Name.StartsWith("AFC_stepper ", StringComparison.Ordinal))
                {
                    hasAfcSteppers = true;
                    break;
                }
            }
        }

        if (!hasAfcObject && !hasAfcSteppers && !state.AfcDetected)
        {
            return;
        }

        // Mark AFC as detected (and set MMU type)
        if (!state.AfcDetected && (hasAfcObject || hasAfcSteppers))
        {
            state.AfcDetected = true;
            state.MmuDetected = true;
            state.MmuEnabled = true;
            state.MmuType = MmuProtocol.Afc;
            state.MmuDirty = true;
            _logger.LogInformation(
                "AFC filament changer detected for printer {PrinterId}",
                printerId);
        }

        // Parse top-level AFC object if present
        if (hasAfcObject && afcElem.ValueKind == JsonValueKind.Object)
        {
            // current_state: Idle, Loading, Unloading, Error, Ejecting, Moving, Restoring, Initialized
            if (afcElem.TryGetProperty("current_state", out JsonElement currentStateElem) &&
                currentStateElem.ValueKind == JsonValueKind.String)
            {
                state.AfcCurrentState = currentStateElem.GetString();
                state.MmuAction = state.AfcCurrentState;
            }

            // current_load: name of currently loaded lane, or null/empty
            if (afcElem.TryGetProperty("current_load", out JsonElement currentLoadElem))
            {
                if (currentLoadElem.ValueKind == JsonValueKind.String)
                {
                    string? loadedLane = currentLoadElem.GetString();
                    state.AfcCurrentLoad = string.IsNullOrEmpty(loadedLane) ? null : loadedLane;
                }
                else if (currentLoadElem.ValueKind == JsonValueKind.Null)
                {
                    state.AfcCurrentLoad = null;
                }
            }

            // error_state: boolean
            if (afcElem.TryGetProperty("error_state", out JsonElement errorStateElem))
            {
                state.AfcErrorState = errorStateElem.ValueKind == JsonValueKind.True;
            }

            // bypass_state: boolean
            if (afcElem.TryGetProperty("bypass_state", out JsonElement bypassStateElem))
            {
                state.AfcBypassState = bypassStateElem.ValueKind == JsonValueKind.True;
                state.MmuHasBypass = true; // AFC supports bypass
            }

            // lanes: array of lane name strings — establishes lane ordering
            if (afcElem.TryGetProperty("lanes", out JsonElement lanesElem) &&
                lanesElem.ValueKind == JsonValueKind.Array)
            {
                List<string> laneNames = [];
                foreach (JsonElement lane in lanesElem.EnumerateArray())
                {
                    if (lane.ValueKind == JsonValueKind.String)
                    {
                        string? name = lane.GetString();
                        if (!string.IsNullOrEmpty(name))
                        {
                            laneNames.Add(name);
                        }
                    }
                }

                if (laneNames.Count > 0)
                {
                    state.AfcLaneNames = laneNames;
                    state.MmuNumGates = laneNames.Count;
                    EnsureSlotArrays(state, laneNames.Count);
                }
            }
        }

        // Parse individual AFC_stepper lane objects for per-lane status
        foreach (JsonProperty prop in statusObj.EnumerateObject())
        {
            if (!prop.Name.StartsWith("AFC_stepper ", StringComparison.Ordinal))
            {
                continue;
            }

            // Extract lane name from "AFC_stepper <name>"
            string laneName = prop.Name["AFC_stepper ".Length..];
            if (string.IsNullOrEmpty(laneName))
            {
                continue;
            }

            // Find the lane index — add to list if not yet known
            int laneIndex = state.AfcLaneNames.IndexOf(laneName);
            if (laneIndex < 0)
            {
                // Lane not in the lanes list yet — append it
                state.AfcLaneNames.Add(laneName);
                laneIndex = state.AfcLaneNames.Count - 1;
                state.MmuNumGates = state.AfcLaneNames.Count;
            }

            int numLanes = state.AfcLaneNames.Count;
            EnsureSlotArrays(state, numLanes);

            JsonElement laneObj = prop.Value;
            if (laneObj.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            // material: filament type (e.g., "PLA", "PETG")
            if (laneObj.TryGetProperty("material", out JsonElement materialElem) &&
                materialElem.ValueKind == JsonValueKind.String)
            {
                string? material = materialElem.GetString();
                state.MmuGateMaterial![laneIndex] = material ?? string.Empty;
                state.MmuGateFilamentName![laneIndex] = material ?? string.Empty;
            }

            // color: filament color hex string
            if (laneObj.TryGetProperty("color", out JsonElement colorElem) &&
                colorElem.ValueKind == JsonValueKind.String)
            {
                string? color = colorElem.GetString();
                state.MmuGateColor![laneIndex] = color ?? string.Empty;
            }

            // spool_id: Spoolman spool ID
            if (laneObj.TryGetProperty("spool_id", out JsonElement spoolIdElem))
            {
                if (spoolIdElem.ValueKind == JsonValueKind.Number)
                {
                    state.MmuGateSpoolId![laneIndex] = spoolIdElem.GetInt32();
                }
                else if (spoolIdElem.ValueKind == JsonValueKind.String &&
                         int.TryParse(spoolIdElem.GetString(), out int spoolId))
                {
                    state.MmuGateSpoolId![laneIndex] = spoolId;
                }
            }

            // Determine gate status from load state flags:
            // load_state — filament loaded to extruder
            // prep_state — filament prepped/present at hub
            // loaded_to_hub — filament loaded to hub
            bool filamentPresent =
                (laneObj.TryGetProperty("load_state", out JsonElement loadStateElem) &&
                 loadStateElem.ValueKind == JsonValueKind.True) ||
                (laneObj.TryGetProperty("prep_state", out JsonElement prepStateElem) &&
                 prepStateElem.ValueKind == JsonValueKind.True) ||
                (laneObj.TryGetProperty("loaded_to_hub", out JsonElement hubLoadElem) &&
                 hubLoadElem.ValueKind == JsonValueKind.True);

            // Gate status: 1=Available, 0=Empty
            state.MmuGateStatus![laneIndex] = filamentPresent ? 1 : 0;
        }

        // Update active tool/gate based on currently loaded lane
        if (state.AfcDetected)
        {
            if (!string.IsNullOrEmpty(state.AfcCurrentLoad))
            {
                int loadedIndex = state.AfcLaneNames.IndexOf(state.AfcCurrentLoad);
                if (loadedIndex >= 0)
                {
                    state.MmuActiveTool = loadedIndex;
                    state.MmuActiveGate = loadedIndex;
                }

                state.MmuFilamentState = "Loaded";
            }
            else
            {
                state.MmuActiveTool = -1;
                state.MmuActiveGate = 0;
                state.MmuFilamentState = "Unloaded";
            }

            // Map AFC states to action label
            state.MmuAction = state.AfcCurrentState ?? "Idle";

            // Is-homed: AFC doesn't have a homing concept; if Initialized or Idle, treat as homed
            state.MmuIsHomed = state.AfcCurrentState is "Idle" or "Initialized" or "Loading" or "Unloading";
            state.MmuDirty = true;

            _logger.LogDebug(
                "AFC update for printer {PrinterId}: State={State}, CurrentLoad={Load}, Lanes={Lanes}, Error={Error}",
                printerId,
                state.AfcCurrentState,
                state.AfcCurrentLoad,
                state.MmuNumGates,
                state.AfcErrorState);
        }
    }

    /// <summary>
    /// Fetches and parses the Qidibox filament/color dictionary from the printer's
    /// officiall_filas_list.cfg configuration file via Moonraker HTTP API.
    /// </summary>
    private async Task FetchQidiboxDictionaryAsync(PrinterState state, string serverUrl, CancellationToken ct)
    {
        // Backoff: skip if we've failed recently and retry window hasn't elapsed
        if (state.QidiboxDictFetchAttempts > 0 && DateTime.UtcNow < state.QidiboxDictRetryAfter)
        {
            return;
        }

        const int maxAttempts = 5;
        if (state.QidiboxDictFetchAttempts >= maxAttempts)
        {
            // Give up permanently after max retries
            state.QidiboxDictFetched = true;
            return;
        }

        try
        {
            string normalized = serverUrl.TrimEnd('/');
            string dictUrl = $"{normalized}/server/files/config/officiall_filas_list.cfg";

            using HttpClient httpClient = _httpClientFactory.CreateClient();
            httpClient.Timeout = TimeSpan.FromSeconds(10);
            string content = await httpClient.GetStringAsync(dictUrl, ct);

            // Parse [colordict] section: index = #hexcolor
            ParseIniSection(content, "colordict", state.QidiboxColorDict);

            // Parse [filaN] sections: sections like [fila1], [fila41] with "filament = PETG"
            ParseFilamentSections(content, state.QidiboxFilamentDict);

            state.QidiboxDictFetched = true;
            state.QidiboxDictFetchAttempts = 0;
            _logger.LogInformation(
                "Fetched Qidibox dictionary: {ColorCount} colors, {FilamentCount} filaments",
                state.QidiboxColorDict.Count,
                state.QidiboxFilamentDict.Count);
        }
        catch (Exception ex)
        {
            state.QidiboxDictFetchAttempts++;
            int delaySec = (int)Math.Pow(2, state.QidiboxDictFetchAttempts) * 5; // 10s, 20s, 40s, 80s, 160s
            state.QidiboxDictRetryAfter = DateTime.UtcNow.AddSeconds(delaySec);

            _logger.LogWarning(
                ex,
                "Failed to fetch Qidibox filament dictionary from {ServerUrl} (attempt {Attempt}/{Max}). Retrying in {Delay}s.",
                serverUrl,
                state.QidiboxDictFetchAttempts,
                maxAttempts,
                delaySec);
        }
    }

    /// <summary>
    /// Parses an INI-style [section] into a dictionary of int → string values.
    /// Used for [colordict] in officiall_filas_list.cfg.
    /// </summary>
    private static void ParseIniSection(string content, string sectionName, Dictionary<int, string> result)
    {
        result.Clear();
        string sectionHeader = $"[{sectionName}]";
        bool inSection = false;

        foreach (string rawLine in content.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.StartsWith('['))
            {
                inSection = string.Equals(line, sectionHeader, StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (string.IsNullOrEmpty(line) || line[0] is '#' or ';')
            {
                continue;
            }

            if (inSection)
            {
                int eqPos = line.IndexOf('=');
                if (eqPos > 0)
                {
                    string key = line[..eqPos].Trim();
                    string value = line[(eqPos + 1)..].Trim();
                    if (int.TryParse(key, out int index) && !string.IsNullOrEmpty(value))
                    {
                        result[index] = value;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Parses [filaN] sections from officiall_filas_list.cfg to build a filament type dictionary.
    /// Each [filaN] section contains "filament = MaterialName".
    /// </summary>
    private static void ParseFilamentSections(string content, Dictionary<int, string> result)
    {
        result.Clear();
        int currentFilaIndex = -1;

        foreach (string rawLine in content.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.StartsWith('['))
            {
                currentFilaIndex = -1;
                if (line.StartsWith("[fila", StringComparison.OrdinalIgnoreCase) && line.EndsWith(']'))
                {
                    string numStr = line[5..^1]; // Extract number between "[fila" and "]"
                    if (int.TryParse(numStr, out int idx))
                    {
                        currentFilaIndex = idx;
                    }
                }

                continue;
            }

            if (string.IsNullOrEmpty(line) || line[0] is '#' or ';')
            {
                continue;
            }

            if (currentFilaIndex >= 0)
            {
                int eqPos = line.IndexOf('=');
                if (eqPos > 0)
                {
                    string key = line[..eqPos].Trim();
                    string value = line[(eqPos + 1)..].Trim();
                    if (string.Equals(key, "filament", StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrEmpty(value))
                    {
                        result[currentFilaIndex] = value;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Handles print state, progress, and job name updates from multiple sources.
    /// Prioritizes print_stats over webhooks state, updates persistent state, and emits a state update event.
    /// </summary>
    /// <param name="printerId">The ID of the printer.</param>
    /// <param name="state">The persistent printer state to update.</param>
    /// <param name="statusObj">The complete status JSON element containing display_status, print_stats, and webhooks.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task HandleStateUpdateAsync(Guid printerId, PrinterState state, JsonElement statusObj, CancellationToken ct)
    {
        string? stateValue = null;
        double? progress = null;
        string? jobName = null;

        // Display status (progress)
        if (statusObj.TryGetProperty("display_status", out JsonElement ds) &&
            ds.TryGetProperty("progress", out JsonElement prog))
        {
            try
            {
                double pv = prog.GetDouble();
                progress = pv > 1 ? pv : pv * 100.0;
            }
            catch
            {
            }
        }

        // Print stats (state, filename)
        string? printStatsState = null;
        if (statusObj.TryGetProperty("print_stats", out JsonElement ps))
        {
            if (ps.TryGetProperty("state", out JsonElement st) && st.ValueKind == JsonValueKind.String)
            {
                printStatsState = st.GetString();
            }

            if (ps.TryGetProperty("filename", out JsonElement fn) && fn.ValueKind == JsonValueKind.String)
            {
                jobName = fn.GetString();
            }
        }

        // Webhooks state (Klipper system state)
        string? webhooksState = null;
        if (statusObj.TryGetProperty("webhooks", out JsonElement wh) &&
            wh.TryGetProperty("state", out JsonElement ws) && ws.ValueKind == JsonValueKind.String)
        {
            webhooksState = ws.GetString();
        }

        // Determine final state: print_stats takes precedence
        if (!string.IsNullOrEmpty(printStatsState))
        {
            stateValue = printStatsState;
        }
        else if (!string.IsNullOrEmpty(webhooksState))
        {
            stateValue = webhooksState;
        }

        // Detect state transitions for job completion synchronization
        string? previousState = state.PreviousState;
        bool stateChanged = stateValue != null && stateValue != previousState;

        // Update persistent state (including PreviousState tracking)
        if (stateValue != null)
        {
            state.PreviousState = state.State;
            state.State = stateValue;
        }

        if (progress.HasValue)
        {
            state.Progress = progress;
        }

        if (jobName != null)
        {
            state.JobName = jobName;
        }

        // Check for print completion/failure transitions and sync job status
        if (stateChanged && previousState != null)
        {
            await CheckAndSyncJobCompletionAsync(printerId, previousState, stateValue!, ct);
        }

        // Emit state update event if any state/progress/jobName changed
        if (stateValue != null || progress.HasValue || jobName != null)
        {
            try
            {
                PrinterStateUpdate update = new PrinterStateUpdate(printerId, stateValue, progress, jobName, PrinterStatusDto.ExtractFileName(jobName));
                _logger.LogDebug("Emitting state update for printer {PrinterId}: State={StateValue}, Progress={Progress}, JobName={JobName}", printerId, stateValue, progress, jobName);
                await hub!.Clients.All.SendAsync("stateupdate", update, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to emit state update for printer {PrinterId}", printerId);
            }
        }
    }

    /// <summary>
    /// Emits a consolidated printer status update containing all accumulated state.
    /// Includes position, temperatures, state, progress, homed axes, and spool information.
    /// Broadcasts via SignalR "printerupdated" event with IsOnline based on actual Klippy ready state.
    /// </summary>
    /// <param name="printerId">The ID of the printer.</param>
    /// <param name="state">The persistent printer state containing all accumulated values.</param>
    /// <param name="spoolInfo">Optional spool information from Spoolman service.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task EmitConsolidatedStatusAsync(Guid printerId, PrinterState state, PrinterSpoolInfoDto? spoolInfo, CancellationToken ct)
    {
        try
        {
            // Determine online status based on Klippy ready state
            // Default to false if not yet tracked (prevents false positives)
            bool isOnline = _klippyReadyState.TryGetValue(printerId, out bool ready) && ready;

            // Build MMU status if detected
            MmuStatusDto? mmuStatus = state.BuildMmuStatus();

            // Send consolidated update for offline status and overall state sync
            PrinterStatusUpdate update = new PrinterStatusUpdate(
                printerId,
                isOnline,
                PrinterStateNormalizer.NormalizeState(state.State),
                state.Progress,
                state.JobName,
                ThumbnailUrl: state.ThumbnailUrl,
                CameraStreamUrl: state.CameraStreamUrl,
                X: state.X,
                Y: state.Y,
                Z: state.Z,
                HotendTemp: state.HotendTemp,
                BedTemp: state.BedTemp,
                HotendTarget: state.HotendTarget,
                BedTarget: state.BedTarget,
                HomedAxes: state.HomedAxes,
                SpoolInfo: spoolInfo,
                MmuStatus: mmuStatus,
                FileName: PrinterStatusDto.ExtractFileName(state.JobName));

            _logger.LogDebug("Emitting consolidated status for printer {PrinterId}: IsOnline={IsOnline}, X={StateX}, Y={StateY}, Z={StateZ}, HotendTemp={StateHotendTemp}, HotendTarget={StateHotendTarget}, BedTemp={StateBedTemp}, BedTarget={StateBedTarget}, HomedAxes={StateHomedAxes}", printerId, isOnline, state.X, state.Y, state.Z, state.HotendTemp, state.HotendTarget, state.BedTemp, state.BedTarget, state.HomedAxes);

            // Update cache before broadcasting to clients
            PrinterStatusDto cacheUpdate = new PrinterStatusDto(
                Id: printerId,
                IsOnline: isOnline,
                State: PrinterStateNormalizer.NormalizeState(state.State),
                Progress: state.Progress,
                JobName: state.JobName,
                ThumbnailUrl: state.ThumbnailUrl,
                CameraStreamUrl: state.CameraStreamUrl,
                CameraSnapshotUrl: null,
                X: state.X,
                Y: state.Y,
                Z: state.Z,
                HotendTemp: state.HotendTemp,
                BedTemp: state.BedTemp,
                HotendTarget: state.HotendTarget,
                BedTarget: state.BedTarget,
                SpoolInfo: spoolInfo,
                MmuStatus: mmuStatus);
            _statusCacheWriter.UpdateStatus(cacheUpdate);

            _logger.LogInformation("[MoonrakerSubscriptionService] Broadcasting printerupdated for {PrinterId} via SignalR", printerId);
            _logger.LogDebug("[MoonrakerSubscriptionService] Hub is null: {Value0}", hub == null);
            await hub!.Clients.All.SendAsync("printerupdated", update, ct);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Consolidated status for printer {PrinterId} was cancelled", printerId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to emit consolidated status for printer {PrinterId}", printerId);
        }
    }

    /// <summary>
    /// Sends an offline status update for a printer.
    /// Broadcasts via SignalR "printerupdated" event with IsOnline=false.
    /// </summary>
    /// <param name="printerId">The ID of the printer.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task SendOfflineStatusAsync(Guid printerId, CancellationToken ct)
    {
        try
        {
            if (hub == null)
            {
                _logger.LogError("Hub context is null, cannot send offline status for printer {PrinterId}", printerId);
                return;
            }

            PrinterStatusUpdate offlineUpdate = new(
                printerId,
                false, // IsOnline
                PrinterStateNormalizer.NormalizeState("Offline"),
                null, null, null, null,
                null, null, null,
                null, null, null, null,
                HomedAxes: null,
                SpoolInfo: null);

            // Update cache before broadcasting to clients
            PrinterStatusDto offlineCacheUpdate = new PrinterStatusDto(
                Id: printerId,
                IsOnline: false,
                State: PrinterStateNormalizer.NormalizeState("Offline"),
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
            _statusCacheWriter.UpdateStatus(offlineCacheUpdate);

            _logger.LogInformation("[MoonrakerSubscriptionService] Broadcasting printerupdated (offline) for {PrinterId} via SignalR", printerId);
            await hub.Clients.All.SendAsync("printerupdated", offlineUpdate, ct);
            _logger.LogDebug("Sent offline status for printer {PrinterId}", printerId);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Offline status update for printer {PrinterId} was cancelled", printerId);
        }
        catch (Exception sendEx)
        {
            _logger.LogError(sendEx, "Failed to send offline status for printer {PrinterId}: {SendExMessage}", printerId, sendEx.Message);
        }
    }

    /// <summary>
    /// Sends a shutdown status update for a printer when Klippy shuts down.
    /// Broadcasts via SignalR "printerupdated" event with State="Shutdown" and IsOnline=false.
    /// </summary>
    /// <param name="printerId">The ID of the printer.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task SendShutdownStatusAsync(Guid printerId, CancellationToken ct)
    {
        try
        {
            if (hub == null)
            {
                _logger.LogError("Hub context is null, cannot send shutdown status for printer {PrinterId}", printerId);
                return;
            }

            PrinterStatusUpdate shutdownUpdate = new(
                printerId,
                false, // IsOnline
                PrinterStateNormalizer.NormalizeState("Shutdown"),
                null, null, null, null,
                null, null, null,
                null, null, null, null,
                HomedAxes: null,
                SpoolInfo: null);

            _logger.LogInformation("[MoonrakerSubscriptionService] Broadcasting printerupdated (shutdown) for {PrinterId} via SignalR", printerId);
            await hub.Clients.All.SendAsync("printerupdated", shutdownUpdate, ct);
            _logger.LogDebug("Sent shutdown status for printer {PrinterId}", printerId);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Shutdown status update for printer {PrinterId} was cancelled", printerId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send shutdown status for printer {PrinterId}: {Message}", printerId, ex.Message);
        }
    }

    /// <summary>
    /// Checks for print completion/failure state transitions and synchronizes job status in database.
    /// Called when printer state changes from "printing" to "standby/complete/idle" (completion)
    /// or from "printing" to "error/cancelled" (failure).
    /// </summary>
    /// <param name="printerId">The ID of the printer.</param>
    /// <param name="previousState">The previous printer state.</param>
    /// <param name="newState">The new printer state.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task CheckAndSyncJobCompletionAsync(Guid printerId, string previousState, string newState, CancellationToken ct)
    {
        try
        {
            // Only act on transitions FROM "printing" state
            if (!PrintJobCompletionService.IsPrintingState(previousState))
            {
                return;
            }

            _logger.LogInformation("[MoonrakerSubscriptionService] Detected state transition for printer {PrinterId}: {PreviousState} -> {NewState}", printerId, previousState, newState);

            // Create a new scope to get the scoped service
            using IServiceScope scope = scopeFactory.CreateScope();
            IPrintJobCompletionService completionService = scope.ServiceProvider.GetRequiredService<IPrintJobCompletionService>();

            if (PrintJobCompletionService.IsCompletionState(newState))
            {
                // Print completed successfully
                bool marked = await completionService.MarkCurrentJobAsCompletedAsync(printerId, newState, ct);
                if (marked)
                {
                    _logger.LogInformation("[MoonrakerSubscriptionService] Print job marked as completed for printer {PrinterId}", printerId);
                }
            }
            else if (PrintJobCompletionService.IsFailureState(newState))
            {
                // Print failed
                bool marked = await completionService.MarkCurrentJobAsFailedAsync(printerId, $"Printer state changed to {newState}", ct);
                if (marked)
                {
                    _logger.LogWarning("[MoonrakerSubscriptionService] Print job marked as failed for printer {PrinterId} (state: {NewState})", printerId, newState);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[MoonrakerSubscriptionService] Failed to sync job completion for printer {PrinterId}: {Message}", printerId, ex.Message);
        }
    }

    /// <summary>
    /// Sets the polling mode for a printer based on Klippy state changes.
    /// Tracks whether WebSocket real-time updates or HTTP polling fallback should be used.
    /// </summary>
    /// <param name="printerId">The ID of the printer.</param>
    /// <param name="mode">The polling mode to set.</param>
    /// <param name="reason">The reason for the mode change (for logging).</param>
    private void SetPollingMode(Guid printerId, PollingMode mode, string reason)
    {
        try
        {
            _ = _pollingModes.AddOrUpdate(printerId, mode, (key, oldValue) => mode);

            _logger.LogInformation("Set polling mode for printer {PrinterId} to {Mode}: {Reason}", printerId, mode, reason);

            // Log state transition if mode changed
            if (_pollingModes.TryGetValue(printerId, out PollingMode previousMode) && previousMode != mode)
            {
                _logger.LogDebug("Polling mode transition for printer {PrinterId}: {PreviousMode} -> {Mode}", printerId, previousMode, mode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError("Failed to set polling mode for printer {PrinterId} to {Mode}: {Message}", printerId, mode, ex.Message);
        }
    }

    // Removed unused GetPollingMode (CA S1144)

    /// <summary>
    /// Queries and caches initial toolhead data from Moonraker, particularly homed_axes.
    /// Called during subscription startup since Moonraker only sends incremental updates thereafter.
    /// </summary>
    /// <param name="printerId">The ID of the printer.</param>
    /// <param name="serverUrl">The Moonraker server URL.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task QueryAndCacheToolheadDataAsync(Guid printerId, string serverUrl, CancellationToken ct)
    {
        try
        {
            string? toolheadData = await QueryHomedAxesAsync(serverUrl, ct);
            if (!string.IsNullOrEmpty(toolheadData))
            {
                PrinterState state = _printerStates.GetOrAdd(printerId, _ => new PrinterState());
                state.HomedAxes = toolheadData;
                _logger.LogInformation("Cached initial homed_axes for printer {PrinterId}: '{HomedAxes}'", printerId.ToString(), toolheadData);
            }
        }
        catch (Exception toolheadEx)
        {
            _logger.LogDebug(toolheadEx, "Failed to cache initial toolhead data for printer {PrinterId}", printerId.ToString());
        }
    }

    /// <summary>
    /// Queries homed axes state via HTTP from Moonraker.
    /// Used because Moonraker doesn't send homed_axes in WebSocket subscriptions.
    /// </summary>
    /// <param name="serverUrl">The Moonraker server URL.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A string representing the homed axes (e.g., "xyz"), or null if unavailable.</returns>
    private async Task<string?> QueryHomedAxesAsync(string serverUrl, CancellationToken ct)
    {
        try
        {
            // Build HTTP query URL
            string normalized = serverUrl.TrimEnd('/');
            if (!normalized.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                normalized = "http://" + normalized;
            }

            UriBuilder ub = new(normalized);
            if (ub.Port == -1)
            {
                ub.Port = 7125;
            }

            Uri queryUri = new(ub.Uri, "/printer/objects/query?toolhead=homed_axes");

            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            using HttpClient httpClient = _httpClientFactory.CreateClient();
            using HttpResponseMessage resp = await httpClient.GetAsync(queryUri, cts.Token);
            if (resp.IsSuccessStatusCode)
            {
                await using Stream stream = await resp.Content.ReadAsStreamAsync(cts.Token);
                using JsonDocument doc = await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token);
                JsonElement root = doc.RootElement;

                if (root.TryGetProperty("result", out JsonElement result) &&
                    result.TryGetProperty("status", out JsonElement statusNode) &&
                    statusNode.TryGetProperty("toolhead", out JsonElement th) &&
                    th.TryGetProperty("homed_axes", out JsonElement homedAxes))
                {
                    return homedAxes.GetString();
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to query homed axes for {ServerUrl}", serverUrl);
            return null;
        }
    }

    /// <summary>
    /// Fetches active spool information from the Spoolman service for a Moonraker printer.
    /// Gets the active spool ID from Moonraker, then retrieves detailed spool information.
    /// </summary>
    /// <param name="serverUrl">The Moonraker server URL.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Printer spool information, or null if no active spool or on error.</returns>
    private async Task<PrinterSpoolInfoDto?> GetSpoolInfoAsync(string serverUrl, CancellationToken ct)
    {
        try
        {
            // Create scope to get moonraker client
            await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
            IMoonrakerClient moonrakerClient = scope.ServiceProvider.GetRequiredService<IMoonrakerClient>();

            // Check if Spoolman is configured and connected on this printer
            SpoolmanStatus? spoolmanStatus = await moonrakerClient.GetSpoolmanStatusAsync(serverUrl, ct);
            if (spoolmanStatus == null || !spoolmanStatus.SpoolmanConnected)
            {
                return null; // Spoolman not configured or not connected
            }

            // Get the active spool ID from Moonraker
            int? activeSpoolId = await moonrakerClient.GetSpoolmanActiveSpoolAsync(serverUrl, ct);
            if (activeSpoolId == null)
            {
                return new PrinterSpoolInfoDto(HasActiveSpool: false);
            }

            // Fetch spool details from Spoolman via Moonraker proxy
            string? spoolDetailsJson = await moonrakerClient.GetSpoolmanSpoolByIdAsync(serverUrl, activeSpoolId.Value, ct);
            if (string.IsNullOrWhiteSpace(spoolDetailsJson))
            {
                return new PrinterSpoolInfoDto(HasActiveSpool: true, ActiveSpoolId: activeSpoolId);
            }

            try
            {
                using JsonDocument doc = JsonDocument.Parse(spoolDetailsJson);
                JsonElement root = doc.RootElement;

                double? remainingWeight = root.TryGetProperty("remaining_weight", out JsonElement weightEl) && weightEl.ValueKind == JsonValueKind.Number ? weightEl.GetDouble() : (double?)null;

                // initial_weight is at root level in Spoolman spool JSON; fallback to filament.weight
                double? initialWeight = root.TryGetProperty("initial_weight", out JsonElement iwEl) && iwEl.ValueKind == JsonValueKind.Number
                    ? iwEl.GetDouble()
                    : (root.TryGetProperty("filament", out JsonElement filEl2) && filEl2.ValueKind == JsonValueKind.Object
                        && filEl2.TryGetProperty("weight", out JsonElement fwEl) && fwEl.ValueKind == JsonValueKind.Number
                        ? fwEl.GetDouble() : (double?)null);

                string? material = null;
                string? colorHex = null;
                string? vendor = null;
                string? filamentName = null;
                if (root.TryGetProperty("filament", out JsonElement filamentEl) && filamentEl.ValueKind == JsonValueKind.Object)
                {
                    material = filamentEl.TryGetProperty("material", out JsonElement matEl) ? matEl.GetString() : null;
                    colorHex = filamentEl.TryGetProperty("color_hex", out JsonElement colorEl) ? colorEl.GetString() : null;
                    filamentName = filamentEl.TryGetProperty("name", out JsonElement fnEl) ? fnEl.GetString() : null;
                    if (filamentEl.TryGetProperty("vendor", out JsonElement vendorEl) && vendorEl.ValueKind == JsonValueKind.Object)
                    {
                        vendor = vendorEl.TryGetProperty("name", out JsonElement vnEl) ? vnEl.GetString() : null;
                    }
                }

                return new PrinterSpoolInfoDto(
                    HasActiveSpool: true,
                    ActiveSpoolId: activeSpoolId,
                    SpoolName: filamentName,
                    Material: material,
                    ColorHex: colorHex != null ? $"#{colorHex}" : null,
                    FilamentName: filamentName,
                    Vendor: vendor,
                    RemainingWeightG: remainingWeight,
                    InitialWeightG: initialWeight);
            }
            catch (Exception parseEx)
            {
                _logger.LogWarning(parseEx, "GetSpoolInfoAsync: Failed to parse spool details for {ServerUrl}", serverUrl);
                return new PrinterSpoolInfoDto(HasActiveSpool: true, ActiveSpoolId: activeSpoolId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetSpoolInfoAsync: Exception occurred during spool detection for {ServerUrl}", serverUrl);

            // If any operations fail, Spoolman status is unknown
            return null;
        }
    }

    /// <summary>
    /// Triggers HTTP polling fallback when WebSocket parse errors exceed threshold or connection is stale.
    /// Uses the existing MoonrakerClient to fetch comprehensive status via HTTP.
    /// </summary>
    /// <param name="printer">The printer to poll.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task TriggerHttpPollingFallbackAsync(Printer printer, CancellationToken ct)
    {
        try
        {
            DateTime lastPollTime = _lastHttpPollTimes.GetValueOrDefault(printer.Id, DateTime.MinValue);
            TimeSpan timeSinceLastPoll = DateTime.UtcNow - lastPollTime;

            // Only poll if enough time has passed since last poll
            if (timeSinceLastPoll < HttpPollInterval)
            {
                return;
            }

            _logger.LogDebug("Starting HTTP polling fallback for printer {PrinterName}", printer.Name);

            // Use existing MoonrakerClient to fetch status via HTTP
            await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
            IMoonrakerClient moonrakerClient = scope.ServiceProvider.GetRequiredService<IMoonrakerClient>();

            // Get comprehensive status using existing HTTP endpoint
            PrinterCompositeStatus compositeStatus = await moonrakerClient.GetCompositeStatusAsync(printer.BackendUrl, ct);

            if (compositeStatus != null && compositeStatus.IsOnline)
            {
                // Convert CompositeStatus to StatusUpdate format and send via existing logic
                _logger.LogDebug("HTTP polling fallback retrieved status for printer {PrinterName}: State={CompositeStatusState}, IsOnline={CompositeStatusIsOnline}", printer.Name, compositeStatus.State, compositeStatus.IsOnline);

                // Create a status update using the composite status data
                PrinterSpoolInfoDto? spoolInfo = await GetSpoolInfoAsync(printer.BackendUrl, ct);

                PrinterStatusUpdate statusUpdate = new(
                    printer.Id,
                    compositeStatus.IsOnline,
                    PrinterStateNormalizer.NormalizeState(compositeStatus.State),
                    compositeStatus.Progress,
                    compositeStatus.JobName,
                    compositeStatus.ThumbnailUrl,
                    compositeStatus.CameraStreamUrl,
                    compositeStatus.X,
                    compositeStatus.Y,
                    compositeStatus.Z,
                    compositeStatus.HotendTemp,
                    compositeStatus.BedTemp,
                    compositeStatus.HotendTarget,
                    compositeStatus.BedTarget,
                    null, // HomedAxes - Not available in CompositeStatus
                    spoolInfo,
                    FileName: PrinterStatusDto.ExtractFileName(compositeStatus.JobName));

                try
                {
                    if (hub == null)
                    {
                        _logger.LogError("Hub context is null in HTTP polling fallback for printer {PrinterName}", printer.Name);
                        return;
                    }

                    await hub.Clients.All.SendAsync("printerupdated", statusUpdate, ct);

                    // Update last poll time and reset parse error count since HTTP polling succeeded
                    _lastHttpPollTimes[printer.Id] = DateTime.UtcNow;
                    _ = _parseErrorCounts.TryRemove(printer.Id, out _);

                    _logger.LogDebug("HTTP polling fallback successful for printer {PrinterName}", printer.Name);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogDebug("HTTP polling fallback status update for printer {PrinterName} was cancelled", printer.Name);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "HTTP polling fallback failed to send status update for printer {PrinterName}: {ExceptionMessage}", printer.Name, ex.Message);
                }
            }
            else
            {
                _logger.LogWarning("HTTP polling fallback failed for printer {PrinterName} - no status returned or offline", printer.Name);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during HTTP polling fallback for printer {PrinterName}", printer.Name);
        }
    }

    #region Connection Health

    /// <inheritdoc/>
    public IReadOnlyDictionary<Guid, PrinterConnectionHealth> GetConnectionHealth()
    {
        // Update uptime percentages before returning
        foreach (var health in _connectionHealth.Values)
        {
            health.UpdateUptimePercent(TimeSpan.FromHours(1));

            // Sync connection mode from polling mode
            if (_pollingModes.TryGetValue(health.PrinterId, out var mode))
            {
                health.ConnectionMode = mode switch
                {
                    PollingMode.WebSocketRealTime => "WebSocket",
                    PollingMode.HttpPollingOnly => "HTTP Polling",
                    PollingMode.WebSocketWithFallback => "WebSocket + Fallback",
                    _ => "Unknown"
                };
            }

            // Sync metrics
            if (_connectionMetrics.TryGetValue(health.PrinterId, out var metrics))
            {
                health.ReconnectAttempts = metrics.ReconnectAttempts;
                health.TotalReconnects = metrics.TotalReconnects;
                health.ConsecutiveFailures = metrics.ConsecutiveFailures;
            }
        }

        return _connectionHealth;
    }

    private PrinterConnectionHealth GetOrCreateHealth(Guid printerId, string printerName)
    {
        return _connectionHealth.GetOrAdd(printerId, id => new PrinterConnectionHealth
        {
            PrinterId = id,
            PrinterName = printerName,
            Backend = PrinterBackend.Moonraker
        });
    }

    private void RecordHealthTransition(Guid printerId, string printerName, PrinterConnectionState newState, string? reason)
    {
        var health = GetOrCreateHealth(printerId, printerName);
        health.RecordTransition(newState, reason);
    }

    private void StartOfflineGraceTimer(Guid printerId, string printerName, CancellationToken ct)
    {
        CancelOfflineGraceTimer(printerId);

        var graceCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _offlineGraceTimers[printerId] = graceCts;

        _ = Task.Run(
            async () =>
        {
            try
            {
                await Task.Delay(OfflineGracePeriod, graceCts.Token);

                // Grace period expired — printer didn't recover, broadcast offline
                _logger.LogWarning("Offline grace period expired for printer {PrinterName}, confirming offline", printerName);
                RecordHealthTransition(printerId, printerName, PrinterConnectionState.Offline, "Grace period expired");
                await SendOfflineStatusAsync(printerId, ct);
            }
            catch (OperationCanceledException)
            {
                // Grace timer was cancelled — printer recovered, no action needed
                _logger.LogDebug("Offline grace timer cancelled for printer {PrinterName} (recovered)", printerName);
            }
            finally
            {
                _offlineGraceTimers.TryRemove(printerId, out _);
            }
        }, CancellationToken.None);
    }

    private void CancelOfflineGraceTimer(Guid printerId)
    {
        if (_offlineGraceTimers.TryRemove(printerId, out var existingCts))
        {
            try
            {
                existingCts.Cancel();
                existingCts.Dispose();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    #endregion
}
