#pragma warning disable S1006, CA2213, S1939 // Default parameters, HttpClient disposal, and interface inheritance are intentional

using System.Buffers;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Settings;
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

    public double CurrentTicks { get; set; }

    public double TotalTicks { get; set; }

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

public sealed class SdcpClient(HttpClient httpClient, ILogger<SdcpClient> logger, BackendTimeoutSettings timeouts) : PrinterClientBase, ISdcpClient,
    ISupportsFileList,
    ISupportsFileUpload,
    ISupportsStartPrint,
    ISupportsUploadAndPrint,
    ISupportsControlOperations,
    ISupportsCamera,
    ISupportsHistory,
    ISupportsFileDelete,
    ISupportsPrinterInformation,
    ISupportsStatus,
    ISupportsCompositeStatus
{
    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    private readonly ILogger<SdcpClient> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly BackendTimeoutSettings _timeouts = timeouts ?? throw new ArgumentNullException(nameof(timeouts));

    private const int SdcpWebSocketPort = 3030;

    /// <summary>
    /// Maximum chunk size for SDCP file uploads (1 MB), matching OrcaSlicer ElegooLink implementation.
    /// The printer expects uploads chunked with MD5, UUID, offset, and total size metadata.
    /// </summary>
    private const int MaxUploadChunkSize = 1048576; // 1024 * 1024

    /// <summary>
    /// SDCP CurrentStatus code indicating the printer is checking/indexing a file.
    /// After upload, the printer enters this state while it validates and indexes the file.
    /// </summary>
    private const int StatusFileChecking = 8;

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

#pragma warning disable CA2254 // Intentional wrapper — callers pass constant templates
    private void LogSdcp(LogLevel level, string message, Exception? exception = null)
        => _logger.Log(level, exception, message);
#pragma warning restore CA2254

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

    // SDCP PrintInfo.Status codes (print-job-level state)
    // Note: code 9 means "complete" at the job level (all layers done, filename cleared).
    // This is distinct from CurrentStatus[9] which means "starting" at the machine level.
    private static readonly Dictionary<int, string> StatusCodeMap = new()
    {
        { 0, "idle" },
        { 5, "idle" },
        { 8, "preparing" },
        { 9, "complete" },
        { 10, "paused" },
        { 13, "printing" },
        { 20, "printing" }
    };

    // SDCP CurrentStatus codes (machine-level state, different numbering from PrintInfo.Status)
    // Values observed via live WebSocket and OrcaSlicer ElegooLink.cpp:
    //   0 = idle, 1 = printing, 5 = idle/busy, 8 = file checking/preparing, 9 = starting
    private static readonly Dictionary<int, string> MachineStatusCodeMap = new()
    {
        { 0, "idle" },
        { 1, "printing" },
        { 5, "idle" },
        { 8, "preparing" },
        { 9, "starting" }
    };

    // Priority for state resolution: higher value wins when merging machine + job states.
    private static readonly Dictionary<string, int> StatePriority = new()
    {
        { "idle", 0 },
        { "online", 1 },
        { "unknown", 1 },
        { "complete", 2 },
        { "preparing", 2 },
        { "starting", 3 },
        { "paused", 4 },
        { "printing", 5 }
    };

    /// <summary>
    /// Resolves the best display state by combining the machine-level CurrentStatus array
    /// with the job-level PrintInfo.Status. CurrentStatus captures transient machine states
    /// (e.g., file checking/preparing) that PrintInfo may not reflect.
    /// </summary>
    private static string ResolveMachineState(SdcpStatus status)
    {
        string state = "online";

        // Check PrintInfo.Status (job-level state)
        if (status.PrintInfo != null)
        {
            state = StatusCodeMap.GetValueOrDefault(status.PrintInfo.Status, "unknown");
        }

        // If the job state is idle/online, check CurrentStatus for machine-level activity
        // that PrintInfo doesn't reflect (e.g., file checking = "preparing")
        if (StatePriority.GetValueOrDefault(state) <= StatePriority.GetValueOrDefault("online")
            && status.CurrentStatus is { Length: > 0 })
        {
            foreach (int code in status.CurrentStatus)
            {
                if (MachineStatusCodeMap.TryGetValue(code, out string? mapped)
                    && StatePriority.GetValueOrDefault(mapped) > StatePriority.GetValueOrDefault(state))
                {
                    state = mapped;
                }
            }
        }

        return state;
    }

    /// <summary>
    /// Computes print progress as a 0.0–1.0 fraction.
    /// Prefers CurrentLayer/TotalLayer (always updated during printing), falling back to
    /// the firmware Progress field (which some firmware only updates sporadically).
    /// </summary>
    /// <summary>
    /// Computes print progress as a 0–100 percentage for UI display.
    /// The frontend expects progress in the 0–100 range (same as Moonraker).
    /// Prefers layer-based calculation (most reliable on Elegoo firmware),
    /// falls back to firmware-reported Progress field.
    /// </summary>
    private static double? ComputeProgress(SdcpPrintInfo? printInfo)
    {
        if (printInfo == null)
        {
            return null;
        }

        // Layer-based progress: most reliable on Elegoo SDCP firmware
        // Returns 0–100 range to match frontend expectations
        if (printInfo.TotalLayer > 0)
        {
            return (double)printInfo.CurrentLayer / printInfo.TotalLayer * 100.0;
        }

        // Fallback to firmware-reported progress (already 0–100 scale)
        if (printInfo.Progress > 0)
        {
            return printInfo.Progress;
        }

        return 0.0;
    }

#pragma warning disable S1172 // Parameters reserved for future diagnostic logging
    private async Task<string?> ReceiveTextMessageAsync(ClientWebSocket ws, string operation, string correlationId, CancellationToken ct)
#pragma warning restore S1172
    {
        byte[] rented = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            ArrayBufferWriter<byte> writer = new();

            long startedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            LogSdcp(LogLevel.Debug, "SDCP WS receive started");

            while (true)
            {
                WebSocketReceiveResult result = await ws.ReceiveAsync(rented, ct);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    LogSdcp(LogLevel.Debug, "SDCP WS received close frame");
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
                        LogSdcp(LogLevel.Debug, "SDCP WS skipping non-text frame");
                    }

                    continue;
                }

                writer.Write(rented.AsSpan(0, result.Count));

                if (result.EndOfMessage)
                {
                    string text = Encoding.UTF8.GetString(writer.WrittenSpan);
                    long endedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    LogSdcp(LogLevel.Debug, "SDCP WS receive completed");
                    return text;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// Reads WebSocket messages until a status broadcast (containing a top-level Status object)
    /// is found. SDCP printers send periodic status broadcasts on the <c>sdcp/status/...</c> topic
    /// alongside ACK responses on <c>sdcp/response/...</c>. When we send a Cmd 0 (GetStatus),
    /// either message may arrive first. This method skips ACK responses and returns the first
    /// status broadcast, or null if none arrives within <paramref name="maxAttempts"/> reads.
    /// </summary>
    private async Task<SdcpStatusResponse?> ReceiveStatusBroadcastAsync(
        ClientWebSocket ws, string baseUrl, string operation, string correlationId, CancellationToken ct, int maxAttempts = 3)
    {
        for (int i = 0; i < maxAttempts; i++)
        {
            string? json = await ReceiveTextMessageAsync(ws, operation, correlationId, ct);
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            try
            {
                SdcpStatusResponse? response = JsonSerializer.Deserialize<SdcpStatusResponse>(json, JsonOptions);
                if (response?.Status != null)
                {
                    CacheMainboardId(baseUrl, response.MainboardID);
                    return response;
                }

                // Got an ACK or non-status message; read next message
                LogSdcp(LogLevel.Debug, $"SDCP WS skipping non-status message (attempt {i + 1})");
            }
            catch (JsonException ex)
            {
                // Malformed or unexpected type in JSON; try next message
                LogSdcp(LogLevel.Warning, $"SDCP WS status JSON parse error (attempt {i + 1}): {ex.Message}");
            }
        }

        LogSdcp(LogLevel.Debug, $"SDCP WS no status broadcast received after {maxAttempts} attempts");
        return null;
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

#pragma warning disable S1172 // Parameters reserved for future diagnostic logging
    private async Task<(ClientWebSocket ws, Uri wsUri)> ConnectWebSocketAsync(
        string baseUrl,
        string operation,
        string correlationId,
        CancellationToken ct)
#pragma warning restore S1172
    {
        List<Uri> candidates = GetWebSocketCandidateUris(baseUrl);
        Exception? lastException = null;

        foreach (Uri candidate in candidates)
        {
            ClientWebSocket ws = new();
            ws.Options.KeepAliveInterval = WebSocketKeepAliveInterval;
            try
            {
                LogSdcp(LogLevel.Debug, "SDCP WS connecting");
                await ws.ConnectAsync(candidate, ct);
                LogSdcp(LogLevel.Debug, "SDCP WS connected");
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
                LogSdcp(LogLevel.Debug, "SDCP WS connect attempt failed", ex);
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

                LogSdcp(LogLevel.Debug, "SDCP WS test request sent");

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
                    LogSdcp(LogLevel.Debug, "SDCP WS close failed", ex);
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

                LogSdcp(LogLevel.Debug, responded ? "SDCP test connection succeeded" : "SDCP test connection got no response");
                return responded;
            }
        }
        catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
        {
            LogSdcp(LogLevel.Debug, "SDCP test connection cancelled", ex);
            throw;
        }
        catch (Exception ex)
        {
            LogSdcp(LogLevel.Debug, "SDCP test connection failed", ex);
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

                LogSdcp(LogLevel.Debug, "SDCP WS status request sent");

                SdcpStatusResponse? statusResponse = await ReceiveStatusBroadcastAsync(ws, baseUrl, "GetStatus", requestId, cts.Token);
                if (statusResponse?.Status != null)
                {
                    string state = ResolveMachineState(statusResponse.Status);
                    LogSdcp(LogLevel.Debug, "SDCP status parsed");
                    return new PrinterStatus(true, state);
                }

                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, cts.Token);
                return new PrinterStatus(true, "online");
            }
        }
        catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
        {
            LogSdcp(LogLevel.Debug, "SDCP status cancelled", ex);
            throw;
        }
        catch (Exception ex)
        {
            LogSdcp(LogLevel.Debug, "SDCP status failed", ex);
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

                LogSdcp(LogLevel.Debug, "SDCP WS job request sent");

                SdcpStatusResponse? statusResponse = await ReceiveStatusBroadcastAsync(ws, baseUrl, "GetJob", requestId, cts.Token);
                if (statusResponse?.Status != null)
                {
                    string state = ResolveMachineState(statusResponse.Status);
                    SdcpPrintInfo? printInfo = statusResponse.Status.PrintInfo;
                    double progress = printInfo != null ? printInfo.Progress / 100.0 : 0;
                    string? jobName = printInfo != null && !string.IsNullOrWhiteSpace(printInfo.Filename)
                        ? Path.GetFileName(printInfo.Filename) : null;

                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, cts.Token);
                    return new PrinterJob(state, progress, jobName, null);
                }

                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, cts.Token);
                return new PrinterJob(null, null, null, null);
            }
        }
        catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
        {
            LogSdcp(LogLevel.Debug, "SDCP job cancelled", ex);
            throw;
        }
        catch (Exception ex)
        {
            LogSdcp(LogLevel.Debug, "SDCP job failed", ex);
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

                LogSdcp(LogLevel.Debug, "SDCP WS composite status request sent");

                SdcpStatusResponse? statusResponse = await ReceiveStatusBroadcastAsync(ws, baseUrl, "GetCompositeStatus", requestId, cts.Token);
                if (statusResponse?.Status != null)
                {
                    SdcpStatus status = statusResponse.Status;
                    SdcpPrintInfo? printInfo = status.PrintInfo;

                    string state = ResolveMachineState(status);

                    double? progress = ComputeProgress(printInfo);
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
                        LogSdcp(LogLevel.Debug, "SDCP WS close failed", ex);
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

                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, cts.Token);
                return new PrinterCompositeStatus(true, "online", null, null, null, null, null);
            }
        }
        catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
        {
            LogSdcp(LogLevel.Debug, "SDCP composite status cancelled", ex);
            throw;
        }
        catch (Exception ex)
        {
            LogSdcp(LogLevel.Debug, "SDCP composite status failed", ex);
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
            FileName: PrinterStatusDto.ExtractFileName(status.JobName),
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
            ObicoEnabled: printer.ObicoEnabled,
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

        LogSdcp(LogLevel.Information, $"SDCP starting print: {sdcpPath}");

        return await SendCommandAsync(baseUrl, SdcpCommandIds.StartPrint,
            new { Filename = sdcpPath, StartLayer = 0, Calibration_switch = 0, PrintPlatformType = 0, Tlp_Switch = 0 },
            timeout: _timeouts.PrintControlTimeout,
            ct: ct);
    }

    public Task<bool> StartPrintAsync(Uri baseUrl, string fileName, PrinterCredential? credential = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(fileName);
        return StartPrintAsync(baseUrl.ToString(), fileName, credential, ct);
    }

    public async Task<bool> PauseAsync(string baseUrl, PrinterCredential? credential = null, CancellationToken ct = default)
    {
        return await SendCommandAsync(baseUrl, SdcpCommandIds.PausePrint, new { }, timeout: _timeouts.PrintControlTimeout, ct: ct);
    }

    public Task<bool> PauseAsync(Uri baseUrl, PrinterCredential? credential = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return PauseAsync(baseUrl.ToString(), credential, ct);
    }

    public async Task<bool> CancelAsync(string baseUrl, PrinterCredential? credential = null, CancellationToken ct = default)
    {
        return await SendCommandAsync(baseUrl, SdcpCommandIds.CancelPrint, new { }, timeout: _timeouts.PrintControlTimeout, ct: ct);
    }

    public Task<bool> CancelAsync(Uri baseUrl, PrinterCredential? credential = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return CancelAsync(baseUrl.ToString(), credential, ct);
    }

    public async Task<bool> ResumeAsync(string baseUrl, PrinterCredential? credential = null, CancellationToken ct = default)
    {
        return await SendCommandAsync(baseUrl, SdcpCommandIds.ResumePrint, new { }, timeout: _timeouts.PrintControlTimeout, ct: ct);
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
            LogSdcp(LogLevel.Debug, "Failed to get camera URL", ex);
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
            LogSdcp(LogLevel.Debug, "Failed to get camera snapshot URL", ex);
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
        return await SendCommandAsync(baseUrl, SdcpCommandIds.SetCameraEnabled, new { Enable = 1 }, ct: ct);
    }

    public Task<bool> EnableCameraAsync(Uri baseUrl, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return EnableCameraAsync(baseUrl.ToString(), ct);
    }

    public async Task<bool> DisableCameraAsync(string baseUrl, CancellationToken ct = default)
    {
        return await SendCommandAsync(baseUrl, SdcpCommandIds.SetCameraEnabled, new { Enable = 0 }, ct: ct);
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

                LogSdcp(LogLevel.Debug, "SDCP WS file list request sent");

                string? responseJson = await ReceiveTextMessageAsync(ws, operation: "GetFileList", correlationId: requestId, cts.Token);
                if (!string.IsNullOrWhiteSpace(responseJson))
                {
                    var response = JsonSerializer.Deserialize<SdcpFileListAckResponse>(responseJson, JsonOptions);
                    var result = response?.Data?.Data;

                    if (result is null || result.Ack != 0)
                    {
                        LogSdcp(LogLevel.Warning, $"SDCP file list returned Ack={result?.Ack ?? -1}");
                        return [];
                    }

                    List<SdcpFileEntry> entries = result.FileList ?? [];
                    int fileCount = entries.Count(e => e.Type == 1);
                    LogSdcp(LogLevel.Debug, $"SDCP file list: {fileCount} files, {entries.Count - fileCount} folders");
                    return entries;
                }

                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, cts.Token);
                return [];
            }
        }
        catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
        {
            LogSdcp(LogLevel.Debug, "SDCP file list cancelled", ex);
            throw;
        }
        catch (Exception ex)
        {
            LogSdcp(LogLevel.Debug, "SDCP file list failed", ex);
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

    private async Task<bool> SendCommandAsync<T>(string baseUrl, int cmd, T data, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout ?? _timeouts.CommandTimeout);
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

                LogSdcp(LogLevel.Debug, "SDCP WS command sent");

                string? responseJson = await ReceiveTextMessageAsync(ws, operation: "SendCommand", correlationId: requestId, cts.Token);
                if (!string.IsNullOrWhiteSpace(responseJson))
                {
                    SdcpAckResponse? ackResponse = JsonSerializer.Deserialize<SdcpAckResponse>(responseJson, JsonOptions);

                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, cts.Token);

                    // ACK code 0 = success, 1 = error, 2 = file not found
                    int? ack = ackResponse?.Data?.Data?.Ack;
                    LogSdcp(ack == 0 ? LogLevel.Debug : LogLevel.Warning, ack == 0 ? "SDCP WS command ack received" : $"SDCP WS command rejected (Ack={ack})");
                    return ack == 0;
                }

                LogSdcp(LogLevel.Debug, "SDCP WS command returned empty response");

                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, cts.Token);
                return false;
            }
        }
        catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
        {
            LogSdcp(LogLevel.Debug, $"SDCP command {cmd} cancelled", ex);
            throw;
        }
        catch (OperationCanceledException ex)
        {
            LogSdcp(LogLevel.Warning, $"SDCP command {cmd} timed out after {(timeout ?? _timeouts.CommandTimeout).TotalSeconds}s: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            LogSdcp(LogLevel.Warning, $"SDCP command {cmd} failed: {ex.Message}", ex);
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

            // The stream must be seekable for MD5 computation and chunked upload.
            if (!fileContent.CanSeek)
            {
                LogSdcp(LogLevel.Warning, "SDCP upload requires a seekable stream for MD5 and chunked upload");
                return false;
            }

            long startPosition = fileContent.Position;
            long fileSize = fileContent.Length - startPosition;

            // Compute MD5 hash of the entire file (required by SDCP upload protocol).
            fileContent.Position = startPosition;
            string md5Hash = await ComputeMd5Async(fileContent, cts.Token);
            fileContent.Position = startPosition;

            // Generate a session UUID for this upload (groups chunked parts together).
            string uploadUuid = Guid.NewGuid().ToString();

            Uri normalizedBaseUri = new(NormalizeBaseUrl(baseUrl, 80));

            List<Uri> candidates = new();
            candidates.Add(new UriBuilder
            {
                Scheme = Uri.UriSchemeHttp,
                Host = normalizedBaseUri.Host,
                Port = SdcpWebSocketPort,
                Path = "/uploadFile/upload"
            }.Uri);
            candidates.Add(new UriBuilder
            {
                Scheme = Uri.UriSchemeHttp,
                Host = normalizedBaseUri.Host,
                Port = 80,
                Path = "/uploadFile/upload"
            }.Uri);

            List<Uri> uploadUris = candidates.Distinct().ToList();

            // Calculate chunk count — OrcaSlicer uses 1 MB chunks with metadata per chunk.
            int chunkCount = (int)((fileSize + MaxUploadChunkSize - 1) / MaxUploadChunkSize);
            if (chunkCount == 0)
            {
                chunkCount = 1;
            }

            foreach (Uri uploadUri in uploadUris)
            {
                fileContent.Position = startPosition;
                bool allChunksOk = true;

                for (int i = 0; i < chunkCount; i++)
                {
                    long offset = (long)i * MaxUploadChunkSize;
                    int length = (int)Math.Min(MaxUploadChunkSize, fileSize - offset);

                    LogSdcp(LogLevel.Debug, $"SDCP HTTP upload chunk {i + 1}/{chunkCount} (offset={offset}, length={length})");

                    fileContent.Position = startPosition + offset;

                    // Read the chunk into a memory buffer so we can wrap it in form content.
                    byte[] chunkBuffer = new byte[length];
                    int bytesRead = 0;
                    while (bytesRead < length)
                    {
                        int read = await fileContent.ReadAsync(chunkBuffer.AsMemory(bytesRead, length - bytesRead), cts.Token);
                        if (read == 0)
                        {
                            break;
                        }

                        bytesRead += read;
                    }

                    using MultipartFormDataContent formContent = new();
                    formContent.Add(new StringContent("1"), "Check");
                    formContent.Add(new StringContent(md5Hash), "S-File-MD5");
                    formContent.Add(new StringContent(offset.ToString()), "Offset");
                    formContent.Add(new StringContent(uploadUuid), "Uuid");
                    formContent.Add(new StringContent(fileSize.ToString()), "TotalSize");

                    ByteArrayContent chunkContent = new(chunkBuffer, 0, bytesRead);
                    chunkContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                    formContent.Add(chunkContent, "File", fileName);

                    try
                    {
                        using HttpResponseMessage resp = await _httpClient.PostAsync(uploadUri, formContent, cts.Token);
                        if (!resp.IsSuccessStatusCode)
                        {
                            string body = await resp.Content.ReadAsStringAsync(cts.Token);
                            LogSdcp(LogLevel.Warning, $"SDCP HTTP upload chunk {i + 1}/{chunkCount} failed: HTTP {(int)resp.StatusCode} {body}");
                            allChunksOk = false;
                            break;
                        }
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        LogSdcp(LogLevel.Debug, $"SDCP HTTP upload chunk {i + 1}/{chunkCount} failed", ex);
                        allChunksOk = false;
                        break;
                    }
                }

                if (allChunksOk)
                {
                    LogSdcp(LogLevel.Information, $"SDCP upload completed: {chunkCount} chunks, {fileSize} bytes, MD5={md5Hash}");
                    return true;
                }

                // Try next candidate URI if this one failed.
            }

            return false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogSdcp(LogLevel.Warning, "SDCP upload failed", ex);
            return false;
        }
    }

    /// <summary>
    /// Computes the MD5 hash of a stream as a lowercase hex string.
    /// MD5 is required by the SDCP upload protocol for file integrity verification — not used for security.
    /// </summary>
#pragma warning disable CA5351 // MD5 required by SDCP protocol, not used for security
    private static async Task<string> ComputeMd5Async(Stream stream, CancellationToken ct)
    {
        using MD5 md5 = MD5.Create();
        byte[] hash = await md5.ComputeHashAsync(stream, ct);
        return Convert.ToHexStringLower(hash);
    }
#pragma warning restore CA5351

    public Task<bool> UploadGcodeAsync(Uri baseUrl, string fileName, Stream fileContent, PrinterCredential? credential = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(fileContent);
        return UploadGcodeAsync(baseUrl.ToString(), fileName, fileContent, credential, ct);
    }

    /// <summary>
    /// Uploads a G-code file and starts printing it on an SDCP printer.
    /// After upload, polls the printer status until the file-checking phase completes
    /// (CurrentStatus no longer contains 8), then starts the print.
    /// This mirrors the OrcaSlicer ElegooLink implementation which uses status polling
    /// rather than file list polling to determine when the printer is ready.
    /// </summary>
    public async Task<UploadAndPrintResult> UploadAndStartPrintAsync(string baseUrl, string fileName, Stream fileContent, PrinterCredential? credential = null, IProgress<UploadAndPrintStage>? progress = null, CancellationToken ct = default)
    {
        progress?.Report(UploadAndPrintStage.Uploading);

        bool uploaded = await UploadGcodeAsync(baseUrl, fileName, fileContent, credential, ct);
        if (!uploaded)
        {
            LogSdcp(LogLevel.Warning, $"UploadAndStartPrint: upload failed for {fileName}");
            progress?.Report(UploadAndPrintStage.Failed);
            return UploadAndPrintResult.Fail(UploadAndPrintStage.Uploading, $"Failed to upload {fileName} to printer");
        }

        LogSdcp(LogLevel.Information, $"UploadAndStartPrint: upload succeeded for {fileName}, waiting for printer to finish file checking");
        progress?.Report(UploadAndPrintStage.Processing);

        // Wait 1 second before first status check (matches OrcaSlicer behavior).
        await Task.Delay(TimeSpan.FromSeconds(1), ct);

        // Poll printer status until CurrentStatus no longer contains 8 (file checking).
        // OrcaSlicer waits up to 60 seconds for this phase to complete.
        bool printerReady = await WaitForPrinterReadyAsync(baseUrl, ct);
        if (!printerReady)
        {
            LogSdcp(LogLevel.Warning, $"UploadAndStartPrint: printer still in file-checking state after {FileCheckingMaxWait.TotalSeconds}s");
            progress?.Report(UploadAndPrintStage.Failed);
            return UploadAndPrintResult.Fail(UploadAndPrintStage.Processing, $"Printer was still checking file after {FileCheckingMaxWait.TotalSeconds}s");
        }

        // Wait 1 second before sending print command (matches OrcaSlicer behavior).
        await Task.Delay(TimeSpan.FromSeconds(1), ct);

        LogSdcp(LogLevel.Information, $"UploadAndStartPrint: printer ready, starting print for {fileName}");
        progress?.Report(UploadAndPrintStage.StartingPrint);

        // Use "/local/" + filename directly — same as OrcaSlicer.
        string bareFileName = Path.GetFileName(fileName);
        bool started = await StartPrintAsync(baseUrl, $"/local/{bareFileName}", credential, ct);
        if (!started)
        {
            LogSdcp(LogLevel.Warning, $"UploadAndStartPrint: start print failed for /local/{bareFileName} after successful upload");
            progress?.Report(UploadAndPrintStage.Failed);
            return UploadAndPrintResult.Fail(UploadAndPrintStage.StartingPrint, $"Failed to start print of /local/{bareFileName} after successful upload");
        }

        progress?.Report(UploadAndPrintStage.Completed);
        return UploadAndPrintResult.Ok();
    }

    /// <summary>
    /// Maximum time to wait for the printer to finish file checking (status 8) after upload.
    /// OrcaSlicer uses 60 seconds for this check.
    /// </summary>
    private static readonly TimeSpan FileCheckingMaxWait = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Interval between status polls while waiting for file checking to complete.
    /// </summary>
    private static readonly TimeSpan FileCheckingPollInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Polls the printer's CurrentStatus array via Cmd 0 (GetStatus) until it no longer
    /// contains status code 8 (file checking). This matches the OrcaSlicer ElegooLink
    /// checkResult() behavior which waits for the printer to finish validating/indexing
    /// a newly uploaded file before attempting to start a print.
    /// Returns true when the printer is ready, false if the timeout expires.
    /// </summary>
    private async Task<bool> WaitForPrinterReadyAsync(string baseUrl, CancellationToken ct)
    {
        using CancellationTokenSource readyCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        readyCts.CancelAfter(FileCheckingMaxWait);
        int consecutiveFailures = 0;

        try
        {
            while (true)
            {
                try
                {
                    int[]? currentStatus = await GetCurrentStatusArrayAsync(baseUrl, readyCts.Token);
                    consecutiveFailures = 0;

                    if (currentStatus is null || !currentStatus.Contains(StatusFileChecking))
                    {
                        string statusStr = currentStatus is not null ? string.Join(",", currentStatus) : "null";
                        LogSdcp(LogLevel.Debug, $"Printer ready (CurrentStatus=[{statusStr}], no file-checking state)");
                        return true;
                    }

                    LogSdcp(LogLevel.Debug, $"Printer still checking file (CurrentStatus=[{string.Join(",", currentStatus)}])");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    consecutiveFailures++;
                    LogLevel level = consecutiveFailures >= 3 ? LogLevel.Warning : LogLevel.Debug;
                    LogSdcp(level, $"Status poll failed (attempt {consecutiveFailures}): {ex.Message}");
                }

                await Task.Delay(FileCheckingPollInterval, readyCts.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Timeout expired — not caller cancellation.
            return false;
        }
    }

    /// <summary>
    /// Queries the printer status via Cmd 0 and returns the raw CurrentStatus array.
    /// Returns null if the response doesn't contain a Status/CurrentStatus field.
    /// </summary>
    private async Task<int[]?> GetCurrentStatusArrayAsync(string baseUrl, CancellationToken ct)
    {
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_timeouts.CommandTimeout);
        string requestId = Guid.NewGuid().ToString("N");

        var (ws, _) = await ConnectWebSocketAsync(baseUrl, operation: "CheckFileStatus", correlationId: requestId, cts.Token);
        using (ws)
        {
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

            SdcpStatusResponse? statusResponse = await ReceiveStatusBroadcastAsync(ws, baseUrl, "CheckFileStatus", requestId, cts.Token);
            return statusResponse?.Status?.CurrentStatus;
        }
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
        bool result = await SendCommandAsync(baseUrl, SdcpCommandIds.DeleteFile, new { Url = filePath }, ct: ct);
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

                LogSdcp(LogLevel.Debug, $"SDCP history: {jobs.Count} jobs from {taskIds.Count} total");
                return new HistoryListResponse { Count = taskIds.Count, Jobs = jobs.ToArray() };
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogSdcp(LogLevel.Debug, "SDCP history list failed", ex);
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
            LogSdcp(LogLevel.Debug, $"Failed to parse history detail for {taskId}", ex);
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
            LogSdcp(LogLevel.Debug, $"SDCP history job detail failed for {jobId}", ex);
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

                LogSdcp(LogLevel.Debug, "SDCP WS attributes request sent");

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

                        LogSdcp(LogLevel.Debug, "SDCP attributes parsed");

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
                    LogSdcp(LogLevel.Debug, "SDCP received non-attributes message, reading next");
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

                LogSdcp(LogLevel.Warning, "SDCP attributes not received after multiple reads");
                return new StandardPrinterInfo();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogSdcp(LogLevel.Debug, "SDCP get printer information failed", ex);
            return new StandardPrinterInfo();
        }
    }
}

#pragma warning restore CS1066
