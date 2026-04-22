#pragma warning disable S1144 // Unused classes and properties reserved for future use

using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.SignalR;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace Farm.Backend.Plugin.OctoPrint;

/// <summary>
/// WebSocket adapter for OctoPrint real-time status updates.
/// Implements dual-layer architecture: WebSocket primary + HTTP polling fallback.
/// Maintains persistent WebSocket connection to /sockjs/websocket endpoint,
/// with automatic fallback to HTTP polling every 10 seconds if WebSocket fails.
/// </summary>
public sealed class OctoPrintWebSocketAdapter(
    Guid printerId,
    Printer printer,
    ILogger logger,
    IOctoPrintClient octoPrintClient,
    IHubContext<PrinterHub> hub,
    IPrinterStatusCacheWriter statusCacheWriter) : IDisposable
{
    private readonly ILogger _logger = logger;
    private readonly IOctoPrintClient _octoPrintClient = octoPrintClient;
    private readonly IHubContext<PrinterHub> _hub = hub;
    private readonly IPrinterStatusCacheWriter _statusCacheWriter = statusCacheWriter;
    private readonly Guid _printerId = printerId;
    private readonly Printer _printer = printer;
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

    /// <summary>
    /// Establishes WebSocket connection and starts receive loop.
    /// Handles authentication and fallback to HTTP polling on failure.
    /// </summary>
    /// <param name="ct">Cancellation token for the async operation.</param>
    public async Task ConnectAsync(CancellationToken ct)
    {
        try
        {
            _logger.LogInformation("OctoPrint WebSocket {PrinterId}: Attempting connection to {ServerUrl}", _printerId, _printer.ServerUrl);
            _socketState = "connecting";

            // Get session token first via HTTP
            await AcquireSessionTokenAsync(ct);

            // Convert HTTP URL to WebSocket URL
            string wsUrl = ConvertToWebSocketUrl(_printer.ServerUrl);
            _logger.LogDebug("OctoPrint WebSocket {PrinterId}: Connecting to {WsUrl}", _printerId, wsUrl);

            // Create WebSocket connection
            _webSocket = new ClientWebSocket();
            _webSocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);

            await _webSocket.ConnectAsync(new Uri(wsUrl), ct);
            _logger.LogInformation("OctoPrint WebSocket {PrinterId}: Connected", _printerId);
            _socketState = "connecting";

            // Authenticate
            await SendAuthMessageAsync(ct);
            _isAuthenticated = true;
            _socketState = "authenticated";
            _apiState = "responding";
            _consecutiveFailures = 0;

            _logger.LogInformation("OctoPrint WebSocket {PrinterId}: Authenticated", _printerId);

            // Start receive loop
            _receiveLoopTask = Task.Run(() => ReceiveLoopAsync(_cts.Token), _cts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OctoPrint WebSocket {PrinterId}: Connection failed, will use HTTP polling fallback", _printerId);
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
                        _logger.LogInformation("OctoPrint WebSocket {PrinterId}: Received close frame", _printerId);
                        _socketState = "closed";
                        break;
                    }

                    if (result.Count > 0)
                    {
                        string message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        _logger.LogDebug("OctoPrint WebSocket {PrinterId}: Received message: {Value1}...", _printerId, message.Substring(0, Math.Min(100, message.Length)));

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
                    _logger.LogError(ex, "OctoPrint WebSocket {PrinterId}: Error receiving message", _printerId);
                    _consecutiveFailures++;
                    _apiState = "noResponse";

                    if (_consecutiveFailures >= 3)
                    {
                        _logger.LogWarning("OctoPrint WebSocket {PrinterId}: Too many failures, switching to HTTP polling fallback", _printerId);
                        _socketState = "error";
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogInformation("OctoPrint WebSocket {PrinterId}: Receive loop cancelled", _printerId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OctoPrint WebSocket {PrinterId}: Receive loop failed", _printerId);
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
    /// <param name="message">The raw WebSocket message to handle.</param>
    /// <param name="ct">Cancellation token for the async operation.</param>
    private async Task HandleWebSocketMessageAsync(string message, CancellationToken ct)
    {
        try
        {
            // OctoPrint WebSocket messages are JSON objects
            using var doc = JsonDocument.Parse(message);
            JsonElement root = doc.RootElement;

            // Look for 'current' message which contains printer state
            if (root.TryGetProperty("current", out JsonElement currentObj))
            {
                OctoPrintStatusData? printerStatus = ParsePrinterStatus(currentObj);
                if (printerStatus != null)
                {
                    await BroadcastStatusAsync(printerStatus, ct);
                }
            }
            else if (root.TryGetProperty("reauthRequired", out _))
            {
                _logger.LogWarning("OctoPrint WebSocket {PrinterId}: Re-authentication required", _printerId);
                _isAuthenticated = false;
                await SendAuthMessageAsync(ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OctoPrint WebSocket {PrinterId}: Failed to parse message", _printerId);
        }
    }

    /// <summary>
    /// Broadcasts printer status update via SignalR hub.
    /// </summary>
    /// <param name="status">The OctoPrint status data to broadcast.</param>
    /// <param name="ct">Cancellation token for the async operation.</param>
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
                SpoolInfo: null,
                PrintTimeLeftSeconds: status.PrintTimeLeftSeconds);

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
                SpoolInfo: null,
                FileName: PrinterStatusDto.ExtractFileName(status.JobName));

            await _hub.Clients.All.SendAsync("printerupdated", signalRUpdate, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OctoPrint WebSocket {PrinterId}: Failed to broadcast status", _printerId);
        }
    }

    /// <summary>
    /// Attempts HTTP polling fallback when WebSocket is unavailable.
    /// Called from polling service if WebSocket is not connected.
    /// </summary>
    /// <param name="ct">Cancellation token for the async operation.</param>
    public async Task<OctoPrintStatusData?> TryHttpPollingFallbackAsync(CancellationToken ct)
    {
        // Only poll if enough time has passed since last poll
        if (DateTime.UtcNow - _lastHttpPoll < _pollingInterval)
        {
            return null;
        }

        try
        {
            if (_printer.Credential == null || !_printer.Credential.HasApiKey)
            {
                _logger.LogWarning("OctoPrint HTTP Fallback {PrinterId}: No API key configured", _printerId);
                _apiState = "authFail";
                return null;
            }

            OctoPrintPrinterState? printerState = await _octoPrintClient.GetPrinterStateAsync(_printer.ServerUrl, _printer.Credential);
            OctoPrintJobStatus? jobStatus = await _octoPrintClient.GetJobStatusAsync(_printer.ServerUrl, _printer.Credential);

            if (printerState == null || jobStatus == null)
            {
                _consecutiveFailures++;
                _logger.LogWarning("OctoPrint HTTP Fallback {PrinterId}: Failed to retrieve status (attempt {ConsecutiveFailures})", _printerId, _consecutiveFailures);
                return null;
            }

            bool isOnline = printerState.Operational;
            string? currentState = isOnline ? printerState.State : null;
            double? currentProgress = isOnline ? jobStatus.Progress : null;

            _lastHttpPoll = DateTime.UtcNow;
            _apiState = "responding";
            _consecutiveFailures = 0;

            _logger.LogDebug(
                "OctoPrint HTTP Fallback {PrinterId}: Got status - Online={IsOnline}, State={CurrentState}, Progress={Progress}, JobName={JobName}",
                _printerId, isOnline, currentState, currentProgress, jobStatus.Filename);

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
                CameraStreamUrl = null,
                PrintTimeLeftSeconds = jobStatus.PrintTimeLeft
            };
        }
        catch (Exception ex)
        {
            _consecutiveFailures++;
            string errorType = ex is HttpRequestException httpEx
                ? httpEx.StatusCode == System.Net.HttpStatusCode.Unauthorized ? "authFail" : "noResponse"
                : "noResponse";

            _apiState = errorType;
            _logger.LogDebug(ex, "OctoPrint HTTP Fallback {PrinterId}: Failed (apiState={ErrorType})", _printerId, errorType);

            return null;
        }
    }

    /// <summary>
    /// Acquires OctoPrint session token via HTTP for WebSocket authentication.
    /// </summary>
    /// <param name="ct">Cancellation token for the async operation.</param>
    private async Task AcquireSessionTokenAsync(CancellationToken ct)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"{_printer.ServerUrl}/api/login");
            request.Headers.Add("X-Api-Key", _printer.Credential?.ApiKey);
            request.Content = new StringContent("{\"passive\":true}", Encoding.UTF8, "application/json");

            HttpResponseMessage response = await _octoPrintClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            string content = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(content);
            if (doc.RootElement.TryGetProperty("session", out JsonElement sessionProp))
            {
                _sessionToken = sessionProp.GetString();
                _logger.LogDebug("OctoPrint WebSocket {PrinterId}: Acquired session token", _printerId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OctoPrint WebSocket {PrinterId}: Failed to acquire session token", _printerId);
            _apiState = "authFail";
            throw;
        }
    }

    /// <summary>
    /// Sends authentication message to OctoPrint WebSocket.
    /// </summary>
    /// <param name="ct">Cancellation token for the async operation.</param>
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

                _logger.LogDebug("OctoPrint WebSocket {PrinterId}: Sent auth message", _printerId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OctoPrint WebSocket {PrinterId}: Failed to send auth message", _printerId);
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
            JsonElement stateObj = currentObj.GetProperty("state");
            JsonElement flags = stateObj.GetProperty("flags");
            bool operational = flags.GetProperty("operational").GetBoolean();
            bool printing = flags.GetProperty("printing").GetBoolean();
            bool paused = flags.GetProperty("paused").GetBoolean();

            string state = printing ? "Printing" : paused ? "Paused" : operational ? "Idle" : "Offline";

            double? progress = null;
            if (currentObj.TryGetProperty("progress", out JsonElement progObj) &&
                progObj.TryGetProperty("completion", out JsonElement completion) &&
                completion.ValueKind != JsonValueKind.Null)
            {
                progress = completion.GetDouble() * 100.0;
            }

            string? jobName = null;
            if (currentObj.TryGetProperty("job", out JsonElement jobObj) &&
                jobObj.TryGetProperty("file", out JsonElement fileObj) &&
                fileObj.TryGetProperty("name", out JsonElement name) &&
                name.ValueKind != JsonValueKind.Null)
            {
                jobName = name.GetString();
            }

            double? z = null;
            if (currentObj.TryGetProperty("currentZ", out JsonElement zProp) && zProp.ValueKind != JsonValueKind.Null)
            {
                z = zProp.GetDouble();
            }

            double? hotendTemp = null, bedTemp = null, hotendTarget = null, bedTarget = null;
            if (currentObj.TryGetProperty("temperature", out JsonElement tempProp))
            {
                if (tempProp.TryGetProperty("tool0", out JsonElement tool0) && tool0.ValueKind != JsonValueKind.Null)
                {
                    if (tool0.TryGetProperty("actual", out JsonElement actual))
                    {
                        hotendTemp = actual.GetDouble();
                    }

                    if (tool0.TryGetProperty("target", out JsonElement target))
                    {
                        hotendTarget = target.GetDouble();
                    }
                }

                if (tempProp.TryGetProperty("bed", out JsonElement bed) && bed.ValueKind != JsonValueKind.Null)
                {
                    if (bed.TryGetProperty("actual", out JsonElement actual))
                    {
                        bedTemp = actual.GetDouble();
                    }

                    if (bed.TryGetProperty("target", out JsonElement target))
                    {
                        bedTarget = target.GetDouble();
                    }
                }
            }

            double? printTimeLeft = null;
            if (currentObj.TryGetProperty("progress", out JsonElement progObj2) &&
                progObj2.TryGetProperty("printTimeLeft", out JsonElement ptl) &&
                ptl.ValueKind != JsonValueKind.Null)
            {
                printTimeLeft = ptl.GetDouble();
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
                BedTarget = bedTarget,
                PrintTimeLeftSeconds = printTimeLeft
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
        if (_disposed)
        {
            return;
        }

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
