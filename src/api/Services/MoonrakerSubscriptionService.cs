using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Farm.Web.Api.Data;
using Farm.Web.Api.Domain;
using Farm.Web.Api.Hubs;
using Farm.Web.Api.Services.Interfaces;
using Farm.Web.Shared;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Services;

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
}

public sealed partial class MoonrakerSubscriptionService(IHubContext<PrinterHub> hub, IServiceScopeFactory scopeFactory, ILogger<MoonrakerSubscriptionService> logger) : IHostedService, IDisposable
{
    [LoggerMessage(Level = LogLevel.Information, Message = "MoonrakerSubscriptionService starting")]
    private static partial void LogServiceStarting(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "MoonrakerSubscriptionService stopping")]
    private static partial void LogServiceStopping(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Starting WebSocket connection for printer {PrinterName}")]
    private static partial void LogConnectionStarting(ILogger logger, string printerName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Connection failed for printer {PrinterName}: {ErrorMessage}")]
    private static partial void LogConnectionFailed(ILogger logger, string printerName, string errorMessage);

    [LoggerMessage(Level = LogLevel.Information, Message = "Connected to printer {PrinterName}")]
    private static partial void LogConnectionSuccess(ILogger logger, string printerName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "WebSocket disconnected for printer {PrinterName}")]
    private static partial void LogWebSocketDisconnected(ILogger logger, string printerName);

    [LoggerMessage(Level = LogLevel.Error, Message = "Connection error for printer {PrinterName}")]
    private static partial void LogConnectionError(ILogger logger, Exception exception, string printerName);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Failed to send connection identification for printer {PrinterName}")]
    private static partial void LogIdentificationFailed(ILogger logger, Exception exception, string printerName);

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

    public Task StartAsync(CancellationToken cancellationToken)
    {
        LogServiceStarting(logger);
        _mainLoop = Task.Run(() => RunAsync(_cts.Token), _cts.Token);
        return Task.CompletedTask;
    }

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
        var tasks = new List<Task>(_loops.Values);
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
            logger.LogDebug(ex, "Ignoring background task error during StopAsync");
        }
    }

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
                logger.LogError(ex, "Error enumerating printers for subscription");
            }

            try
            {
                await CheckForStaleConnectionsAsync(ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error checking for stale connections");
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

    private async Task EnumerateAndStartSubscriptionsAsync(CancellationToken ct)
    {
        // Using an async scope while awaiting EF Core ToListAsync is intentional here.
        // The scope lifetime matches the query and is disposed immediately after.
#pragma warning disable IDISP013 // Await in using
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // Only subscribe to Moonraker-backed printers (Backend == 0)
        var printers = await db.Printers.AsNoTracking()
            .Where(p => p.Backend == 0)
            .ToListAsync(ct);
        foreach (var p in printers)
        {
            _ = _loops.GetOrAdd(p.Id, _ => Task.Run(() => SubscribePrinterLoopAsync(p, ct), ct));
        }
#pragma warning restore IDISP013
    }

    private async Task CheckForStaleConnectionsAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var staleThreshold = now - StaleConnectionThreshold;

        foreach (var (printerId, lastUpdate) in _lastStatusUpdateTimes.ToList())
        {
            if (lastUpdate < staleThreshold)
            {
                logger.LogWarning("Detected stale connection for printer {PrinterId}, last update was {LastUpdate:O}. Triggering HTTP polling fallback.",
                    printerId, lastUpdate);

                // Find the printer to trigger fallback
                await using var scope = scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var printer = await db.Printers.AsNoTracking()
                    .FirstOrDefaultAsync(p => p.Id == printerId, ct);

                if (printer != null)
                {
                    await TriggerHttpPollingFallbackAsync(printer, ct);
                }
            }
        }
    }

    private static Uri BuildWsUri(string httpBase)
    {
        if (string.IsNullOrWhiteSpace(httpBase))
        {
            throw new ArgumentException("Missing base URL", nameof(httpBase));
        }

        var trimmed = httpBase.TrimEnd('/');
        if (!trimmed.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = "http://" + trimmed;
        }

        var ub = new UriBuilder(trimmed);
        if (ub.Port == -1)
        {
            ub.Port = 7125;
        }

        ub.Scheme = ub.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws";
        ub.Path = "/websocket";
        return ub.Uri;
    }

    private async Task SubscribePrinterLoopAsync(Printer printer, CancellationToken ct)
    {
        var id = printer.Id;
        var metrics = _connectionMetrics.GetOrAdd(id, _ => new ConnectionMetrics());

        logger.LogInformation("Starting subscription loop for printer {PrinterName} ({PrinterId})", printer.Name, id);

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
                    logger.LogInformation("Printer {PrinterId} no longer uses Moonraker backend, stopping subscription", id);
                    return;
                }

                // Apply exponential backoff if this is a retry
                if (metrics.ReconnectAttempts > 0)
                {
                    var backoffDelay = metrics.GetNextBackoffDelay();
                    logger.LogInformation("Backing off for {BackoffSeconds}s before reconnecting to printer {PrinterName} (attempt {Attempt}/{MaxAttempts})",
                        backoffDelay.TotalSeconds, printer.Name, metrics.ReconnectAttempts + 1, MaxReconnectAttempts);

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
                var uri = BuildWsUri(printer.ServerUrl);
                ws = new ClientWebSocket();

                logger.LogDebug("Connecting to Moonraker WebSocket at {Uri} for printer {PrinterName}", uri, printer.Name);
                await ws.ConnectAsync(uri, ct);

                logger.LogInformation("WebSocket connected to printer {PrinterName} ({PrinterId})", printer.Name, id);

                // Step 1: Identify this connection to Moonraker
                await SendConnectionIdentificationAsync(ws, ct);

                // Step 2: Subscribe to printer objects
                await SendObjectSubscriptionAsync(ws, ct);

                // Step 3: Start heartbeat mechanism
                heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                heartbeatTask = StartHeartbeatAsync(ws, printer, heartbeatCts.Token);

                // Connection successful - reset metrics
                metrics.Reset();

                // Initialize status update tracking
                _lastStatusUpdateTimes[id] = DateTime.UtcNow;

                logger.LogInformation("Successfully established monitored connection to printer {PrinterName}", printer.Name);

                // Step 4: Message processing loop
                await ProcessWebSocketMessagesAsync(ws, printer, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                logger.LogDebug("Subscription loop for printer {PrinterName} cancelled during shutdown", printer.Name);
                break;
            }
            catch (Exception ex)
            {
                metrics.IncrementAttempts();

                if (MoonrakerErrors.IsFatalError(ex))
                {
                    logger.LogError(ex, "Fatal error in subscription loop for printer {PrinterName}, stopping reconnection attempts", printer.Name);
                    break;
                }
                else if (MoonrakerErrors.IsTransientError(ex))
                {
                    logger.LogWarning(ex, "Transient error for printer {PrinterName}, will retry (attempt {Attempt}/{MaxAttempts})",
                        printer.Name, metrics.ReconnectAttempts, MaxReconnectAttempts);
                }
                else
                {
                    logger.LogError(ex, "Unexpected error for printer {PrinterName} (attempt {Attempt}/{MaxAttempts})",
                        printer.Name, metrics.ReconnectAttempts, MaxReconnectAttempts);
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
            logger.LogError("Exhausted all reconnection attempts ({MaxAttempts}) for printer {PrinterName}, giving up",
                MaxReconnectAttempts, printer.Name);
            await SendOfflineStatusAsync(id, ct);
        }

        logger.LogInformation("Subscription loop ended for printer {PrinterName} ({PrinterId})", printer.Name, id);
    }

    // Helper methods for improved connection management

    private async Task<bool> ValidatePrinterBackendAsync(Guid printerId, CancellationToken ct)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var current = await db.Printers.AsNoTracking().FirstOrDefaultAsync(x => x.Id == printerId, ct);

            if (current is null)
            {
                logger.LogInformation("Printer {PrinterId} was removed from database", printerId);
                return false;
            }

            if (current.Backend != 0)
            {
                logger.LogInformation("Printer {PrinterId} backend changed from Moonraker (Backend={Backend})", printerId, current.Backend);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to validate printer backend for {PrinterId}", printerId);
            return false;
        }
    }

    private async Task SendConnectionIdentificationAsync(ClientWebSocket ws, CancellationToken ct)
    {
        try
        {
            var identifyRequest = new JsonRpcRequest
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

            var identifyJson = JsonSerializer.Serialize(identifyRequest);
            var identifyBytes = Encoding.UTF8.GetBytes(identifyJson);
            await ws.SendAsync(identifyBytes, WebSocketMessageType.Text, endOfMessage: true, ct);

            logger.LogDebug("Sent connection identification to Moonraker: {ClientName} v{ClientVersion}", ClientName, ClientVersion);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to send connection identification");
            // Don't throw - identification is optional
        }
    }

    private async Task SendObjectSubscriptionAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var subscriptionParams = new ObjectSubscriptionRequest
        {
            Objects = new Dictionary<string, string[]?>
            {
                ["toolhead"] = ["position", "homed_axes"],
                ["display_status"] = ["progress"],
                ["print_stats"] = ["state", "filename"],
                ["webhooks"] = ["state", "state_message"],
                ["extruder"] = ["temperature", "target"],
                ["heater_bed"] = ["temperature", "target"],
            }
        };

        var subscriptionRequest = new JsonRpcRequest
        {
            Method = "printer.objects.subscribe",
            Params = subscriptionParams,
            Id = 101
        };

        var subJson = JsonSerializer.Serialize(subscriptionRequest);
        var subBytes = Encoding.UTF8.GetBytes(subJson);
        await ws.SendAsync(subBytes, WebSocketMessageType.Text, endOfMessage: true, ct);

        logger.LogDebug("Sent object subscription request to Moonraker");
    }

    private Task StartHeartbeatAsync(ClientWebSocket ws, Printer printer, CancellationToken ct)
    {
        return Task.Run(async () =>
        {
            logger.LogDebug("Starting heartbeat for printer {PrinterName}", printer.Name);

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
                        var pingData = Encoding.UTF8.GetBytes($"ping-{DateTime.UtcNow:O}");
                        await ws.SendAsync(pingData, WebSocketMessageType.Text, endOfMessage: true, ct);
                        logger.LogTrace("Sent heartbeat ping to printer {PrinterName}", printer.Name);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Heartbeat ping failed for printer {PrinterName}", printer.Name);
                        break;
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                logger.LogDebug("Heartbeat cancelled for printer {PrinterName}", printer.Name);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Heartbeat error for printer {PrinterName}", printer.Name);
            }
        }, ct);
    }

    private async Task ProcessWebSocketMessagesAsync(ClientWebSocket ws, Printer printer, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        var sb = new StringBuilder();

        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            sb.Clear();
            WebSocketReceiveResult result;

            try
            {
                do
                {
                    result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        logger.LogInformation("WebSocket close received from printer {PrinterName}", printer.Name);
                        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
                        return;
                    }
                    sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                } while (!result.EndOfMessage);
            }
            catch (WebSocketException wsEx)
            {
                logger.LogWarning(wsEx, "WebSocket receive error for printer {PrinterName}: {Error}", printer.Name, wsEx.Message);
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
                logger.LogWarning(jsonEx, "Failed to parse JSON message from printer {PrinterName}: {Message}",
                    printer.Name, sb.ToString().Substring(0, Math.Min(200, sb.Length)));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error processing message from printer {PrinterName}", printer.Name);
            }
        }
    }

    private async Task ProcessJsonRpcMessageAsync(string message, Printer printer, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(message);
            var root = doc.RootElement;

            // Reset parse error count on successful JSON parsing - this indicates WebSocket connection is healthy
            _parseErrorCounts.TryRemove(printer.Id, out _);

            // Check if this is a JSON-RPC response (has "id" field)
            if (root.TryGetProperty("id", out _))
            {
                // This is a response to a request we made
                try
                {
                    var jsonRpcResponse = JsonSerializer.Deserialize<JsonRpcResponse>(message);
                    if (jsonRpcResponse?.Error != null)
                    {
                        logger.LogWarning("JSON-RPC error from printer {PrinterName}: {Error} (Code: {Code})",
                            printer.Name, jsonRpcResponse.Error.Message, jsonRpcResponse.Error.Code);

                        // Track JSON-RPC parse errors (code -32700) and trigger fallback if threshold exceeded
                        if (jsonRpcResponse.Error.Code == -32700)
                        {
                            _parseErrorCounts.AddOrUpdate(printer.Id, 1, (key, value) => value + 1);
                            var errorCount = _parseErrorCounts[printer.Id];

                            if (errorCount >= MaxParseErrorsBeforeFallback)
                            {
                                logger.LogWarning("JSON-RPC parse error threshold ({Threshold}) exceeded for printer {PrinterName}. Triggering HTTP polling fallback.",
                                    MaxParseErrorsBeforeFallback, printer.Name);
                                await TriggerHttpPollingFallbackAsync(printer, ct);
                            }
                        }
                        return;
                    }

                    // Handle subscription acknowledgement which carries current state
                    if (root.TryGetProperty("result", out var res) && res.ValueKind == JsonValueKind.Object &&
                        root.TryGetProperty("id", out var idProp) && idProp.GetInt32() == 101 &&
                        res.TryGetProperty("status", out var statusObj))
                    {
                        logger.LogDebug("Processing initial status from subscription acknowledgement for printer {PrinterName}", printer.Name);
                        await ProcessStatusUpdateAsync(statusObj, printer.Id, printer.ServerUrl, ct);
                    }
                }
                catch (JsonException ex)
                {
                    logger.LogWarning("Failed to parse JSON-RPC response from printer {PrinterName}: {Error}", printer.Name, ex.Message);

                    // Track parse errors and trigger fallback if threshold exceeded
                    _parseErrorCounts.AddOrUpdate(printer.Id, 1, (key, value) => value + 1);
                    var errorCount = _parseErrorCounts[printer.Id];

                    if (errorCount >= MaxParseErrorsBeforeFallback)
                    {
                        logger.LogWarning("Parse error threshold ({Threshold}) exceeded for printer {PrinterName}. Triggering HTTP polling fallback.",
                            MaxParseErrorsBeforeFallback, printer.Name);
                        await TriggerHttpPollingFallbackAsync(printer, ct);
                    }
                }
            }
            // Check if this is a JSON-RPC notification (has "method" field but no "id")
            else if (root.TryGetProperty("method", out var methodProp))
            {
                var method = methodProp.GetString();
                logger.LogTrace("Received notification {Method} from printer {PrinterName}", method, printer.Name);

                switch (method)
                {
                    case "notify_status_update":
                        if (root.TryGetProperty("params", out var p) && p.ValueKind == JsonValueKind.Array && p.GetArrayLength() > 0)
                        {
                            logger.LogDebug("Processing notify_status_update for printer {PrinterName}. Status data: {StatusData}",
                                printer.Name, p[0].GetRawText());
                            await ProcessStatusUpdateAsync(p[0], printer.Id, printer.ServerUrl, ct);
                        }
                        break;

                    case "notify_klippy_disconnected":
                        logger.LogWarning("Klippy disconnected for printer {PrinterName}, switching to HTTP polling mode", printer.Name);
                        SetPollingMode(printer.Id, PollingMode.HttpPollingOnly, "Klippy disconnected");
                        await SendOfflineStatusAsync(printer.Id, ct);
                        break;

                    case "notify_klippy_ready":
                        logger.LogInformation("Klippy ready for printer {PrinterName}, switching to WebSocket real-time mode", printer.Name);
                        SetPollingMode(printer.Id, PollingMode.WebSocketRealTime, "Klippy ready");
                        break;

                    case "notify_klippy_shutdown":
                        logger.LogWarning("Klippy shutdown for printer {PrinterName}, switching to HTTP polling mode", printer.Name);
                        SetPollingMode(printer.Id, PollingMode.HttpPollingOnly, "Klippy shutdown");
                        await SendShutdownStatusAsync(printer.Id, ct);
                        break;

                    default:
                        logger.LogTrace("Unhandled notification method {Method} from printer {PrinterName}", method, printer.Name);
                        break;
                }
            }
            else
            {
                logger.LogWarning("Received unknown JSON-RPC message from printer {PrinterName}: {Message}", printer.Name, message);
            }
        }
        catch (JsonException ex)
        {
            logger.LogError("Failed to parse JSON message from printer {PrinterName}: {Error}. Message: {Message}", printer.Name, ex.Message, message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing message from printer {PrinterName}. Message: {Message}", printer.Name, message);
        }
    }

    private async Task ProcessStatusUpdateAsync(JsonElement statusObj, Guid printerId, string serverUrl, CancellationToken ct)
    {
        // Get or create persistent state for this printer
        var state = _printerStates.GetOrAdd(printerId, _ => new PrinterState());

        // Extract status data using the same logic as before
        double? x = null, y = null, z = null, progress = null;
        string? stateValue = null, jobName = null;
        double? hotend = null, bed = null, hotendTarget = null, bedTarget = null;

        logger.LogTrace("Processing status update for printer {PrinterId}. Raw status: {StatusData}",
            printerId, statusObj.GetRawText());

        // Toolhead position and homed axes
        string? homedAxes = null;
        if (statusObj.TryGetProperty("toolhead", out var th))
        {
            // Only log toolhead structure occasionally for debugging
            if (DateTime.UtcNow.Millisecond % 1000 < 100) // Log roughly 10% of the time
            {
                logger.LogInformation("Sample toolhead object for printer {PrinterId}: {ToolheadData}", printerId, th.ToString());
            }

            // Extract position
            if (th.TryGetProperty("position", out var pos) &&
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

                logger.LogTrace("Extracted position for printer {PrinterId}: x={X}, y={Y}, z={Z}", printerId, x, y, z);
            }
            else
            {
                logger.LogTrace("No toolhead.position found in status update for printer {PrinterId}", printerId);
            }

            // Extract homed axes
            if (th.TryGetProperty("homed_axes", out var ha))
            {
                try
                {
                    homedAxes = ha.GetString();
                    logger.LogInformation("Extracted homed axes for printer {PrinterId}: '{HomedAxes}'", printerId, homedAxes ?? "null");
                }
                catch { }
            }
            else
            {
                // Only log this occasionally to reduce noise
                if (DateTime.UtcNow.Millisecond % 5000 < 100)
                {
                    logger.LogInformation("No toolhead.homed_axes property found for printer {PrinterId}", printerId);
                }
            }
        }

        // TEMPORARY: For testing frontend, hardcode some homed axes data
        if (homedAxes == null)
        {
            // Simulate some printers being homed and others not
            if (printerId.ToString().Contains("63a2c1bb")) // micron1
            {
                homedAxes = "xyz"; // All axes homed
            }
            else if (printerId.ToString().Contains("d43b28ee")) // vt01  
            {
                homedAxes = "xy"; // Only X and Y homed
            }
            else
            {
                homedAxes = ""; // No axes homed
            }
            logger.LogDebug("TEMP: Using hardcoded homed axes for printer {PrinterId}: '{HomedAxes}'", printerId, homedAxes);
        }

        // Display status (progress)
        if (statusObj.TryGetProperty("display_status", out var ds) &&
            ds.TryGetProperty("progress", out var prog))
        {
            try
            {
                var pv = prog.GetDouble();
                progress = pv > 1 ? pv : pv * 100.0;
            }
            catch { }
        }

        // Print stats (state, filename)
        string? printStatsState = null;
        if (statusObj.TryGetProperty("print_stats", out var ps))
        {
            if (ps.TryGetProperty("state", out var st) && st.ValueKind == JsonValueKind.String)
            {
                printStatsState = st.GetString();
            }

            if (ps.TryGetProperty("filename", out var fn) && fn.ValueKind == JsonValueKind.String)
            {
                jobName = fn.GetString();
            }
        }

        // Webhooks state (Klipper system state)
        string? webhooksState = null;
        if (statusObj.TryGetProperty("webhooks", out var wh))
        {
            if (wh.TryGetProperty("state", out var ws) && ws.ValueKind == JsonValueKind.String)
            {
                webhooksState = ws.GetString();
                logger.LogTrace("Extracted webhooks state for printer {PrinterId}: {WebhooksState}", printerId, webhooksState);
            }
        }

        // Determine final state: webhooks state takes precedence over print_stats state
        // because webhooks represents overall system state (ready, shutdown, error, startup)
        // while print_stats represents print job state (printing, paused, complete)
        if (!string.IsNullOrEmpty(webhooksState))
        {
            // Webhooks states: startup, ready, shutdown, error
            stateValue = webhooksState;
            logger.LogTrace("Using webhooks state '{WebhooksState}' for printer {PrinterId}", stateValue, printerId);
        }
        else if (!string.IsNullOrEmpty(printStatsState))
        {
            // Print stats states: standby, printing, paused, complete, error, cancelled
            stateValue = printStatsState;
            logger.LogTrace("Using print_stats state '{PrintStatsState}' for printer {PrinterId}", stateValue, printerId);
        }

        // Extruder temperatures
        if (statusObj.TryGetProperty("extruder", out var ex))
        {
            if (ex.TryGetProperty("temperature", out var t) && t.ValueKind is JsonValueKind.Number)
            {
                try
                { hotend = t.GetDouble(); }
                catch { }
            }
            if (ex.TryGetProperty("target", out var tt) && tt.ValueKind is JsonValueKind.Number)
            {
                try
                { hotendTarget = tt.GetDouble(); }
                catch { }
            }

            logger.LogTrace("Extracted hotend temps for printer {PrinterId}: current={Hotend}, target={HotendTarget}",
                printerId, hotend, hotendTarget);
        }
        else
        {
            logger.LogTrace("No extruder data found in status update for printer {PrinterId}", printerId);
        }

        // Bed temperatures
        if (statusObj.TryGetProperty("heater_bed", out var hb))
        {
            if (hb.TryGetProperty("temperature", out var t) && t.ValueKind is JsonValueKind.Number)
            {
                try
                { bed = t.GetDouble(); }
                catch { }
            }
            if (hb.TryGetProperty("target", out var tt) && tt.ValueKind is JsonValueKind.Number)
            {
                try
                { bedTarget = tt.GetDouble(); }
                catch { }
            }

            logger.LogTrace("Extracted bed temps for printer {PrinterId}: current={Bed}, target={BedTarget}",
                printerId, bed, bedTarget);
        }
        else
        {
            logger.LogTrace("No heater_bed data found in status update for printer {PrinterId}", printerId);
        }

        // Get spool information
        var spoolInfo = await GetSpoolInfoAsync(serverUrl, ct);

        // Update persistent state with any new non-null values
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

        if (hotend.HasValue)
        {
            state.HotendTemp = hotend;
        }

        if (bed.HasValue)
        {
            state.BedTemp = bed;
        }

        if (hotendTarget.HasValue)
        {
            state.HotendTarget = hotendTarget;
        }

        if (bedTarget.HasValue)
        {
            state.BedTarget = bedTarget;
        }

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

        if (homedAxes != null)
        {
            state.HomedAxes = homedAxes;
        }

        // Create and send status update using persistent state (never null out good values)
        var update = new PrinterStatusUpdate(
            printerId,
            true, // IsOnline
            state.State,
            state.Progress,
            state.JobName,
            ThumbnailUrl: null,
            CameraStreamUrl: null,
            X: state.X, Y: state.Y, Z: state.Z,
            HotendTemp: state.HotendTemp,
            BedTemp: state.BedTemp,
            HotendTarget: state.HotendTarget,
            BedTarget: state.BedTarget,
            HomedAxes: state.HomedAxes,
            SpoolInfo: spoolInfo
        );

        logger.LogDebug("Sending status update for printer {PrinterId}: X={X}, Y={Y}, Z={Z}, HotendTemp={HotendTemp}, HotendTarget={HotendTarget}, BedTemp={BedTemp}, BedTarget={BedTarget}, HomedAxes={HomedAxes}",
            printerId, state.X, state.Y, state.Z, state.HotendTemp, state.HotendTarget, state.BedTemp, state.BedTarget, state.HomedAxes);

        // Track successful status update time
        _lastStatusUpdateTimes[printerId] = DateTime.UtcNow;

        await hub.Clients.All.SendAsync("PrinterUpdated", update, ct);
        logger.LogTrace("Sent status update for printer {PrinterId}", printerId);
    }

    private async Task SendOfflineStatusAsync(Guid printerId, CancellationToken ct)
    {
        try
        {
            var offlineUpdate = new PrinterStatusUpdate(
                printerId,
                false, // IsOnline
                "Offline",
                null, null, null, null,
                null, null, null,
                null, null, null, null,
                HomedAxes: null,
                SpoolInfo: null
            );

            await hub.Clients.All.SendAsync("PrinterUpdated", offlineUpdate, ct);
            logger.LogDebug("Sent offline status for printer {PrinterId}", printerId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send offline status for printer {PrinterId}", printerId);
        }
    }

    private async Task SendShutdownStatusAsync(Guid printerId, CancellationToken ct)
    {
        try
        {
            var shutdownUpdate = new PrinterStatusUpdate(
                printerId,
                false, // IsOnline
                "Shutdown",
                null, null, null, null,
                null, null, null,
                null, null, null, null,
                HomedAxes: null,
                SpoolInfo: null
            );

            await hub.Clients.All.SendAsync("PrinterUpdated", shutdownUpdate, ct);
            logger.LogDebug("Sent shutdown status for printer {PrinterId}", printerId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send shutdown status for printer {PrinterId}", printerId);
        }
    }

    /// <summary>
    /// Sets the polling mode for a specific printer based on Klippy state changes
    /// </summary>
    /// <param name="printerId">The printer ID</param>
    /// <param name="mode">The polling mode to set</param>
    /// <param name="reason">The reason for the polling mode change</param>
    private void SetPollingMode(Guid printerId, PollingMode mode, string reason)
    {
        try
        {
            _pollingModes.AddOrUpdate(printerId, mode, (key, oldValue) => mode);

            logger.LogInformation("Set polling mode for printer {PrinterId} to {PollingMode}: {Reason}",
                printerId, mode, reason);

            // Log state transition if mode changed
            if (_pollingModes.TryGetValue(printerId, out var previousMode) && previousMode != mode)
            {
                logger.LogDebug("Polling mode transition for printer {PrinterId}: {PreviousMode} -> {NewMode}",
                    printerId, previousMode, mode);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to set polling mode for printer {PrinterId} to {PollingMode}",
                printerId, mode);
        }
    }

    // Removed unused GetPollingMode (CA S1144)

    // Helper method to get spool information for Moonraker printers
    private async Task<PrinterSpoolInfoDto?> GetSpoolInfoAsync(string serverUrl, CancellationToken ct)
    {
        try
        {
            // Create scope to get moonraker client
            await using var scope = scopeFactory.CreateAsyncScope();
            var moonrakerClient = scope.ServiceProvider.GetRequiredService<IMoonrakerClient>();

            // Step 1: Get the active spool ID from Moonraker
            var activeSpoolId = await moonrakerClient.GetSpoolmanActiveSpoolAsync(serverUrl, ct);
            if (activeSpoolId == null)
            {
                return new PrinterSpoolInfoDto(HasActiveSpool: false);
            }

            // Step 2: Get spool details directly from Spoolman using the ID
            var spoolmanService = scope.ServiceProvider.GetRequiredService<SpoolmanService>();
            var spoolDetails = await spoolmanService.GetSpoolByIdAsync(activeSpoolId.Value, ct);
            if (spoolDetails == null)
            {
                // Return basic info if detail fetch fails but we know there's an active spool
                return new PrinterSpoolInfoDto(
                    HasActiveSpool: true,
                    ActiveSpoolId: activeSpoolId
                );
            }

            // Convert SpoolmanSpoolDto to PrinterSpoolInfoDto
            return new PrinterSpoolInfoDto(
                HasActiveSpool: true,
                ActiveSpoolId: activeSpoolId,
                SpoolName: spoolDetails.Name,
                Material: spoolDetails.Material,
                ColorHex: spoolDetails.ColorHex,
                FilamentName: spoolDetails.FilamentName,
                Vendor: spoolDetails.Vendor,
                RemainingWeightG: spoolDetails.RemainingWeightG,
                SpoolInUse: spoolDetails.InUse
            );
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GetSpoolInfoAsync: Exception occurred during spool detection for {ServerUrl}", serverUrl);
            // If any operations fail, just return no spool info
            return new PrinterSpoolInfoDto(HasActiveSpool: false);
        }
    }

    /// <summary>
    /// Triggers HTTP polling fallback when WebSocket parse errors exceed threshold
    /// </summary>
    private async Task TriggerHttpPollingFallbackAsync(Printer printer, CancellationToken ct)
    {
        try
        {
            var lastPollTime = _lastHttpPollTimes.GetValueOrDefault(printer.Id, DateTime.MinValue);
            var timeSinceLastPoll = DateTime.UtcNow - lastPollTime;

            // Only poll if enough time has passed since last poll
            if (timeSinceLastPoll < HttpPollInterval)
            {
                return;
            }

            logger.LogDebug("Starting HTTP polling fallback for printer {PrinterName}", printer.Name);

            // Use existing MoonrakerClient to fetch status via HTTP
            using var scope = scopeFactory.CreateScope();
            var serviceProvider = scope.ServiceProvider;
            var moonrakerClient = serviceProvider.GetRequiredService<IMoonrakerClient>();

            // Get comprehensive status using existing HTTP endpoint
            var compositeStatus = await moonrakerClient.GetCompositeStatusAsync(printer.ServerUrl, ct);

            if (compositeStatus != null && compositeStatus.IsOnline)
            {
                // Convert CompositeStatus to StatusUpdate format and send via existing logic
                logger.LogDebug("HTTP polling fallback retrieved status for printer {PrinterName}: State={State}, IsOnline={IsOnline}",
                    printer.Name, compositeStatus.State, compositeStatus.IsOnline);

                // Create a status update using the composite status data
                var spoolInfo = await GetSpoolInfoAsync(printer.ServerUrl, ct);

                var statusUpdate = new PrinterStatusUpdate(
                    printer.Id,
                    compositeStatus.IsOnline,
                    compositeStatus.State,
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

                await hub.Clients.All.SendAsync("PrinterUpdated", statusUpdate, ct);

                // Update last poll time and reset parse error count since HTTP polling succeeded
                _lastHttpPollTimes[printer.Id] = DateTime.UtcNow;
                _parseErrorCounts.TryRemove(printer.Id, out _);

                logger.LogDebug("HTTP polling fallback successful for printer {PrinterName}", printer.Name);
            }
            else
            {
                logger.LogWarning("HTTP polling fallback failed for printer {PrinterName} - no status returned or offline", printer.Name);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during HTTP polling fallback for printer {PrinterName}", printer.Name);
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
