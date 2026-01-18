using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Farm.Infrastructure;
using Farm.Infrastructure.Contracts.Printers.Moonraker;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Printers;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.SignalR;
using Farm.Infrastructure.Telemetry;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Farm.Backend.Plugin.Moonraker;

// Persistent state for a printer to avoid overwriting good values with nulls
internal sealed class PrinterState
{
    public double? X { get; set; }

    public double? Y { get; set; }

    public double? Z { get; set; }

    public double? HotendTemp { get; set; }

    public double? BedTemp { get; set; }

    public double? HotendTarget { get; set; }

    public double? BedTarget { get; set; }

    public string? State { get; set; }

    public double? Progress { get; set; }

    public string? JobName { get; set; }

    public string? HomedAxes { get; set; }

    public string? CameraStreamUrl { get; set; }

    public string? ThumbnailUrl { get; set; }
}

public sealed class MoonrakerSubscriptionService(
    IHubContext<PrinterHub> hub,
    IServiceScopeFactory scopeFactory,
    IUnifiedLoggingService logger,
    IHttpClientFactory httpClientFactory,
    IPrinterStatusCacheWriter statusCacheWriter) : IHostedService, IDisposable
{
    private readonly IUnifiedLoggingService _logger = logger;
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
        catch { /* ignore during dispose */ }
        _cts.Dispose();
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
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    private async Task EnumerateAndStartSubscriptionsAsync(CancellationToken ct)
    {
        // Using an async scope while awaiting EF Core ToListAsync is intentional here.
        // The scope lifetime matches the query and is disposed immediately after.
#pragma warning disable IDISP013 // Await in using
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<Farm.Infrastructure.Repositories.UnitOfWork.IUnitOfWork>();
        IPrintersRepository printersRepo = unitOfWork.Printers;

        // Only subscribe to ENABLED Moonraker-backed printers
        // Note: Only Moonraker supports real-time WebSocket subscriptions
        // PrusaLink and SDCP are polled via HTTP on-demand, not continuously
        List<Printer> allPrinters = await printersRepo.GetByBackendAsync(PrinterBackend.Moonraker, ct);
        List<Printer> enabledPrinters = allPrinters.Where(p => p.IsEnabled).ToList();

        foreach (Printer? p in enabledPrinters)
        {
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
                _logger.LogWarning($"Detected stale connection for printer {printerId}, last update was {lastUpdate:O}. Triggering HTTP polling fallback.");

                // Find the printer to trigger fallback
                await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
                var unitOfWork = scope.ServiceProvider.GetRequiredService<Farm.Infrastructure.Repositories.UnitOfWork.IUnitOfWork>();
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
                    _logger.LogInformation($"Printer {id} no longer uses Moonraker backend, stopping subscription");
                    return;
                }

                // Apply exponential backoff if this is a retry
                if (metrics.ReconnectAttempts > 0)
                {
                    TimeSpan backoffDelay = metrics.GetNextBackoffDelay();
                    _logger.LogInformation($"Backing off for {backoffDelay.TotalSeconds}s before reconnecting to printer {printer.Name} (attempt {metrics.ReconnectAttempts + 1}/{MaxReconnectAttempts})");

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

                _logger.LogDebug($"Connecting to Moonraker WebSocket at {uri} for printer {printer.Name}");
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
                    _logger.LogWarning(ex, "Failed to query initial toolhead data for printer {PrinterName}", printer.Name);
                    // Don't fail startup over this - we'll query on-demand later
                }

                // Step 3: Start heartbeat mechanism
                heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                heartbeatTask = StartHeartbeatAsync(ws, printer, heartbeatCts.Token);

                // Connection successful - reset metrics
                metrics.Reset();

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
                    _logger.LogError(ex, $"Fatal error in subscription loop for printer {printer.Name}, stopping reconnection attempts");
                    break;
                }
                else if (MoonrakerErrors.IsTransientError(ex))
                {
                    _logger.LogWarning(ex, $"Transient error for printer {printer.Name}, will retry (attempt {metrics.ReconnectAttempts}/{MaxReconnectAttempts})");
                }
                else
                {
                    _logger.LogError(ex, $"Unexpected error for printer {printer.Name} (attempt {metrics.ReconnectAttempts}/{MaxReconnectAttempts})");
                }

                // Send offline status on connection failure
                await SendOfflineStatusAsync(id, ct);
            }
            finally
            {
                // Cleanup resources
                if (heartbeatCts != null && !heartbeatCts.IsCancellationRequested)
                {
                    await heartbeatCts.CancelAsync();
                }

                try
                { await (heartbeatTask ?? Task.CompletedTask); }
                catch { }
                heartbeatCts?.Dispose();

                try
                { ws?.Dispose(); }
                catch { }
            }
        }

        if (metrics.ReconnectAttempts >= MaxReconnectAttempts)
        {
            _logger.LogError($"Exhausted all reconnection attempts ({MaxReconnectAttempts}) for printer {printer.Name}, giving up");
            await SendOfflineStatusAsync(id, ct);
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
            var unitOfWork = scope.ServiceProvider.GetRequiredService<Farm.Infrastructure.Repositories.UnitOfWork.IUnitOfWork>();
            Printer? current = await unitOfWork.Printers.FindByIdAsync(printerId, ct);

            if (current is null)
            {
                _logger.LogInformation($"Printer {printerId} was removed from database");
                return false;
            }

            if (current.Backend != (int)Farm.Infrastructure.PrinterBackend.Moonraker)
            {
                _logger.LogInformation($"Printer {printerId} backend changed from Moonraker (Backend={current.Backend})");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"Failed to validate printer backend for {printerId}: {ex.Message}");
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
            _logger.LogWarning(ex, "Failed to send connection identification");
            // Don't throw - identification is optional
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
        var receiveTimeout = TimeSpan.FromSeconds(5);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(receiveTimeout);

        try
        {
            StringBuilder sb = new();
            while (ws.State == WebSocketState.Open && !cts.Token.IsCancellationRequested)
            {
                var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
                if (result.MessageType == WebSocketMessageType.Text)
                {
                    sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                    if (result.EndOfMessage)
                    {
                        string message = sb.ToString();
                        sb.Clear();

                        // Check if this is the objects.list response (ID 102)
                        using var doc = JsonDocument.Parse(message);
                        if (doc.RootElement.TryGetProperty("id", out var idElem) && idElem.GetInt32() == 102)
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
        var subscriptionObjects = new Dictionary<string, string[]?>();

        if (!string.IsNullOrEmpty(objectsListJson))
        {
            using var doc = JsonDocument.Parse(objectsListJson);
            if (doc.RootElement.TryGetProperty("result", out var resultElem) &&
                resultElem.TryGetProperty("objects", out var objectsElem) &&
                objectsElem.ValueKind == JsonValueKind.Array)
            {
                var blocklist = new[] { "menu" }; // Objects to skip (same as Mainsail)
                List<string> subscribedObjects = [];
                List<string> skippedObjects = [];

                foreach (var objElem in objectsElem.EnumerateArray())
                {
                    if (objElem.ValueKind == JsonValueKind.String)
                    {
                        string objectName = objElem.GetString() ?? "";
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

                _logger.LogInformation("Discovered {ObjectCount} objects from Moonraker, subscribing to {SubscriptionCount}",
                    objectsElem.GetArrayLength().ToString(), subscribedObjects.Count.ToString());
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

        _logger.LogInformation("Sent subscription request for {ObjectCount} objects to Moonraker: {ObjectList}",
            subscriptionObjects.Count.ToString(), string.Join(", ", subscriptionObjects.Keys));
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
        return Task.Run(async () =>
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
                        _logger.LogDebug($"Sent heartbeat ping to printer {printer.Name}");
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
                        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
                        return;
                    }

                    _ = sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                } while (!result.EndOfMessage);
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
                _logger.LogWarning(jsonEx, "Failed to parse JSON message from printer {PrinterName}: {Message}",
                    printer.Name, sb.ToString()[..Math.Min(200, sb.Length)]);
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
                _logger.LogWarning($"Received unknown JSON-RPC message from printer {printer.Name}: {message}");
            }
        }
        catch (JsonException ex)
        {
            _logger.LogError($"Failed to parse JSON message from printer {printer.Name}: {ex.Message}. Message: {message}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error processing message from printer {printer.Name}. Message: {message}");
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
                _logger.LogWarning($"JSON-RPC error from printer {printer.Name}: {jsonRpcResponse.Error.Message} (Code: {jsonRpcResponse.Error.Code})");

                // Track JSON-RPC parse errors (code -32700) and trigger fallback if threshold exceeded
                if (jsonRpcResponse.Error.Code == -32700)
                {
                    _ = _parseErrorCounts.AddOrUpdate(printer.Id, 1, (key, value) => value + 1);
                    int errorCount = _parseErrorCounts[printer.Id];

                    if (errorCount >= MaxParseErrorsBeforeFallback)
                    {
                        _logger.LogWarning($"JSON-RPC parse error threshold ({MaxParseErrorsBeforeFallback}) exceeded for printer {printer.Name}. Triggering HTTP polling fallback.");
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
                await ProcessStatusUpdateAsync(statusObj, printer.Id, printer.BackendUrl, printer.CameraStreamUrl, null, ct);
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning($"Failed to parse JSON-RPC response from printer {printer.Name}: {ex.Message}");

            // Track parse errors and trigger fallback if threshold exceeded
            _ = _parseErrorCounts.AddOrUpdate(printer.Id, 1, (key, value) => value + 1);
            int errorCount = _parseErrorCounts[printer.Id];

            if (errorCount >= MaxParseErrorsBeforeFallback)
            {
                _logger.LogWarning($"Parse error threshold ({MaxParseErrorsBeforeFallback}) exceeded for printer {printer.Name}. Triggering HTTP polling fallback.");
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
        _logger.LogDebug($"Received notification {method} from printer {printer.Name}");

        switch (method)
        {
            case "notify_status_update":
                if (root.TryGetProperty("params", out JsonElement p) && p.ValueKind == JsonValueKind.Array && p.GetArrayLength() > 0)
                {
                    _logger.LogDebug("Processing notify_status_update for printer {PrinterName}. Status data: {StatusData}",
                        printer.Name, p[0].GetRawText());
                    await ProcessStatusUpdateAsync(p[0], printer.Id, printer.BackendUrl, printer.CameraStreamUrl, null, ct);
                }

                break;

            case "notify_klippy_disconnected":
                _logger.LogWarning("Klippy disconnected for printer {PrinterName}, switching to HTTP polling mode", printer.Name);
                _klippyReadyState[printer.Id] = false;
                SetPollingMode(printer.Id, PollingMode.HttpPollingOnly, "Klippy disconnected");
                await SendOfflineStatusAsync(printer.Id, ct);
                break;

            case "notify_klippy_ready":
                _logger.LogInformation("Klippy ready for printer {PrinterName}, switching to WebSocket real-time mode", printer.Name);
                _klippyReadyState[printer.Id] = true;
                SetPollingMode(printer.Id, PollingMode.WebSocketRealTime, "Klippy ready");
                break;

            case "notify_klippy_shutdown":
                _logger.LogWarning("Klippy shutdown for printer {PrinterName}, switching to HTTP polling mode", printer.Name);
                _klippyReadyState[printer.Id] = false;
                SetPollingMode(printer.Id, PollingMode.HttpPollingOnly, "Klippy shutdown");
                await SendShutdownStatusAsync(printer.Id, ct);
                break;

            default:
                _logger.LogDebug($"Unhandled notification method {method} from printer {printer.Name}");
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

        _logger.LogDebug($"Processing status update for printer {printerId}. Raw status: {statusObj.GetRawText()}");

        // Initialize klippy ready state from webhooks if not yet set
        // This handles the initial subscription response which contains the current klippy state
        if (!_klippyReadyState.ContainsKey(printerId) &&
            statusObj.TryGetProperty("webhooks", out JsonElement wh) &&
            wh.TryGetProperty("state", out JsonElement ws) && ws.ValueKind == JsonValueKind.String)
        {
            string? webhooksState = ws.GetString();
            bool isReady = webhooksState == "ready";
            _klippyReadyState[printerId] = isReady;
            _logger.LogInformation($"Initialized klippyReadyState for printer {printerId}: {isReady} (webhooks.state={webhooksState})");
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
            { x = pos[0].GetDouble(); }
            catch { }
            try
            { y = pos[1].GetDouble(); }
            catch { }
            try
            { z = pos[2].GetDouble(); }
            catch { }
        }

        // Extract homed_axes
        if (th.TryGetProperty("homed_axes", out JsonElement ha) && ha.ValueKind == JsonValueKind.String)
        {
            homedAxes = ha.GetString();
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

        if (!string.IsNullOrEmpty(homedAxes))
        {
            state.HomedAxes = homedAxes;
        }

        // Emit separate toolhead event
        try
        {
            var update = new PrinterToolheadUpdate(printerId, x, y, z, homedAxes);
            _logger.LogDebug($"Emitting toolhead update for printer {printerId}: X={x}, Y={y}, Z={z}, HomedAxes={homedAxes}");
            await hub!.Clients.All.SendAsync("toolheadupdate", update, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to emit toolhead update for printer {printerId}");
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
            { temperature = t.GetDouble(); }
            catch { }
        }

        if (ex.TryGetProperty("target", out JsonElement tt) && tt.ValueKind is JsonValueKind.Number)
        {
            try
            { target = tt.GetDouble(); }
            catch { }
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
            var update = new PrinterExtruderUpdate(printerId, temperature, target);
            _logger.LogDebug($"Emitting extruder update for printer {printerId}: Temp={temperature}, Target={target}");
            await hub!.Clients.All.SendAsync("extruderupdate", update, ct);
        }
        catch (Exception extruderEx)
        {
            _logger.LogError(extruderEx, $"Failed to emit extruder update for printer {printerId}");
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
            { temperature = t.GetDouble(); }
            catch { }
        }

        if (hb.TryGetProperty("target", out JsonElement tt) && tt.ValueKind is JsonValueKind.Number)
        {
            try
            { target = tt.GetDouble(); }
            catch { }
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
            var update = new PrinterHeaterBedUpdate(printerId, temperature, target);
            _logger.LogDebug($"Emitting heater bed update for printer {printerId}: Temp={temperature}, Target={target}");
            await hub!.Clients.All.SendAsync("heaterbedupdate", update, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to emit heater bed update for printer {printerId}");
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
            catch { }
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

        // Update persistent state
        if (stateValue != null)
        {
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

        // Emit state update event if any state/progress/jobName changed
        if (stateValue != null || progress.HasValue || jobName != null)
        {
            try
            {
                var update = new PrinterStateUpdate(printerId, stateValue, progress, jobName);
                _logger.LogDebug($"Emitting state update for printer {printerId}: State={stateValue}, Progress={progress}, JobName={jobName}");
                await hub!.Clients.All.SendAsync("stateupdate", update, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to emit state update for printer {printerId}");
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
            bool isOnline = _klippyReadyState.TryGetValue(printerId, out var ready) && ready;

            // Send consolidated update for offline status and overall state sync
            var update = new PrinterStatusUpdate(
                printerId,
                isOnline,
                PrinterStateNormalizer.NormalizeState(state.State),
                state.Progress,
                state.JobName,
                ThumbnailUrl: state.ThumbnailUrl,
                CameraStreamUrl: state.CameraStreamUrl,
                X: state.X, Y: state.Y, Z: state.Z,
                HotendTemp: state.HotendTemp,
                BedTemp: state.BedTemp,
                HotendTarget: state.HotendTarget,
                BedTarget: state.BedTarget,
                HomedAxes: state.HomedAxes,
                SpoolInfo: spoolInfo
            );

            _logger.LogDebug($"Emitting consolidated status for printer {printerId}: IsOnline={isOnline}, X={state.X}, Y={state.Y}, Z={state.Z}, HotendTemp={state.HotendTemp}, HotendTarget={state.HotendTarget}, BedTemp={state.BedTemp}, BedTarget={state.BedTarget}, HomedAxes={state.HomedAxes}");

            // Update cache before broadcasting to clients
            var cacheUpdate = new PrinterStatusDto(
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
                SpoolInfo: spoolInfo
            );
            _statusCacheWriter.UpdateStatus(cacheUpdate);

            _logger.LogInformation($"[MoonrakerSubscriptionService] Broadcasting printerupdated for {printerId} via SignalR");
            _logger.LogDebug($"[MoonrakerSubscriptionService] Hub is null: {hub == null}");
            await hub!.Clients.All.SendAsync("printerupdated", update, ct);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug($"Consolidated status for printer {printerId} was cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to emit consolidated status for printer {printerId}");
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
                _logger.LogError($"Hub context is null, cannot send offline status for printer {printerId}");
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
                SpoolInfo: null
            );

            // Update cache before broadcasting to clients
            var offlineCacheUpdate = new PrinterStatusDto(
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
                SpoolInfo: null
            );
            _statusCacheWriter.UpdateStatus(offlineCacheUpdate);

            _logger.LogInformation($"[MoonrakerSubscriptionService] Broadcasting printerupdated (offline) for {printerId} via SignalR");
            await hub.Clients.All.SendAsync("printerupdated", offlineUpdate, ct);
            _logger.LogDebug($"Sent offline status for printer {printerId}");
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug($"Offline status update for printer {printerId} was cancelled");
        }
        catch (Exception sendEx)
        {
            _logger.LogError(sendEx, $"Failed to send offline status for printer {printerId}: {sendEx.Message}");
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
                _logger.LogError($"Hub context is null, cannot send shutdown status for printer {printerId}");
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
                SpoolInfo: null
            );

            _logger.LogInformation($"[MoonrakerSubscriptionService] Broadcasting printerupdated (shutdown) for {printerId} via SignalR");
            await hub.Clients.All.SendAsync("printerupdated", shutdownUpdate, ct);
            _logger.LogDebug($"Sent shutdown status for printer {printerId}");
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug($"Shutdown status update for printer {printerId} was cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to send shutdown status for printer {printerId}: {ex.Message}");
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

            _logger.LogInformation($"Set polling mode for printer {printerId} to {mode}: {reason}");

            // Log state transition if mode changed
            if (_pollingModes.TryGetValue(printerId, out PollingMode previousMode) && previousMode != mode)
            {
                _logger.LogDebug($"Polling mode transition for printer {printerId}: {previousMode} -> {mode}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to set polling mode for printer {printerId} to {mode}: {ex.Message}");
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
            var toolheadData = await QueryHomedAxesAsync(serverUrl, ct);
            if (!string.IsNullOrEmpty(toolheadData))
            {
                var state = _printerStates.GetOrAdd(printerId, _ => new PrinterState());
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

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
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

            // Get the active spool ID from Moonraker
            int? activeSpoolId = await moonrakerClient.GetSpoolmanActiveSpoolAsync(serverUrl, ct);
            if (activeSpoolId == null)
            {
                return new PrinterSpoolInfoDto(HasActiveSpool: false);
            }

            // Return basic info - Spoolman detailed fetch removed to avoid API layer dependency
            // In a backend plugin, we only report that there's an active spool
            return new PrinterSpoolInfoDto(
                HasActiveSpool: true,
                ActiveSpoolId: activeSpoolId
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetSpoolInfoAsync: Exception occurred during spool detection for {ServerUrl}", serverUrl);
            // If any operations fail, just return no spool info
            return new PrinterSpoolInfoDto(HasActiveSpool: false);
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
                _logger.LogDebug($"HTTP polling fallback retrieved status for printer {printer.Name}: State={compositeStatus.State}, IsOnline={compositeStatus.IsOnline}");

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
                    spoolInfo
                );

                try
                {
                    if (hub == null)
                    {
                        _logger.LogError($"Hub context is null in HTTP polling fallback for printer {printer.Name}");
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
                    _logger.LogDebug($"HTTP polling fallback status update for printer {printer.Name} was cancelled");
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
}

// Connection metrics and retry logic helpers
internal sealed class ConnectionMetrics
{
    public int ReconnectAttempts { get; set; }

    public DateTime LastConnected { get; set; }

    public DateTime LastReconnectAttempt { get; set; }

    public TimeSpan GetNextBackoffDelay() => TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, Math.Min(ReconnectAttempts, 8))));

    public void Reset() { ReconnectAttempts = 0; LastConnected = DateTime.UtcNow; }

    public void IncrementAttempts() { ReconnectAttempts++; LastReconnectAttempt = DateTime.UtcNow; }
}

internal static class MoonrakerErrors
{
    public static bool IsTransientError(Exception ex) => ex switch
    {
        OperationCanceledException => false, // Don't retry on cancellation
        WebSocketException wsEx => wsEx.WebSocketErrorCode switch
        {
            WebSocketError.ConnectionClosedPrematurely => true,
            WebSocketError.Faulted => true,
            _ => false
        },
        HttpRequestException => true, // Network connectivity issues
        TimeoutException => true,
        _ => false
    };

    public static bool IsFatalError(Exception ex) => ex switch
    {
        ArgumentException => true, // Configuration issues
        UriFormatException => true, // Invalid URLs
        UnauthorizedAccessException => true, // Auth failures
        _ => false
    };
}
