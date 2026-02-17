#pragma warning disable S1006, CA2213, S1939 // Default parameters, HttpClient disposal, and interface inheritance are intentional

using System.Buffers;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Settings;
using Farm.Infrastructure.Telemetry;
using Microsoft.Extensions.Logging;

namespace Farm.Backend.Plugin.Sdcp;

#pragma warning disable CS1066 // Default value for optional parameter not enforced for interface members

/// <summary>
/// SDCP (Smart Device Control Protocol) Client for communicating with printers using SDCP 3.0 over WebSockets.
/// Designed primarily for the Elegoo Centauri Carbon printer but may work with other SDCP-compatible printers.
///
/// Protocol Documentation: https://github.com/WalkerFrederick/sdcp-centauri-carbon
///
/// Features:
/// - Real-time status monitoring (temperatures, coordinates, print progress)
/// - Print job control (start, pause, resume, cancel)
/// - WebSocket-based communication over MQTT-like message structure
/// - Automatic connection management and timeout handling
/// </summary>

// SDCP message structures based on https://github.com/WalkerFrederick/sdcp-centauri-carbon
public record SdcpMessage<T>(
    string Id,
    SdcpData<T> Data,
    string Topic);

public record SdcpData<T>(
    int Cmd,
    T Data,
    string RequestID,
    string MainboardID,
    long TimeStamp,
    int From = 1);

// SDCP status structures that match the exact JSON from the protocol
public class SdcpStatusResponse
{
    public SdcpStatus? Status { get; set; }

    public string? MainboardID { get; set; }

    public long TimeStamp { get; set; }

    public string? Topic { get; set; }
}

public class SdcpStatus
{
    public int[]? CurrentStatus { get; set; }

    public int TimeLapseStatus { get; set; }

    public int PlatFormType { get; set; }

    public double TempOfHotbed { get; set; }

    public double TempOfNozzle { get; set; }

    public double TempOfBox { get; set; }

    public double TempTargetHotbed { get; set; }

    public double TempTargetNozzle { get; set; }

    public double TempTargetBox { get; set; }

    public string? CurrenCoord { get; set; }

    public SdcpFanSpeed? CurrentFanSpeed { get; set; }

    public double ZOffset { get; set; }

    public SdcpLightStatus? LightStatus { get; set; }

    public SdcpPrintInfo? PrintInfo { get; set; }
}

public class SdcpFanSpeed
{
    public int ModelFan { get; set; }

    public int AuxiliaryFan { get; set; }

    public int BoxFan { get; set; }
}

public class SdcpLightStatus
{
    public int SecondLight { get; set; }

    public int[]? RgbLight { get; set; }
}

public class SdcpPrintInfo
{
    public int Status { get; set; }

    public int CurrentLayer { get; set; }

    public int TotalLayer { get; set; }

    public long CurrentTicks { get; set; }

    public long TotalTicks { get; set; }

    public string? Filename { get; set; }

    public string? TaskId { get; set; }

    public int PrintSpeedPct { get; set; }

    public double Progress { get; set; }
}

public class SdcpAckResponse
{
    public string? Id { get; set; }

    public SdcpAckData? Data { get; set; }

    public string? Topic { get; set; }
}

public class SdcpAckData
{
    public int Cmd { get; set; }

    public SdcpAckResult? Data { get; set; }

    public string? RequestID { get; set; }

    public string? MainboardID { get; set; }

    public long TimeStamp { get; set; }
}

public class SdcpAckResult
{
    public int Ack { get; set; }
}

/// <summary>
/// SDCP Cmd 258 (GetFileList) response envelope.
/// The printer responds with Cmd 192 containing a FileList array.
/// </summary>
public class SdcpFileListAckResponse
{
    public string? Id { get; set; }

    public SdcpFileListAckData? Data { get; set; }

    public string? Topic { get; set; }
}

public class SdcpFileListAckData
{
    public int Cmd { get; set; }

    public SdcpFileListResult? Data { get; set; }

    public string? RequestID { get; set; }

    public string? MainboardID { get; set; }

    public long TimeStamp { get; set; }
}

/// <summary>
/// Contains the acknowledgement status and list of files returned by the printer.
/// </summary>
public class SdcpFileListResult
{
    public int Ack { get; set; }

    public List<SdcpFileEntry>? FileList { get; set; }
}

/// <summary>
/// Represents a single file or folder entry from the SDCP file list response.
/// </summary>
public class SdcpFileEntry
{
    /// <summary>Full path on the printer (e.g., "/local/model.gcode").</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Used storage space in bytes.</summary>
    public long UsedSize { get; set; }

    /// <summary>Total storage space in bytes.</summary>
    public long TotalSize { get; set; }

    /// <summary>Storage type: 0 = Internal, 1 = External (USB).</summary>
    public int StorageType { get; set; }

    /// <summary>Entry type: 0 = Folder, 1 = File.</summary>
    public int Type { get; set; }
}

// ========== SDCP History Response DTOs (Cmd 320 / 321) ==========

/// <summary>
/// Response envelope for Cmd 320 (GetHistoryIds) — returns a list of task IDs.
/// </summary>
public class SdcpHistoryIdsAckResponse
{
    public string? Id { get; set; }

    public SdcpHistoryIdsAckData? Data { get; set; }

    public string? Topic { get; set; }
}

public class SdcpHistoryIdsAckData
{
    public int Cmd { get; set; }

    public SdcpHistoryIdsResult? Data { get; set; }

    public string? RequestID { get; set; }

    public string? MainboardID { get; set; }

    public long TimeStamp { get; set; }
}

public class SdcpHistoryIdsResult
{
    public int Ack { get; set; }

    /// <summary>Ordered list of task IDs (UUIDs). Spec field name: HistoryData.</summary>
    public List<string>? HistoryData { get; set; }
}

/// <summary>
/// Response envelope for Cmd 321 (GetHistoryDetail) — returns details for one task.
/// </summary>
public class SdcpHistoryDetailAckResponse
{
    public string? Id { get; set; }

    public SdcpHistoryDetailAckData? Data { get; set; }

    public string? Topic { get; set; }
}

public class SdcpHistoryDetailAckData
{
    public int Cmd { get; set; }

    public SdcpHistoryDetailResult? Data { get; set; }

    public string? RequestID { get; set; }

    public string? MainboardID { get; set; }

    public long TimeStamp { get; set; }
}

/// <summary>
/// Inner data wrapper for Cmd 321 response containing Ack and the detail list.
/// </summary>
public class SdcpHistoryDetailResult
{
    public int Ack { get; set; }

    /// <summary>Array of history detail records. Spec field: HistoryDetailList.</summary>
    public List<SdcpHistoryDetail>? HistoryDetailList { get; set; }
}

/// <summary>
/// SDCP history detail for a single print job per SDCP V3.0.0 spec.
/// Unknown fields are tolerated via case-insensitive deserialization.
/// </summary>
public class SdcpHistoryDetail
{
    /// <summary>The unique task identifier.</summary>
    public string? TaskId { get; set; }

    /// <summary>The task/file name. Spec field: TaskName.</summary>
    public string? TaskName { get; set; }

    /// <summary>Start time as Unix timestamp (seconds). Spec field: BeginTime.</summary>
    public double BeginTime { get; set; }

    /// <summary>End time as Unix timestamp (seconds), or 0 if not finished.</summary>
    public double EndTime { get; set; }

    /// <summary>Task status per SDCP spec: 0 = Other, 1 = Completed, 2 = Exceptional, 3 = Stopped.</summary>
    public int TaskStatus { get; set; }

    /// <summary>Thumbnail address (URL or path).</summary>
    public string? Thumbnail { get; set; }

    /// <summary>Number of layers already printed.</summary>
    public int AlreadyPrintLayer { get; set; }

    /// <summary>Error status reason code (0 = OK).</summary>
    public int ErrorStatusReason { get; set; }
}

/// <summary>
/// SDCP Attributes response envelope.
/// Published on the sdcp/attributes/${MainboardID} topic when attribute information changes
/// or upon receiving Cmd 1 (Request for attribute message).
/// </summary>
public class SdcpAttributesResponse
{
    public SdcpAttributes? Attributes { get; set; }

    public string? MainboardID { get; set; }

    public long TimeStamp { get; set; }

    public string? Topic { get; set; }
}

/// <summary>
/// SDCP machine attribute information containing printer identification, firmware version,
/// capabilities, and hardware status per SDCP V3.0.0 spec.
/// </summary>
public class SdcpAttributes
{
    /// <summary>Machine Name (user-configurable printer name).</summary>
    public string? Name { get; set; }

    /// <summary>Machine Model (hardware model identifier).</summary>
    public string? MachineName { get; set; }

    /// <summary>Brand Name (manufacturer brand, e.g., "CBD", "Elegoo").</summary>
    public string? BrandName { get; set; }

    /// <summary>Protocol Version (e.g., "V3.0.0").</summary>
    public string? ProtocolVersion { get; set; }

    /// <summary>Firmware Version (e.g., "V1.0.0").</summary>
    public string? FirmwareVersion { get; set; }

    /// <summary>Screen resolution (e.g., "7680x4320").</summary>
    public string? Resolution { get; set; }

    /// <summary>Maximum printing dimensions in XYZ, in millimeters (e.g., "210x140x100").</summary>
    public string? XYZsize { get; set; }

    /// <summary>Motherboard IP Address.</summary>
    public string? MainboardIP { get; set; }

    /// <summary>Motherboard ID (16-bit identifier).</summary>
    public string? MainboardID { get; set; }
}

public sealed class SdcpClient(HttpClient httpClient, IUnifiedLoggingService logger, BackendTimeoutSettings timeouts) : PrinterClientBase, ISdcpClient,
    ISupportsFileList,
    ISupportsFileUpload,
    ISupportsStartPrint,
    ISupportsControlOperations,
    ISupportsCamera,
    ISupportsHistory,
    ISupportsFileDelete,
    ISupportsPrinterInformation,
    ISupportsStatus,
    ISupportsCompositeStatus
{
    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    private readonly IUnifiedLoggingService _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly BackendTimeoutSettings _timeouts = timeouts ?? throw new ArgumentNullException(nameof(timeouts));

    private const string SdcpLogCategory = "SDCP";
    private const int SdcpWebSocketPort = 3030;

    /// <summary>
    /// Interval at which the client sends WebSocket ping frames to the printer.
    /// Keeps long-lived connections (e.g., multi-step history retrieval) alive
    /// and allows the runtime to detect unresponsive peers.
    /// </summary>
    private static readonly TimeSpan WebSocketKeepAliveInterval = TimeSpan.FromSeconds(15);

    private static class SdcpCommandIds
    {
        public const int GetStatus = 0;
        public const int GetAttributes = 1;
        public const int StartPrint = 128;
        public const int PausePrint = 129;
        public const int CancelPrint = 130;
        public const int ResumePrint = 131;
        public const int GetFileList = 258;
        public const int DeleteFile = 259;
        public const int GetHistoryIds = 320;
        public const int GetHistoryDetail = 321;
        public const int SetCameraEnabled = 386;
    }

    private void LogSdcp(LogLevel level, string message, string? correlationId = null, object? metadata = null, Exception? exception = null)
        => _logger.LogWithContext(level, SdcpLogCategory, message, correlationId, metadata, context: null, exception);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = null, // Keep original property names for SDCP
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Static cache of MainboardID by normalized printer host.
    /// Populated from status/ack responses; used in subsequent requests for protocol correctness.
    /// </summary>
    private static readonly ConcurrentDictionary<string, string> MainboardIdCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns the cached MainboardID for the given base URL, or empty string if not yet known.
    /// </summary>
    private static string GetCachedMainboardId(string baseUrl)
    {
        try
        {
            string host = new Uri(NormalizeBaseUrl(baseUrl, 80)).Host;
            return MainboardIdCache.TryGetValue(host, out string? id) ? id : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Caches the MainboardID from a response if it is non-empty.
    /// </summary>
    private static void CacheMainboardId(string baseUrl, string? mainboardId)
    {
        if (string.IsNullOrWhiteSpace(mainboardId))
        {
            return;
        }

        try
        {
            string host = new Uri(NormalizeBaseUrl(baseUrl, 80)).Host;
            MainboardIdCache[host] = mainboardId;
        }
        catch
        {
            // Best-effort caching; do not let URI parse failures affect operations
        }
    }

    // SDCP status codes based on documentation
    private static readonly Dictionary<int, string> StatusCodeMap = new()
    {
        { 0, "idle" },
        { 5, "idle" },
        { 8, "preparing" },
        { 9, "starting" },
        { 10, "paused" },
        { 13, "printing" },
        { 20, "printing" }
    };

    private async Task<string?> ReceiveTextMessageAsync(ClientWebSocket ws, string operation, string correlationId, CancellationToken ct)
    {
        byte[] rented = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            ArrayBufferWriter<byte> writer = new();

            long startedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            LogSdcp(LogLevel.Debug, "SDCP WS receive started", correlationId, new { operation });

            while (true)
            {
                WebSocketReceiveResult result = await ws.ReceiveAsync(rented, ct);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    LogSdcp(
                        LogLevel.Debug,
                        "SDCP WS received close frame",
                        correlationId,
                        new { operation, closeStatus = ws.CloseStatus?.ToString(), closeStatusDescription = ws.CloseStatusDescription });
                    return null;
                }

                if (result.MessageType != WebSocketMessageType.Text)
                {
                    // Skip non-text frames (e.g., binary heartbeat/ping frames from the printer)
                    // without aborting the receive loop. Protocol-level WebSocket pings are
                    // handled transparently by .NET's ClientWebSocket; application-level
                    // binary frames should be silently consumed.
                    if (result.EndOfMessage)
                    {
                        LogSdcp(
                            LogLevel.Debug,
                            "SDCP WS skipping non-text frame",
                            correlationId,
                            new { operation, messageType = result.MessageType.ToString() });
                    }

                    continue;
                }

                writer.Write(rented.AsSpan(0, result.Count));

                if (result.EndOfMessage)
                {
                    string text = Encoding.UTF8.GetString(writer.WrittenSpan);
                    long endedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    LogSdcp(
                        LogLevel.Debug,
                        "SDCP WS receive completed",
                        correlationId,
                        new { operation, bytes = writer.WrittenCount, durationMs = endedAt - startedAt });
                    return text;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static List<Uri> GetWebSocketCandidateUris(string baseUrl)
    {
        string normalizedUrl = NormalizeBaseUrl(baseUrl, 80);
        Uri uri = new(normalizedUrl);
        bool isSecure = string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);

        // SDCP v3.0.0 spec:
        // - WebSocket service on port 3030
        // - ws://${MainboardIP}:3030/websocket
        // Some printers (e.g., observed Centauri Carbon) expose SDCP via port 80 reverse proxy.
        bool isLocalHost =
            string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(uri.Host, "::1", StringComparison.OrdinalIgnoreCase);

        string wsScheme = isSecure ? "wss" : "ws";

        List<Uri> candidates = new();

        void AddPort(int port)
        {
            Uri candidate = new UriBuilder
            {
                Scheme = wsScheme,
                Host = uri.Host,
                Port = port,
                Path = "/websocket"
            }.Uri;

            if (!candidates.Contains(candidate))
            {
                candidates.Add(candidate);
            }
        }

        // For localhost tests, keep the explicit port so Kestrel can use an ephemeral port.
        if (isLocalHost && !uri.IsDefaultPort)
        {
            AddPort(uri.Port);
            return candidates;
        }

        // If the caller explicitly specifies a likely SDCP port, try it first.
        if (!uri.IsDefaultPort && (uri.Port == SdcpWebSocketPort || uri.Port == 80 || uri.Port == 443))
        {
            AddPort(uri.Port);
        }

        // Spec default.
        AddPort(SdcpWebSocketPort);

        // Common fallback when SDCP is exposed behind a web server.
        if (!isSecure)
        {
            AddPort(80);
        }
        else
        {
            AddPort(443);
        }

        return candidates;
    }

    private async Task<(ClientWebSocket ws, Uri wsUri)> ConnectWebSocketAsync(
        string baseUrl,
        string operation,
        string correlationId,
        CancellationToken ct)
    {
        List<Uri> candidates = GetWebSocketCandidateUris(baseUrl);
        Exception? lastException = null;

        foreach (Uri candidate in candidates)
        {
            ClientWebSocket ws = new();
            ws.Options.KeepAliveInterval = WebSocketKeepAliveInterval;
            try
            {
                LogSdcp(LogLevel.Debug, "SDCP WS connecting", correlationId, new { operation, wsUrl = candidate.ToString(), host = candidate.Host, port = candidate.Port });
                await ws.ConnectAsync(candidate, ct);
                LogSdcp(LogLevel.Debug, "SDCP WS connected", correlationId, new { operation, wsUrl = candidate.ToString(), host = candidate.Host, port = candidate.Port });
                return (ws, candidate);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                ws.Dispose();
                throw;
            }
            catch (Exception ex)
            {
                lastException = ex;
                LogSdcp(LogLevel.Debug, "SDCP WS connect attempt failed", correlationId, new { operation, wsUrl = candidate.ToString(), host = candidate.Host, port = candidate.Port }, ex);
                ws.Dispose();
            }
        }

        throw new InvalidOperationException("SDCP WS connection failed for all candidates.", lastException);
    }

    public async Task<bool> TestConnectionAsync(string baseUrl, CancellationToken ct = default)
    {
        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeouts.CommandTimeout);

            string requestId = Guid.NewGuid().ToString("N");
            var (ws, wsUri) = await ConnectWebSocketAsync(baseUrl, operation: "TestConnection", correlationId: requestId, cts.Token);
            using (ws)
            {
                string wsUrl = wsUri.ToString();

                SdcpMessage<object> statusRequest = new(
                    string.Empty,
                    new SdcpData<object>(
                        Cmd: SdcpCommandIds.GetStatus,
                        Data: new { },
                        RequestID: requestId,
                        MainboardID: GetCachedMainboardId(baseUrl),
                        TimeStamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
                    string.Empty);

                string json = JsonSerializer.Serialize(statusRequest, JsonOptions);
                byte[] bytes = Encoding.UTF8.GetBytes(json);
                await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cts.Token);

                LogSdcp(LogLevel.Debug, "SDCP WS test request sent", requestId, new { operation = "TestConnection", cmd = SdcpCommandIds.GetStatus, bytes = bytes.Length, wsUrl });

                string? responseJson = await ReceiveTextMessageAsync(ws, operation: "TestConnection", correlationId: requestId, cts.Token);

                try
                {
                    if (ws.State == WebSocketState.Open)
                    {
                        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, cts.Token);
                    }
                }
                catch (Exception ex)
                {
                    LogSdcp(LogLevel.Debug, "SDCP WS close failed", requestId, new { operation = "TestConnection", wsUrl }, ex);
                }

                bool responded = !string.IsNullOrWhiteSpace(responseJson);
                if (responded)
                {
                    try
                    {
                        var ack = JsonSerializer.Deserialize<SdcpAckResponse>(responseJson!, JsonOptions);
                        CacheMainboardId(baseUrl, ack?.Data?.MainboardID);
                    }
                    catch
                    {
                        // best-effort caching
                    }
                }

                LogSdcp(LogLevel.Debug, responded ? "SDCP test connection succeeded" : "SDCP test connection got no response", requestId, new { operation = "TestConnection", wsUrl });
                return responded;
            }
        }
        catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
        {
            LogSdcp(LogLevel.Debug, "SDCP test connection cancelled", correlationId: null, exception: ex);
            throw;
        }
        catch (Exception ex)
        {
            LogSdcp(LogLevel.Debug, "SDCP test connection failed", correlationId: null, new { operation = "TestConnection" }, ex);
            return false;
        }
    }

    public Task<bool> TestConnectionAsync(Uri baseUrl, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return TestConnectionAsync(baseUrl.ToString(), ct);
    }

    public async Task<PrinterStatus> GetStatusAsync(string baseUrl, CancellationToken ct = default)
    {
        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeouts.CommandTimeout);
            string requestId = Guid.NewGuid().ToString("N");

            var (ws, wsUri) = await ConnectWebSocketAsync(baseUrl, operation: "GetStatus", correlationId: requestId, cts.Token);
            using (ws)
            {
                string wsUrl = wsUri.ToString();

                // Send status request
                SdcpMessage<object> statusRequest = new(
                    string.Empty,
                    new SdcpData<object>(
                        Cmd: SdcpCommandIds.GetStatus,
                        Data: new { },
                        RequestID: requestId,
                        MainboardID: GetCachedMainboardId(baseUrl),
                        TimeStamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
                    string.Empty);

                string json = JsonSerializer.Serialize(statusRequest, JsonOptions);
                byte[] bytes = Encoding.UTF8.GetBytes(json);
                await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cts.Token);

                LogSdcp(LogLevel.Debug, "SDCP WS status request sent", requestId, new { operation = "GetStatus", cmd = SdcpCommandIds.GetStatus, bytes = bytes.Length, wsUrl });

                string? responseJson = await ReceiveTextMessageAsync(ws, operation: "GetStatus", correlationId: requestId, cts.Token);
                if (!string.IsNullOrWhiteSpace(responseJson))
                {
                    // Try to parse as status response
                    try
                    {
                        SdcpStatusResponse? statusResponse = JsonSerializer.Deserialize<SdcpStatusResponse>(responseJson, JsonOptions);
                        CacheMainboardId(baseUrl, statusResponse?.MainboardID);
                        if (statusResponse?.Status?.PrintInfo != null)
                        {
                            string state = StatusCodeMap.GetValueOrDefault(statusResponse.Status.PrintInfo.Status, "unknown");
                            LogSdcp(
                                LogLevel.Debug,
                                "SDCP status parsed",
                                requestId,
                                new
                                {
                                    operation = "GetStatus",
                                    statusCode = statusResponse.Status.PrintInfo.Status,
                                    state,
                                    hasPrintInfo = true
                                });
                            return new PrinterStatus(true, state);
                        }

                        LogSdcp(LogLevel.Debug, "SDCP status response did not include printInfo", requestId, new { operation = "GetStatus", hasStatus = statusResponse?.Status != null, wsUrl });
                    }
                    catch (Exception ex)
                    {
                        // Might be an ACK response, still indicates printer is online
                        LogSdcp(
                            LogLevel.Debug,
                            "SDCP status response parse failed; treating as online",
                            requestId,
                            new { operation = "GetStatus", responseLength = responseJson.Length, wsUrl },
                            ex);
                        return new PrinterStatus(true, "online");
                    }
                }
                else
                {
                    LogSdcp(LogLevel.Debug, "SDCP empty status response; treating as online", requestId, new { operation = "GetStatus", wsUrl });
                }

                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, cts.Token);
                return new PrinterStatus(true, "online");
            }
        }
        catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
        {
            LogSdcp(LogLevel.Debug, "SDCP status cancelled", correlationId: null, exception: ex);
            throw;
        }
        catch (Exception ex)
        {
            LogSdcp(LogLevel.Debug, "SDCP status failed", correlationId: null, new { operation = "GetStatus" }, ex);
            return new PrinterStatus(false, null);
        }
    }

    public Task<PrinterStatus> GetStatusAsync(Uri baseUrl, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return GetStatusAsync(baseUrl.ToString(), ct);
    }

    public async Task<PrinterJob> GetJobAsync(string baseUrl, CancellationToken ct = default)
    {
        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeouts.CommandTimeout);
            string requestId = Guid.NewGuid().ToString("N");

            var (ws, wsUri) = await ConnectWebSocketAsync(baseUrl, operation: "GetJob", correlationId: requestId, cts.Token);
            using (ws)
            {
                string wsUrl = wsUri.ToString();

                // Send status request to get print info
                SdcpMessage<object> statusRequest = new(
                    string.Empty,
                    new SdcpData<object>(
                        Cmd: SdcpCommandIds.GetStatus,
                        Data: new { },
                        RequestID: requestId,
                        MainboardID: GetCachedMainboardId(baseUrl),
                        TimeStamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
                    string.Empty);

                string json = JsonSerializer.Serialize(statusRequest, JsonOptions);
                byte[] bytes = Encoding.UTF8.GetBytes(json);
                await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cts.Token);

                LogSdcp(LogLevel.Debug, "SDCP WS job request sent", requestId, new { operation = "GetJob", cmd = SdcpCommandIds.GetStatus, bytes = bytes.Length, wsUrl });

                string? responseJson = await ReceiveTextMessageAsync(ws, operation: "GetJob", correlationId: requestId, cts.Token);
                if (!string.IsNullOrWhiteSpace(responseJson))
                {
                    SdcpStatusResponse? statusResponse = JsonSerializer.Deserialize<SdcpStatusResponse>(responseJson, JsonOptions);
                    CacheMainboardId(baseUrl, statusResponse?.MainboardID);

                    if (statusResponse?.Status?.PrintInfo != null)
                    {
                        SdcpPrintInfo printInfo = statusResponse.Status.PrintInfo;
                        string state = StatusCodeMap.GetValueOrDefault(printInfo.Status, "unknown");
                        double progress = printInfo.Progress / 100.0; // Convert percentage to decimal
                        string? jobName = string.IsNullOrWhiteSpace(printInfo.Filename) ? null :
                                     Path.GetFileName(printInfo.Filename);

                        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, cts.Token);
                        return new PrinterJob(state, progress, jobName, null);
                    }

                    LogSdcp(LogLevel.Debug, "SDCP job response did not include printInfo", requestId, new { operation = "GetJob", hasStatus = statusResponse?.Status != null, wsUrl });
                }

                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, cts.Token);
                return new PrinterJob(null, null, null, null);
            }
        }
        catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
        {
            LogSdcp(LogLevel.Debug, "SDCP job cancelled", correlationId: null, exception: ex);
            throw;
        }
        catch (Exception ex)
        {
            LogSdcp(LogLevel.Debug, "SDCP job failed", correlationId: null, new { operation = "GetJob" }, ex);
            return new PrinterJob(null, null, null, null);
        }
    }

    public Task<PrinterJob> GetJobAsync(Uri baseUrl, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return GetJobAsync(baseUrl.ToString(), ct);
    }

    public async Task<PrinterCompositeStatus> GetCompositeStatusAsync(string baseUrl, CancellationToken ct = default)
    {
        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeouts.CommandTimeout);
            string requestId = Guid.NewGuid().ToString("N");

            var (ws, wsUri) = await ConnectWebSocketAsync(baseUrl, operation: "GetCompositeStatus", correlationId: requestId, cts.Token);
            using (ws)
            {
                string wsUrl = wsUri.ToString();

                // Send status request
                SdcpMessage<object> statusRequest = new(
                    string.Empty,
                    new SdcpData<object>(
                        Cmd: SdcpCommandIds.GetStatus,
                        Data: new { },
                        RequestID: requestId,
                        MainboardID: GetCachedMainboardId(baseUrl),
                        TimeStamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
                    string.Empty);

                string json = JsonSerializer.Serialize(statusRequest, JsonOptions);
                byte[] bytes = Encoding.UTF8.GetBytes(json);
                await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cts.Token);

                LogSdcp(LogLevel.Debug, "SDCP WS composite status request sent", requestId, new { operation = "GetCompositeStatus", cmd = SdcpCommandIds.GetStatus, bytes = bytes.Length, wsUrl });

                string? responseJson = await ReceiveTextMessageAsync(ws, operation: "GetCompositeStatus", correlationId: requestId, cts.Token);
                if (!string.IsNullOrWhiteSpace(responseJson))
                {
                    SdcpStatusResponse? statusResponse;
                    try
                    {
                        statusResponse = JsonSerializer.Deserialize<SdcpStatusResponse>(responseJson, JsonOptions);
                        CacheMainboardId(baseUrl, statusResponse?.MainboardID);
                    }
                    catch (JsonException ex)
                    {
                        LogSdcp(
                            LogLevel.Debug,
                            "SDCP composite status parse failed; treating endpoint as online",
                            requestId,
                            new { operation = "GetCompositeStatus", responseLength = responseJson.Length, wsUrl },
                            ex);

                        try
                        {
                            if (ws.State == WebSocketState.Open)
                            {
                                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, cts.Token);
                            }
                        }
                        catch (Exception closeEx)
                        {
                            LogSdcp(LogLevel.Debug, "SDCP WS close failed", requestId, new { operation = "GetCompositeStatus" }, closeEx);
                        }

                        return new PrinterCompositeStatus(true, "online", null, null, null, null, null);
                    }

                    if (statusResponse?.Status != null)
                    {
                        SdcpStatus status = statusResponse.Status;
                        SdcpPrintInfo? printInfo = status.PrintInfo;

                        string state = printInfo != null ? StatusCodeMap.GetValueOrDefault(printInfo.Status, "unknown") : "online";
                        double? progress = printInfo?.Progress / 100.0;
                        string? jobName = string.IsNullOrWhiteSpace(printInfo?.Filename) ? null :
                                     Path.GetFileName(printInfo.Filename);

                        // Parse coordinates
                        double? x = null, y = null, z = null;
                        if (!string.IsNullOrWhiteSpace(status.CurrenCoord))
                        {
                            string[] coords = status.CurrenCoord.Split(',');
                            if (coords.Length >= 3)
                            {
                                if (double.TryParse(coords[0], out double xVal))
                                {
                                    x = xVal;
                                }

                                if (double.TryParse(coords[1], out double yVal))
                                {
                                    y = yVal;
                                }

                                if (double.TryParse(coords[2], out double zVal))
                                {
                                    z = zVal;
                                }
                            }
                        }

                        try
                        {
                            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, cts.Token);
                        }
                        catch (Exception ex)
                        {
                            LogSdcp(LogLevel.Debug, "SDCP WS close failed", requestId, new { operation = "GetCompositeStatus" }, ex);
                        }

                        // Get camera URLs if available
                        string? cameraStreamUrl = await GetCameraUrlAsync(baseUrl, ct);
                        string? cameraSnapshotUrl = await GetCameraSnapshotUrlAsync(baseUrl, ct);

                        return new PrinterCompositeStatus(
                            IsOnline: true,
                            State: state,
                            Progress: progress,
                            JobName: jobName,
                            ThumbnailUrl: null, // SDCP doesn't provide thumbnails directly
                            CameraStreamUrl: cameraStreamUrl,
                            CameraSnapshotUrl: cameraSnapshotUrl,
                            X: x,
                            Y: y,
                            Z: z,
                            HotendTemp: status.TempOfNozzle,
                            BedTemp: status.TempOfHotbed,
                            HotendTarget: status.TempTargetNozzle,
                            BedTarget: status.TempTargetHotbed);
                    }

                    LogSdcp(LogLevel.Debug, "SDCP composite status response did not include status payload", requestId, new { operation = "GetCompositeStatus" });
                }

                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, cts.Token);
                return new PrinterCompositeStatus(true, "online", null, null, null, null, null);
            }
        }
        catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
        {
            LogSdcp(LogLevel.Debug, "SDCP composite status cancelled", correlationId: null, exception: ex);
            throw;
        }
        catch (Exception ex)
        {
            LogSdcp(LogLevel.Debug, "SDCP composite status failed", correlationId: null, new { operation = "GetCompositeStatus" }, ex);
            throw;  // Let callers (polling service) handle failure counting
        }
    }

    public Task<PrinterCompositeStatus> GetCompositeStatusAsync(Uri baseUrl, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return GetCompositeStatusAsync(baseUrl.ToString(), ct);
    }

    public async Task<PrinterDto> CreatePrinterDtoAsync(
        Printer printer,
        PrinterCompositeStatus status,
        CancellationToken ct = default)
    {
        // Get camera URLs from SDCP client methods
        string? cameraStreamUrl = await GetCameraUrlAsync(printer.ServerUrl, ct).ConfigureAwait(false);
        string? cameraSnapshotUrl = await GetCameraSnapshotUrlAsync(printer.ServerUrl, ct).ConfigureAwait(false);

        // Construct backend-specific PrinterDto
        return new PrinterDto(
            Id: printer.Id,
            Name: printer.Name,
            Notes: printer.Notes,
            IsOnline: status.IsOnline,
            State: status.State,
            ManufacturerName: printer.Manufacturer?.Name,
            ModelName: printer.Model?.Name,
            Progress: status.Progress,
            JobName: status.JobName,
            ThumbnailUrl: status.ThumbnailUrl,
            CameraStreamUrl: cameraStreamUrl,
            CameraSnapshotUrl: cameraSnapshotUrl,
            X: status.X,
            Y: status.Y,
            Z: status.Z,
            HotendTemp: status.HotendTemp,
            BedTemp: status.BedTemp,
            HotendTarget: status.HotendTarget,
            BedTarget: status.BedTarget,
            Backend: PrinterBackend.SDCP,
            ApiKey: printer.ApiKey,
            Username: printer.Username,
            Password: printer.Password,
            OriginalServerUrl: printer.OriginalServerUrl,
            BackendPort: printer.BackendPort,
            FrontendPort: printer.FrontendPort,
            BackendUrl: printer.BackendUrl,
            FrontendUrl: printer.FrontendUrl,
            Location: printer.Location == null ? null : new LocationSummaryDto(printer.Location.Id, printer.Location.Name, printer.Location.Description));
    }

    // Print control methods (ISupportsStartPrint + ISupportsControlOperations)
    public async Task<bool> StartPrintAsync(string baseUrl, string fileName, PrinterCredential? credential = null, CancellationToken ct = default)
    {
        // SDCP protocol requires the full storage path — files uploaded via /uploadFile/upload
        // are stored under /local/ on the printer.
        string sdcpPath = fileName.StartsWith("/local/", StringComparison.OrdinalIgnoreCase)
            ? fileName
            : $"/local/{fileName}";

        return await SendCommandAsync(baseUrl, SdcpCommandIds.StartPrint, new { Filename = sdcpPath, StartLayer = 0, Calibration_switch = 0, PrintPlatformType = 0, Tlp_Switch = 0 }, ct);
    }

    public Task<bool> StartPrintAsync(Uri baseUrl, string fileName, PrinterCredential? credential = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(fileName);
        return StartPrintAsync(baseUrl.ToString(), fileName, credential, ct);
    }

    public async Task<bool> PauseAsync(string baseUrl, PrinterCredential? credential = null, CancellationToken ct = default)
    {
        return await SendCommandAsync(baseUrl, SdcpCommandIds.PausePrint, new { }, ct);
    }

    public Task<bool> PauseAsync(Uri baseUrl, PrinterCredential? credential = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return PauseAsync(baseUrl.ToString(), credential, ct);
    }

    public async Task<bool> CancelAsync(string baseUrl, PrinterCredential? credential = null, CancellationToken ct = default)
    {
        return await SendCommandAsync(baseUrl, SdcpCommandIds.CancelPrint, new { }, ct);
    }

    public Task<bool> CancelAsync(Uri baseUrl, PrinterCredential? credential = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return CancelAsync(baseUrl.ToString(), credential, ct);
    }

    public async Task<bool> ResumeAsync(string baseUrl, PrinterCredential? credential = null, CancellationToken ct = default)
    {
        return await SendCommandAsync(baseUrl, SdcpCommandIds.ResumePrint, new { }, ct);
    }

    public Task<bool> ResumeAsync(Uri baseUrl, PrinterCredential? credential = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return ResumeAsync(baseUrl.ToString(), credential, ct);
    }

    // Camera control methods
    public async Task<string?> GetCameraUrlAsync(string baseUrl, CancellationToken ct = default)
    {
        try
        {
            // SDCP cameras are typically accessible via HTTP streaming
            Uri baseUri = new(NormalizeBaseUrl(baseUrl, 80));
            async Task<string?> TryGetUrlAsync(int port)
            {
                Uri cameraUri = new UriBuilder
                {
                    Scheme = Uri.UriSchemeHttp,
                    Host = baseUri.Host,
                    Port = port,
                    Path = "/video"
                }.Uri;

                using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(_timeouts.StatusPollTimeout);

                try
                {
                    using HttpResponseMessage response = await _httpClient.GetAsync(cameraUri, cts.Token);
                    return response.IsSuccessStatusCode ? cameraUri.ToString() : null;
                }
                catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
                {
                    return null;
                }
            }

            string? url = await TryGetUrlAsync(8080);
            if (!string.IsNullOrWhiteSpace(url))
            {
                return url;
            }

            return await TryGetUrlAsync(3030);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Expected when cancellation is requested
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogSdcp(LogLevel.Debug, "Failed to get camera URL", correlationId: null, new { baseUrl }, ex);
        }

        return null;
    }

    public Task<string?> GetCameraUrlAsync(Uri baseUrl, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return GetCameraUrlAsync(baseUrl.ToString(), ct);
    }

    public async Task<string?> GetCameraSnapshotUrlAsync(string baseUrl, CancellationToken ct = default)
    {
        try
        {
            // SDCP camera snapshots are typically available via HTTP
            Uri baseUri = new(NormalizeBaseUrl(baseUrl, 80));
            async Task<string?> TryGetUrlAsync(int port)
            {
                Uri snapshotUri = new UriBuilder
                {
                    Scheme = Uri.UriSchemeHttp,
                    Host = baseUri.Host,
                    Port = port,
                    Path = "/snapshot"
                }.Uri;

                using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(_timeouts.StatusPollTimeout);

                try
                {
                    using HttpResponseMessage response = await _httpClient.GetAsync(snapshotUri, cts.Token);
                    return response.IsSuccessStatusCode ? snapshotUri.ToString() : null;
                }
                catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
                {
                    return null;
                }
            }

            string? url = await TryGetUrlAsync(8080);
            if (!string.IsNullOrWhiteSpace(url))
            {
                return url;
            }

            return await TryGetUrlAsync(3030);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Expected when cancellation is requested
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogSdcp(LogLevel.Debug, "Failed to get camera snapshot URL", correlationId: null, new { baseUrl }, ex);
        }

        return null;
    }

    public Task<string?> GetCameraSnapshotUrlAsync(Uri baseUrl, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return GetCameraSnapshotUrlAsync(baseUrl.ToString(), ct);
    }

    public async Task<bool> EnableCameraAsync(string baseUrl, CancellationToken ct = default)
    {
        return await SendCommandAsync(baseUrl, SdcpCommandIds.SetCameraEnabled, new { Enable = 1 }, ct);
    }

    public Task<bool> EnableCameraAsync(Uri baseUrl, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return EnableCameraAsync(baseUrl.ToString(), ct);
    }

    public async Task<bool> DisableCameraAsync(string baseUrl, CancellationToken ct = default)
    {
        return await SendCommandAsync(baseUrl, SdcpCommandIds.SetCameraEnabled, new { Enable = 0 }, ct);
    }

    public Task<bool> DisableCameraAsync(Uri baseUrl, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return DisableCameraAsync(baseUrl.ToString(), ct);
    }

    // File management methods

    /// <summary>
    /// Returns all file/folder entries from the printer's local storage via SDCP Cmd 258.
    /// </summary>
    internal async Task<List<SdcpFileEntry>> GetFileListEntriesAsync(string baseUrl, CancellationToken ct = default)
    {
        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeouts.CommandTimeout);
            string requestId = Guid.NewGuid().ToString("N");

            var (ws, wsUri) = await ConnectWebSocketAsync(baseUrl, operation: "GetFileList", correlationId: requestId, cts.Token);
            using (ws)
            {
                string wsUrl = wsUri.ToString();

                // Send file list request — Url specifies the storage path to list
                SdcpMessage<object> fileListRequest = new(
                    string.Empty,
                    new SdcpData<object>(
                        Cmd: SdcpCommandIds.GetFileList,
                        Data: new { Url = "/local" },
                        RequestID: requestId,
                        MainboardID: GetCachedMainboardId(baseUrl),
                        TimeStamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
                    string.Empty);

                string json = JsonSerializer.Serialize(fileListRequest, JsonOptions);
                byte[] bytes = Encoding.UTF8.GetBytes(json);
                await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cts.Token);

                LogSdcp(
                    LogLevel.Debug,
                    "SDCP WS file list request sent",
                    requestId,
                    new { operation = "GetFileList", cmd = SdcpCommandIds.GetFileList, bytes = bytes.Length, wsUrl });

                string? responseJson = await ReceiveTextMessageAsync(ws, operation: "GetFileList", correlationId: requestId, cts.Token);
                if (!string.IsNullOrWhiteSpace(responseJson))
                {
                    var response = JsonSerializer.Deserialize<SdcpFileListAckResponse>(responseJson, JsonOptions);
                    var result = response?.Data?.Data;

                    if (result is null || result.Ack != 0)
                    {
                        LogSdcp(LogLevel.Warning, $"SDCP file list returned Ack={result?.Ack ?? -1}", requestId);
                        return [];
                    }

                    List<SdcpFileEntry> entries = result.FileList ?? [];
                    int fileCount = entries.Count(e => e.Type == 1);
                    LogSdcp(LogLevel.Debug, $"SDCP file list: {fileCount} files, {entries.Count - fileCount} folders", requestId);
                    return entries;
                }

                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, cts.Token);
                return [];
            }
        }
        catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
        {
            LogSdcp(LogLevel.Debug, "SDCP file list cancelled", correlationId: null, exception: ex);
            throw;
        }
        catch (Exception ex)
        {
            LogSdcp(LogLevel.Debug, "SDCP file list failed", correlationId: null, new { operation = "GetFileList" }, ex);
            return [];
        }
    }

    public async Task<string[]> GetFileListAsync(string baseUrl, CancellationToken ct = default)
    {
        List<SdcpFileEntry> entries = await GetFileListEntriesAsync(baseUrl, ct);
        return entries
            .Where(e => e.Type == 1)
            .Select(e => e.Name)
            .ToArray();
    }

    public Task<string[]> GetFileListAsync(Uri baseUrl, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return GetFileListAsync(baseUrl.ToString(), ct);
    }

    private async Task<bool> SendCommandAsync<T>(string baseUrl, int cmd, T data, CancellationToken ct = default)
    {
        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeouts.CommandTimeout);
            string requestId = Guid.NewGuid().ToString("N");

            var (ws, wsUri) = await ConnectWebSocketAsync(baseUrl, operation: "SendCommand", correlationId: requestId, cts.Token);
            using (ws)
            {
                string wsUrl = wsUri.ToString();

                SdcpMessage<T> command = new(
                    string.Empty,
                    new SdcpData<T>(
                        Cmd: cmd,
                        Data: data,
                        RequestID: requestId,
                        MainboardID: GetCachedMainboardId(baseUrl),
                        TimeStamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
                    string.Empty);

                string json = JsonSerializer.Serialize(command, JsonOptions);
                byte[] bytes = Encoding.UTF8.GetBytes(json);
                await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cts.Token);

                LogSdcp(
                    LogLevel.Debug,
                    "SDCP WS command sent",
                    requestId,
                    new { operation = "SendCommand", cmd, bytes = bytes.Length, wsUrl });

                string? responseJson = await ReceiveTextMessageAsync(ws, operation: "SendCommand", correlationId: requestId, cts.Token);
                if (!string.IsNullOrWhiteSpace(responseJson))
                {
                    SdcpAckResponse? ackResponse = JsonSerializer.Deserialize<SdcpAckResponse>(responseJson, JsonOptions);

                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, cts.Token);

                    // ACK code 0 = success, 1 = error, 2 = file not found
                    int? ack = ackResponse?.Data?.Data?.Ack;
                    LogSdcp(
                        ack == 0 ? LogLevel.Debug : LogLevel.Warning,
                        ack == 0 ? "SDCP WS command ack received" : $"SDCP WS command rejected (Ack={ack})",
                        requestId,
                        new { operation = "SendCommand", cmd, ack, wsUrl });
                    return ack == 0;
                }

                LogSdcp(
                    LogLevel.Debug,
                    "SDCP WS command returned empty response",
                    requestId,
                    new { operation = "SendCommand", cmd, wsUrl });

                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, cts.Token);
                return false;
            }
        }
        catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
        {
            LogSdcp(LogLevel.Debug, "SDCP command cancelled", correlationId: null, new { operation = "SendCommand", cmd }, ex);
            throw;
        }
        catch (Exception ex)
        {
            LogSdcp(LogLevel.Debug, "SDCP command failed", correlationId: null, new { operation = "SendCommand", cmd }, ex);
            return false;
        }
    }

    // File upload and management methods
    public async Task<bool> UploadGcodeAsync(string baseUrl, string fileName, Stream fileContent, PrinterCredential? credential = null, CancellationToken ct = default)
    {
        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeouts.FileUploadTimeout);

            // SDCP v3 docs show HTTP upload endpoints living on port 3030.
            // Some printers may expose SDCP behind a reverse proxy on port 80; if the stream is seekable,
            // we can safely retry on fallback ports.
            Uri normalizedBaseUri = new(NormalizeBaseUrl(baseUrl, 80));
            string requestId = Guid.NewGuid().ToString("N");

            List<Uri> candidates = new();
            candidates.Add(new UriBuilder
            {
                Scheme = Uri.UriSchemeHttp,
                Host = normalizedBaseUri.Host,
                Port = SdcpWebSocketPort,
                Path = "/uploadFile/upload"
            }.Uri);

            // Fallbacks for devices that proxy SDCP over common ports.
            candidates.Add(new UriBuilder
            {
                Scheme = Uri.UriSchemeHttp,
                Host = normalizedBaseUri.Host,
                Port = 80,
                Path = "/uploadFile/upload"
            }.Uri);

            // Remove duplicates while preserving order.
            List<Uri> uploadUris = candidates.Distinct().ToList();

            long? startPosition = null;
            if (fileContent.CanSeek)
            {
                startPosition = fileContent.Position;
            }

            MultipartFormDataContent CreateMultipartContent()
            {
                MultipartFormDataContent formContent = new();
                StreamContent streamContent = new(fileContent);
                streamContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                formContent.Add(streamContent, "file", fileName);
                return formContent;
            }

            foreach (Uri uploadUri in uploadUris)
            {
                if (startPosition != null)
                {
                    fileContent.Position = startPosition.Value;
                }

                LogSdcp(LogLevel.Debug, "SDCP HTTP upload sending", requestId, new { operation = "UploadGcode", uploadUrl = uploadUri.ToString(), fileName, canSeek = fileContent.CanSeek });

                using MultipartFormDataContent formContent = CreateMultipartContent();

                try
                {
                    using HttpResponseMessage resp = await _httpClient.PostAsync(uploadUri, formContent, cts.Token);
                    LogSdcp(LogLevel.Debug, "SDCP HTTP upload response", requestId, new { operation = "UploadGcode", uploadUrl = uploadUri.ToString(), statusCode = (int)resp.StatusCode });

                    if (resp.IsSuccessStatusCode)
                    {
                        return true;
                    }

                    if (!fileContent.CanSeek)
                    {
                        // Can't retry with a forward-only stream.
                        return false;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (fileContent.CanSeek)
                {
                    LogSdcp(LogLevel.Debug, "SDCP HTTP upload attempt failed", requestId, new { operation = "UploadGcode", uploadUrl = uploadUri.ToString() }, ex);

                    // continue to next candidate
                }
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    public Task<bool> UploadGcodeAsync(Uri baseUrl, string fileName, Stream fileContent, PrinterCredential? credential = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(fileContent);
        return UploadGcodeAsync(baseUrl.ToString(), fileName, fileContent, credential, ct);
    }

    public void Dispose()
    {
        // No resources to dispose in this implementation
    }

    // ========== CAPABILITY INTERFACE IMPLEMENTATIONS ==========
    async Task<List<PrinterFileInfo>> ISupportsFileList.GetFileListAsync(string baseUrl, PrinterCredential? credential = null, CancellationToken ct = default)
    {
        List<SdcpFileEntry> entries = await GetFileListEntriesAsync(baseUrl, ct);
        return entries
            .Where(e => e.Type == 1) // files only
            .Select(e => new PrinterFileInfo
            {
                Name = System.IO.Path.GetFileName(e.Name),
                Path = e.Name,
                Size = e.UsedSize > 0 ? e.UsedSize : null,
                Modified = null, // SDCP does not provide modification timestamps
                ThumbnailUrl = null
            }).ToList();
    }

    Task<string?> ISupportsCamera.GetCameraStreamUrlAsync(string baseUrl, int? frontendPort = null, PrinterCredential? credential = null, CancellationToken ct = default)
    {
        return GetCameraUrlAsync(baseUrl, ct);
    }

    Task<string?> ISupportsCamera.GetCameraSnapshotUrlAsync(string baseUrl, int? frontendPort = null, PrinterCredential? credential = null, CancellationToken ct = default)
    {
        return GetCameraSnapshotUrlAsync(baseUrl, ct);
    }

    // ========== FILE DELETE CAPABILITY ==========

    /// <summary>
    /// Deletes a file from the printer's storage via SDCP Cmd 259.
    /// </summary>
    async Task<bool> ISupportsFileDelete.DeleteFileAsync(string baseUrl, string filePath, PrinterCredential? credential, CancellationToken ct)
    {
        bool result = await SendCommandAsync(baseUrl, SdcpCommandIds.DeleteFile, new { Url = filePath }, ct);
        if (!result)
        {
            LogSdcp(LogLevel.Warning, $"SDCP file delete failed for '{filePath}'");
        }

        return result;
    }

    // ========== HISTORY CAPABILITY ==========

    /// <summary>
    /// Retrieves print history via SDCP Cmd 320 (list IDs) then Cmd 321 (details per ID).
    /// Maps SDCP-specific fields into the shared HistoryJob DTO.
    /// </summary>
    async Task<HistoryListResponse?> ISupportsHistory.GetHistoryListAsync(
        string baseUrl, int? limit, int? start, DateTime? since,
        PrinterCredential? credential, CancellationToken ct)
    {
        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeouts.CommandTimeout);
            string requestId = Guid.NewGuid().ToString("N");

            var (ws, _) = await ConnectWebSocketAsync(baseUrl, operation: "GetHistoryIds", correlationId: requestId, cts.Token);
            using (ws)
            {
                // Step 1: Request history task ID list (Cmd 320)
                SdcpMessage<object> idsRequest = new(
                    string.Empty,
                    new SdcpData<object>(
                        Cmd: SdcpCommandIds.GetHistoryIds,
                        Data: new { },
                        RequestID: requestId,
                        MainboardID: GetCachedMainboardId(baseUrl),
                        TimeStamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
                    string.Empty);

                string json = JsonSerializer.Serialize(idsRequest, JsonOptions);
                byte[] bytes = Encoding.UTF8.GetBytes(json);
                await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cts.Token);

                string? idsResponseJson = await ReceiveTextMessageAsync(ws, "GetHistoryIds", requestId, cts.Token);
                if (string.IsNullOrWhiteSpace(idsResponseJson))
                {
                    return new HistoryListResponse { Count = 0, Jobs = [] };
                }

                var idsResponse = JsonSerializer.Deserialize<SdcpHistoryIdsAckResponse>(idsResponseJson, JsonOptions);
                var idsResult = idsResponse?.Data?.Data;

                if (idsResult is null || idsResult.Ack != 0 || idsResult.HistoryData is null or { Count: 0 })
                {
                    return new HistoryListResponse { Count = 0, Jobs = [] };
                }

                List<string> taskIds = idsResult.HistoryData;

                // Apply since filter client-side (SDCP has no server-side date filter)
                // Apply start/limit pagination client-side
                int skipCount = start ?? 0;
                int takeCount = limit ?? taskIds.Count;
                List<string> pageIds = taskIds.Skip(skipCount).Take(takeCount).ToList();

                // Step 2: Request details for each task ID (Cmd 321)
                List<HistoryJob> jobs = [];
                foreach (string taskId in pageIds)
                {
                    HistoryJob? job = await RequestHistoryDetailAsync(ws, baseUrl, taskId, requestId, since, cts.Token);
                    if (job is not null)
                    {
                        jobs.Add(job);
                    }
                }

                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, cts.Token);

                LogSdcp(LogLevel.Debug, $"SDCP history: {jobs.Count} jobs from {taskIds.Count} total", requestId);
                return new HistoryListResponse { Count = taskIds.Count, Jobs = jobs.ToArray() };
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogSdcp(LogLevel.Debug, "SDCP history list failed", correlationId: null, new { operation = "GetHistoryList" }, ex);
            return null;
        }
    }

    /// <summary>
    /// Requests details for a single history job via Cmd 321.
    /// Returns null if the job doesn't match the since filter.
    /// </summary>
    private async Task<HistoryJob?> RequestHistoryDetailAsync(
        ClientWebSocket ws, string baseUrl, string taskId, string correlationId, DateTime? since, CancellationToken ct)
    {
        string detailRequestId = Guid.NewGuid().ToString("N");
        SdcpMessage<object> detailRequest = new(
            string.Empty,
            new SdcpData<object>(
                Cmd: SdcpCommandIds.GetHistoryDetail,
                Data: new { Id = new[] { taskId } },
                RequestID: detailRequestId,
                MainboardID: GetCachedMainboardId(baseUrl),
                TimeStamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
            string.Empty);

        string json = JsonSerializer.Serialize(detailRequest, JsonOptions);
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);

        string? detailJson = await ReceiveTextMessageAsync(ws, "GetHistoryDetail", correlationId, ct);
        if (string.IsNullOrWhiteSpace(detailJson))
        {
            return null;
        }

        try
        {
            var detailResponse = JsonSerializer.Deserialize<SdcpHistoryDetailAckResponse>(detailJson, JsonOptions);
            var detailResult = detailResponse?.Data?.Data;
            if (detailResult is null || detailResult.Ack != 0 ||
                detailResult.HistoryDetailList is null or { Count: 0 })
            {
                return null;
            }

            var detail = detailResult.HistoryDetailList[0];

            // Apply client-side since filter
            if (since.HasValue && detail.BeginTime > 0)
            {
                double sinceUnix = new DateTimeOffset(since.Value.ToUniversalTime()).ToUnixTimeSeconds();
                if (detail.BeginTime < sinceUnix)
                {
                    return null;
                }
            }

            return MapSdcpHistoryToJob(detail);
        }
        catch (JsonException ex)
        {
            // Tolerate unknown fields per acceptance criteria
            LogSdcp(LogLevel.Debug, $"Failed to parse history detail for {taskId}", correlationId, exception: ex);
            return null;
        }
    }

    /// <summary>
    /// Maps SDCP-specific history detail fields into the shared HistoryJob DTO.
    /// Fields that have no SDCP equivalent are left at their default.
    /// </summary>
    private static HistoryJob MapSdcpHistoryToJob(SdcpHistoryDetail detail)
    {
        // SDCP V3.0.0 spec: 0 = Other, 1 = Completed, 2 = Exceptional, 3 = Stopped
        string status = detail.TaskStatus switch
        {
            1 => "completed",
            2 => "error",
            3 => "cancelled",
            _ => $"unknown({detail.TaskStatus})"
        };

        double duration = detail.EndTime > 0 && detail.BeginTime > 0
            ? detail.EndTime - detail.BeginTime
            : 0;

        return new HistoryJob
        {
            JobId = detail.TaskId ?? string.Empty,
            Exists = true,
            Filename = detail.TaskName ?? string.Empty,
            Status = status,
            StartTime = detail.BeginTime,
            EndTime = detail.EndTime > 0 ? detail.EndTime : null,
            PrintDuration = duration,
            TotalDuration = duration,
            FilamentUsed = 0, // SDCP does not report filament usage in history
            User = string.Empty
        };
    }

    async Task<HistoryJob?> ISupportsHistory.GetHistoryJobAsync(
        string baseUrl, string jobId, PrinterCredential? credential, CancellationToken ct)
    {
        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeouts.CommandTimeout);
            string requestId = Guid.NewGuid().ToString("N");

            var (ws, _) = await ConnectWebSocketAsync(baseUrl, operation: "GetHistoryDetail", correlationId: requestId, cts.Token);
            using (ws)
            {
                HistoryJob? job = await RequestHistoryDetailAsync(ws, baseUrl, jobId, requestId, null, cts.Token);
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, cts.Token);
                return job;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogSdcp(LogLevel.Debug, $"SDCP history job detail failed for {jobId}", correlationId: null, exception: ex);
            return null;
        }
    }

    Task<HistoryTotals?> ISupportsHistory.GetHistoryTotalsAsync(
        string baseUrl, PrinterCredential? credential, CancellationToken ct)
    {
        // SDCP protocol does not provide aggregate history totals
        return Task.FromResult<HistoryTotals?>(null);
    }

    Task<bool> ISupportsHistory.DeleteHistoryJobAsync(
        string baseUrl, string jobId, PrinterCredential? credential, CancellationToken ct)
    {
        // SDCP protocol does not support deleting individual history entries
        return Task.FromResult(false);
    }

    async Task<StandardPrinterInfo> ISupportsPrinterInformation.GetPrinterInformationAsync(
        string baseUrl, PrinterCredential? credential, CancellationToken ct)
    {
        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeouts.CommandTimeout);
            string requestId = Guid.NewGuid().ToString("N");

            var (ws, wsUri) = await ConnectWebSocketAsync(baseUrl, operation: "GetAttributes", correlationId: requestId, cts.Token);
            using (ws)
            {
                string wsUrl = wsUri.ToString();

                // Send Cmd 1 (Request for attribute message) per SDCP V3.0.0 spec.
                // The printer responds with an ack on sdcp/response topic, then publishes
                // the full attributes payload on sdcp/attributes topic.
                SdcpMessage<object> request = new(
                    string.Empty,
                    new SdcpData<object>(
                        Cmd: SdcpCommandIds.GetAttributes,
                        Data: new { },
                        RequestID: requestId,
                        MainboardID: GetCachedMainboardId(baseUrl),
                        TimeStamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
                    string.Empty);

                string json = JsonSerializer.Serialize(request, JsonOptions);
                byte[] bytes = Encoding.UTF8.GetBytes(json);
                await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cts.Token);

                LogSdcp(LogLevel.Debug, "SDCP WS attributes request sent", requestId,
                    new { operation = "GetAttributes", cmd = SdcpCommandIds.GetAttributes, bytes = bytes.Length, wsUrl });

                // Read up to 3 messages looking for the attributes response.
                // Cmd 1 triggers an ack first, then the actual attributes message.
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    string? responseJson = await ReceiveTextMessageAsync(ws, operation: "GetAttributes", correlationId: requestId, cts.Token);
                    if (string.IsNullOrWhiteSpace(responseJson))
                    {
                        continue;
                    }

                    // Try parsing as attributes response
                    SdcpAttributesResponse? attrResponse = JsonSerializer.Deserialize<SdcpAttributesResponse>(responseJson, JsonOptions);
                    if (attrResponse?.Attributes is not null)
                    {
                        CacheMainboardId(baseUrl, attrResponse.MainboardID);
                        SdcpAttributes attrs = attrResponse.Attributes;

                        LogSdcp(LogLevel.Debug, "SDCP attributes parsed", requestId,
                            new { operation = "GetAttributes", name = attrs.Name, model = attrs.MachineName, brand = attrs.BrandName, firmware = attrs.FirmwareVersion });

                        try
                        {
                            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, cts.Token);
                        }
                        catch
                        {
                            // best-effort close
                        }

                        return new StandardPrinterInfo
                        {
                            Name = attrs.Name ?? string.Empty,
                            Model = attrs.MachineName ?? string.Empty,
                            Firmware = attrs.FirmwareVersion ?? string.Empty,
                            BackendVersion = attrs.ProtocolVersion,
                            ApiVersion = attrs.ProtocolVersion
                        };
                    }

                    // Not the attributes message (likely the ack); try reading the next one
                    LogSdcp(LogLevel.Debug, "SDCP received non-attributes message, reading next", requestId,
                        new { operation = "GetAttributes", attempt, responseLength = responseJson.Length });
                }

                // Exhausted attempts without getting attributes
                try
                {
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, cts.Token);
                }
                catch
                {
                    // best-effort close
                }

                LogSdcp(LogLevel.Warning, "SDCP attributes not received after multiple reads", requestId,
                    new { operation = "GetAttributes", wsUrl });
                return new StandardPrinterInfo();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogSdcp(LogLevel.Debug, "SDCP get printer information failed", correlationId: null,
                new { operation = "GetAttributes" }, ex);
            return new StandardPrinterInfo();
        }
    }
}

#pragma warning restore CS1066
