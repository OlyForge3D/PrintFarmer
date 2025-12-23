#pragma warning disable S1144 // Unused classes and properties reserved for future use

using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.SignalR;
using Farm.Infrastructure.Telemetry;
using Microsoft.AspNetCore.SignalR;

namespace Farm.Backend.Plugin.OctoPrint;

/// <summary>
/// WebSocket adapter for OctoPrint real-time status updates.
/// Implements dual-layer architecture: WebSocket primary + HTTP polling fallback.
/// Maintains persistent WebSocket connection to /sockjs/websocket endpoint,
/// with automatic fallback to HTTP polling every 10 seconds if WebSocket fails.
/// </summary>
public sealed class OctoPrintWebSocketAdapter : IDisposable
{
    private readonly IUnifiedLoggingService _logger;
    private readonly IOctoPrintClient _octoPrintClient;
    private readonly IHubContext<PrinterHub> _hub;
    private readonly IPrinterStatusCacheWriter _statusCacheWriter;
    private readonly Guid _printerId;
    private readonly Printer _printer;
    private readonly CancellationTokenSource _cts = new();

    private ClientWebSocket? _webSocket;
    private Task? _receiveLoopTask;
    private bool _disposed = false;
    private DateTime _lastSuccessfulUpdate = DateTime.UtcNow;
    private int _consecutiveFailures = 0;
    private bool _isAuthenticated = false;
    private string? _sessionToken;

    // API state tracking
    private string _apiState = "unset"; // "responding", "authFail", "noResponse"
    private string _socketState = "unopened"; // "unopened", "connecting", "authenticated", "error", "closed"

    // Polling fallback when WebSocket fails
    private readonly TimeSpan _pollingInterval = TimeSpan.FromSeconds(5);
    private DateTime _lastHttpPoll = DateTime.MinValue;

    public string SocketState => _socketState;
    public string ApiState => _apiState;
    public bool IsConnected => _webSocket?.State == WebSocketState.Open && _isAuthenticated;

    public OctoPrintWebSocketAdapter(
        Guid printerId,
        Printer printer,
        IUnifiedLoggingService logger,
        IOctoPrintClient octoPrintClient,
        IHubContext<PrinterHub> hub,
        IPrinterStatusCacheWriter statusCacheWriter)
    {
        _printerId = printerId;
        _printer = printer;
        _logger = logger;
        _octoPrintClient = octoPrintClient;
        _hub = hub;
        _statusCacheWriter = statusCacheWriter;
    }

    /// <summary>
    /// Establishes WebSocket connection and starts receive loop.
    /// Handles authentication and fallback to HTTP polling on failure.
    /// </summary>
    public async Task ConnectAsync(CancellationToken ct)
    {
        try
        {
            _logger.LogInformation($"OctoPrint WebSocket {_printerId}: Attempting connection to {_printer.ServerUrl}");
            _socketState = "connecting";

            // Get session token first via HTTP
            await AcquireSessionTokenAsync(ct);

            // Convert HTTP URL to WebSocket URL
            string wsUrl = ConvertToWebSocketUrl(_printer.ServerUrl);
            _logger.LogDebug($"OctoPrint WebSocket {_printerId}: Connecting to {wsUrl}");

            // Create WebSocket connection
            _webSocket = new ClientWebSocket();
            _webSocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);

            await _webSocket.ConnectAsync(new Uri(wsUrl), ct);
            _logger.LogInformation($"OctoPrint WebSocket {_printerId}: Connected");
            _socketState = "connecting";

            // Authenticate
            await SendAuthMessageAsync(ct);
            _isAuthenticated = true;
            _socketState = "authenticated";
            _apiState = "responding";
            _consecutiveFailures = 0;

            _logger.LogInformation($"OctoPrint WebSocket {_printerId}: Authenticated");

            // Start receive loop
            _receiveLoopTask = Task.Run(() => ReceiveLoopAsync(_cts.Token), _cts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"OctoPrint WebSocket {_printerId}: Connection failed, will use HTTP polling fallback");
            _socketState = "error";
            _apiState = "noResponse";
            _isAuthenticated = false;
            _webSocket?.Dispose();
            _webSocket = null;
            // Don't throw - let polling fallback take over
        }
    }

    /// <summary>
    /// Main WebSocket receive loop. Handles incoming messages and broadcasts status updates.
    /// Implements automatic fallback to HTTP polling on WebSocket failure.
    /// </summary>
    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        byte[] buffer = new byte[4096];

        try
        {
            while (!ct.IsCancellationRequested && _webSocket?.State == WebSocketState.Open)
            {
                try
                {
                    WebSocketReceiveResult result = await _webSocket.ReceiveAsync(
                        new ArraySegment<byte>(buffer), ct);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        _logger.LogInformation($"OctoPrint WebSocket {_printerId}: Received close frame");
                        _socketState = "closed";
                        break;
                    }

                    if (result.Count > 0)
                    {
                        string message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        _logger.LogDebug($"OctoPrint WebSocket {_printerId}: Received message: {message.Substring(0, Math.Min(100, message.Length))}...");

                        await HandleWebSocketMessageAsync(message, ct);
                        _lastSuccessfulUpdate = DateTime.UtcNow;
                        _consecutiveFailures = 0;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"OctoPrint WebSocket {_printerId}: Error receiving message");
                    _consecutiveFailures++;
                    _apiState = "noResponse";

                    if (_consecutiveFailures >= 3)
                    {
                        _logger.LogWarning($"OctoPrint WebSocket {_printerId}: Too many failures, switching to HTTP polling fallback");
                        _socketState = "error";
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogInformation($"OctoPrint WebSocket {_printerId}: Receive loop cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"OctoPrint WebSocket {_printerId}: Receive loop failed");
        }
        finally
        {
            _socketState = "closed";
            _isAuthenticated = false;
        }
    }

    /// <summary>
    /// Handles incoming WebSocket messages. Parses 'current' events containing printer status.
    /// </summary>
    private async Task HandleWebSocketMessageAsync(string message, CancellationToken ct)
    {
        try
        {
            // OctoPrint WebSocket messages are JSON objects
            using var doc = JsonDocument.Parse(message);
            var root = doc.RootElement;

            // Look for 'current' message which contains printer state
            if (root.TryGetProperty("current", out var currentObj))
            {
                var printerStatus = ParsePrinterStatus(currentObj);
                if (printerStatus != null)
                {
                    await BroadcastStatusAsync(printerStatus, ct);
                }
            }
            else if (root.TryGetProperty("reauthRequired", out _))
            {
                _logger.LogWarning($"OctoPrint WebSocket {_printerId}: Re-authentication required");
                _isAuthenticated = false;
                await SendAuthMessageAsync(ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"OctoPrint WebSocket {_printerId}: Failed to parse message");
        }
    }

    /// <summary>
    /// Broadcasts printer status update via SignalR hub.
    /// </summary>
    private async Task BroadcastStatusAsync(OctoPrintStatusData status, CancellationToken ct)
    {
        try
        {
            // Create cache update (PrinterStatusDto - no HomedAxes)
            var cacheUpdate = new PrinterStatusDto(
                Id: _printerId,
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
                SpoolInfo: null
            );

            // Update cache before broadcasting to clients
            _statusCacheWriter.UpdateStatus(cacheUpdate);

            // Create SignalR update (PrinterStatusUpdate - includes HomedAxes)
            var signalRUpdate = new PrinterStatusUpdate(
                Id: _printerId,
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
                SpoolInfo: null
            );

            await _hub.Clients.All.SendAsync("printerupdated", signalRUpdate, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"OctoPrint WebSocket {_printerId}: Failed to broadcast status");
        }
    }

    /// <summary>
    /// Attempts HTTP polling fallback when WebSocket is unavailable.
    /// Called from polling service if WebSocket is not connected.
    /// </summary>
    public async Task<OctoPrintStatusData?> TryHttpPollingFallbackAsync(CancellationToken ct)
    {
        // Only poll if enough time has passed since last poll
        if (DateTime.UtcNow - _lastHttpPoll < _pollingInterval)
        {
            return null;
        }

        try
        {
            if (string.IsNullOrEmpty(_printer.ApiKey))
            {
                _logger.LogWarning($"OctoPrint HTTP Fallback {_printerId}: No API key configured");
                _apiState = "authFail";
                return null;
            }

            OctoPrintPrinterState? printerState = await _octoPrintClient.GetPrinterStateAsync(_printer.ServerUrl, _printer.ApiKey);
            OctoPrintJobStatus? jobStatus = await _octoPrintClient.GetJobStatusAsync(_printer.ServerUrl, _printer.ApiKey);

            if (printerState == null || jobStatus == null)
            {
                _consecutiveFailures++;
                _logger.LogWarning($"OctoPrint HTTP Fallback {_printerId}: Failed to retrieve status (attempt {_consecutiveFailures})");
                return null;
            }

            bool isOnline = printerState.Operational;
            string? currentState = isOnline ? printerState.State : null;
            double? currentProgress = isOnline ? jobStatus.Progress : null;

            _lastHttpPoll = DateTime.UtcNow;
            _apiState = "responding";
            _consecutiveFailures = 0;

            _logger.LogDebug(
                $"OctoPrint HTTP Fallback {_printerId}: Got status - Online={isOnline}, State={currentState}, " +
                $"Progress={currentProgress}, JobName={jobStatus.Filename}");

            return new OctoPrintStatusData
            {
                IsOnline = isOnline,
                Operational = printerState.Operational,
                State = currentState,
                Progress = currentProgress,
                JobName = jobStatus.Filename,
                X = null,
                Y = null,
                Z = null,
                HotendTemp = null,
                BedTemp = null,
                HotendTarget = null,
                BedTarget = null,
                ThumbnailUrl = null,
                CameraStreamUrl = null
            };
        }
        catch (Exception ex)
        {
            _consecutiveFailures++;
            string errorType = ex is HttpRequestException httpEx
                ? httpEx.StatusCode == System.Net.HttpStatusCode.Unauthorized ? "authFail" : "noResponse"
                : "noResponse";

            _apiState = errorType;
            _logger.LogDebug(ex, $"OctoPrint HTTP Fallback {_printerId}: Failed (apiState={errorType})");

            return null;
        }
    }

    /// <summary>
    /// Acquires OctoPrint session token via HTTP for WebSocket authentication.
    /// </summary>
    private async Task AcquireSessionTokenAsync(CancellationToken ct)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"{_printer.ServerUrl}/api/login");
            request.Headers.Add("X-Api-Key", _printer.ApiKey);
            request.Content = new StringContent("{\"passive\":true}", Encoding.UTF8, "application/json");

            var response = await _octoPrintClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            string content = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(content);
            if (doc.RootElement.TryGetProperty("session", out var sessionProp))
            {
                _sessionToken = sessionProp.GetString();
                _logger.LogDebug($"OctoPrint WebSocket {_printerId}: Acquired session token");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"OctoPrint WebSocket {_printerId}: Failed to acquire session token");
            _apiState = "authFail";
            throw;
        }
    }

    /// <summary>
    /// Sends authentication message to OctoPrint WebSocket.
    /// </summary>
    private async Task SendAuthMessageAsync(CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrEmpty(_sessionToken))
            {
                throw new InvalidOperationException("Session token not acquired");
            }

            // OctoPrint WebSocket auth format: {"auth":"username:sessiontoken"}
            string authMessage = $"{{\"auth\":\"api:{_sessionToken}\"}}";
            byte[] buffer = Encoding.UTF8.GetBytes(authMessage);

            if (_webSocket?.State == WebSocketState.Open)
            {
                await _webSocket.SendAsync(
                    new ArraySegment<byte>(buffer),
                    WebSocketMessageType.Text,
                    true,
                    ct);

                _logger.LogDebug($"OctoPrint WebSocket {_printerId}: Sent auth message");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"OctoPrint WebSocket {_printerId}: Failed to send auth message");
            _apiState = "authFail";
            throw;
        }
    }

    /// <summary>
    /// Converts HTTP URL to WebSocket URL (/sockjs/websocket endpoint).
    /// </summary>
    private static string ConvertToWebSocketUrl(string httpUrl)
    {
        var uri = new Uri(httpUrl);
        string scheme = uri.Scheme == "https" ? "wss" : "ws";
        return $"{scheme}://{uri.Host}:{uri.Port}/sockjs/websocket";
    }

    /// <summary>
    /// Parses OctoPrint WebSocket 'current' message to extract status data.
    /// </summary>
    private static OctoPrintStatusData? ParsePrinterStatus(JsonElement currentObj)
    {
        try
        {
            var stateObj = currentObj.GetProperty("state");
            var flags = stateObj.GetProperty("flags");
            bool operational = flags.GetProperty("operational").GetBoolean();
            bool printing = flags.GetProperty("printing").GetBoolean();
            bool paused = flags.GetProperty("paused").GetBoolean();

            string state = printing ? "Printing" : paused ? "Paused" : operational ? "Idle" : "Offline";

            double? progress = null;
            if (currentObj.TryGetProperty("progress", out var progObj) &&
                progObj.TryGetProperty("completion", out var completion) &&
                completion.ValueKind != JsonValueKind.Null)
            {
                progress = completion.GetDouble() * 100.0;
            }

            string? jobName = null;
            if (currentObj.TryGetProperty("job", out var jobObj) &&
                jobObj.TryGetProperty("file", out var fileObj) &&
                fileObj.TryGetProperty("name", out var name) &&
                name.ValueKind != JsonValueKind.Null)
            {
                jobName = name.GetString();
            }

            double? z = null;
            if (currentObj.TryGetProperty("currentZ", out var zProp) && zProp.ValueKind != JsonValueKind.Null)
            {
                z = zProp.GetDouble();
            }

            double? hotendTemp = null, bedTemp = null, hotendTarget = null, bedTarget = null;
            if (currentObj.TryGetProperty("temperature", out var tempProp))
            {
                if (tempProp.TryGetProperty("tool0", out var tool0) && tool0.ValueKind != JsonValueKind.Null)
                {
                    if (tool0.TryGetProperty("actual", out var actual))
                        hotendTemp = actual.GetDouble();
                    if (tool0.TryGetProperty("target", out var target))
                        hotendTarget = target.GetDouble();
                }
                if (tempProp.TryGetProperty("bed", out var bed) && bed.ValueKind != JsonValueKind.Null)
                {
                    if (bed.TryGetProperty("actual", out var actual))
                        bedTemp = actual.GetDouble();
                    if (bed.TryGetProperty("target", out var target))
                        bedTarget = target.GetDouble();
                }
            }

            return new OctoPrintStatusData
            {
                IsOnline = operational,
                Operational = operational,
                State = state,
                Progress = progress,
                JobName = jobName,
                Z = z,
                HotendTemp = hotendTemp,
                BedTemp = bedTemp,
                HotendTarget = hotendTarget,
                BedTarget = bedTarget
            };
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Failed to parse OctoPrint WebSocket status", ex);
        }
    }

    /// <summary>
    /// Parses OctoPrint /api/printer HTTP response (used for fallback).
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        _cts?.Cancel();
        _cts?.Dispose();
        _receiveLoopTask?.Wait(TimeSpan.FromSeconds(5));
        _webSocket?.Dispose();
    }

    /// <summary>
    /// Data class for parsed printer state from HTTP fallback.
    /// Used for JSON deserialization - properties must have setters.
    /// </summary>
#pragma warning disable S3459, S1144 // Suppress "unassigned property" and "unused setter" warnings for DTO
    private sealed class PrinterStateData
    {
        public bool IsOnline { get; set; }
        public bool Operational { get; set; }
        public string? State { get; set; }
        public double? X { get; set; }
        public double? Y { get; set; }
        public double? Z { get; set; }
        public double? HotendTemp { get; set; }
        public double? BedTemp { get; set; }
        public double? HotendTarget { get; set; }
        public double? BedTarget { get; set; }
        public string? ThumbnailUrl { get; set; }
        public string? CameraStreamUrl { get; set; }
    }
#pragma warning restore S3459, S1144
}

