using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Farm.Web.Server.Services.Interfaces;

namespace Farm.Web.Server.Services;

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
    string Topic
);

public record SdcpData<T>(
    int Cmd,
    T Data,
    string RequestID,
    string MainboardID,
    long TimeStamp,
    int From = 1
);

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

public class SdcpClient : PrinterClientBase, ISdcpClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = null, // Keep original property names for SDCP
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // SDCP status codes based on documentation
    private static readonly Dictionary<int, string> StatusCodeMap = new()
    {
        { 0, "idle" },
        { 8, "preparing" },
        { 9, "starting" },
        { 10, "paused" },
        { 13, "printing" }
    };

    public async Task<PrinterStatus> GetStatusAsync(string baseUrl, CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10)); // SDCP may be slower than HTTP APIs

            var wsUrl = GetWebSocketUrl(baseUrl);
            using var ws = new ClientWebSocket();
            
            await ws.ConnectAsync(new Uri(wsUrl), cts.Token);
            
            // Send status request (Cmd: 0)
            var requestId = Guid.NewGuid().ToString("N");
            var statusRequest = new SdcpMessage<object>(
                "",
                new SdcpData<object>(
                    Cmd: 0,
                    Data: new { },
                    RequestID: requestId,
                    MainboardID: "",
                    TimeStamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                ),
                ""
            );

            var json = JsonSerializer.Serialize(statusRequest, JsonOptions);
            var bytes = Encoding.UTF8.GetBytes(json);
            await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cts.Token);

            // Read response
            var buffer = new byte[8192];
            var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
            
            if (result.MessageType == WebSocketMessageType.Text)
            {
                var responseJson = Encoding.UTF8.GetString(buffer, 0, result.Count);
                
                // Try to parse as status response
                try
                {
                    var statusResponse = JsonSerializer.Deserialize<SdcpStatusResponse>(responseJson, JsonOptions);
                    if (statusResponse?.Status?.PrintInfo != null)
                    {
                        var state = StatusCodeMap.GetValueOrDefault(statusResponse.Status.PrintInfo.Status, "unknown");
                        return new PrinterStatus(true, state);
                    }
                }
                catch
                {
                    // Might be an ACK response, still indicates printer is online
                    return new PrinterStatus(true, "online");
                }
            }

            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", cts.Token);
            return new PrinterStatus(true, "online");
        }
        catch
        {
            return new PrinterStatus(false, null);
        }
    }

    public async Task<PrinterJob> GetJobAsync(string baseUrl, CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            var wsUrl = GetWebSocketUrl(baseUrl);
            using var ws = new ClientWebSocket();
            
            await ws.ConnectAsync(new Uri(wsUrl), cts.Token);
            
            // Send status request to get print info
            var requestId = Guid.NewGuid().ToString("N");
            var statusRequest = new SdcpMessage<object>(
                "",
                new SdcpData<object>(
                    Cmd: 0,
                    Data: new { },
                    RequestID: requestId,
                    MainboardID: "",
                    TimeStamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                ),
                ""
            );

            var json = JsonSerializer.Serialize(statusRequest, JsonOptions);
            var bytes = Encoding.UTF8.GetBytes(json);
            await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cts.Token);

            // Read response
            var buffer = new byte[8192];
            var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
            
            if (result.MessageType == WebSocketMessageType.Text)
            {
                var responseJson = Encoding.UTF8.GetString(buffer, 0, result.Count);
                var statusResponse = JsonSerializer.Deserialize<SdcpStatusResponse>(responseJson, JsonOptions);
                
                if (statusResponse?.Status?.PrintInfo != null)
                {
                    var printInfo = statusResponse.Status.PrintInfo;
                    var state = StatusCodeMap.GetValueOrDefault(printInfo.Status, "unknown");
                    var progress = printInfo.Progress / 100.0; // Convert percentage to decimal
                    var jobName = string.IsNullOrWhiteSpace(printInfo.Filename) ? null : 
                                 Path.GetFileName(printInfo.Filename);

                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", cts.Token);
                    return new PrinterJob(state, progress, jobName, null);
                }
            }

            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", cts.Token);
            return new PrinterJob(null, null, null, null);
        }
        catch
        {
            return new PrinterJob(null, null, null, null);
        }
    }

    public async Task<PrinterCompositeStatus> GetCompositeStatusAsync(string baseUrl, CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            var wsUrl = GetWebSocketUrl(baseUrl);
            using var ws = new ClientWebSocket();
            
            await ws.ConnectAsync(new Uri(wsUrl), cts.Token);
            
            // Send status request
            var requestId = Guid.NewGuid().ToString("N");
            var statusRequest = new SdcpMessage<object>(
                "",
                new SdcpData<object>(
                    Cmd: 0,
                    Data: new { },
                    RequestID: requestId,
                    MainboardID: "",
                    TimeStamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                ),
                ""
            );

            var json = JsonSerializer.Serialize(statusRequest, JsonOptions);
            var bytes = Encoding.UTF8.GetBytes(json);
            await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cts.Token);

            // Read response
            var buffer = new byte[8192];
            var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
            
            if (result.MessageType == WebSocketMessageType.Text)
            {
                var responseJson = Encoding.UTF8.GetString(buffer, 0, result.Count);
                var statusResponse = JsonSerializer.Deserialize<SdcpStatusResponse>(responseJson, JsonOptions);
                
                if (statusResponse?.Status != null)
                {
                    var status = statusResponse.Status;
                    var printInfo = status.PrintInfo;
                    
                    var state = printInfo != null ? StatusCodeMap.GetValueOrDefault(printInfo.Status, "unknown") : "online";
                    var progress = printInfo?.Progress / 100.0;
                    var jobName = string.IsNullOrWhiteSpace(printInfo?.Filename) ? null : 
                                 Path.GetFileName(printInfo.Filename);
                    
                    // Parse coordinates
                    double? x = null, y = null, z = null;
                    if (!string.IsNullOrWhiteSpace(status.CurrenCoord))
                    {
                        var coords = status.CurrenCoord.Split(',');
                        if (coords.Length >= 3)
                        {
                            if (double.TryParse(coords[0], out var xVal)) x = xVal;
                            if (double.TryParse(coords[1], out var yVal)) y = yVal;
                            if (double.TryParse(coords[2], out var zVal)) z = zVal;
                        }
                    }

                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", cts.Token);
                    
                    // Get camera URLs if available
                    var cameraStreamUrl = await GetCameraUrlAsync(baseUrl, ct);
                    var cameraSnapshotUrl = await GetCameraSnapshotUrlAsync(baseUrl, ct);
                    
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
                        BedTarget: status.TempTargetHotbed
                    );
                }
            }

            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", cts.Token);
            return new PrinterCompositeStatus(true, "online", null, null, null, null, null);
        }
        catch
        {
            return new PrinterCompositeStatus(false, null, null, null, null, null, null);
        }
    }

    // Print control methods
    public async Task<bool> StartPrintAsync(string baseUrl, string filename, CancellationToken ct = default)
    {
        return await SendCommandAsync(baseUrl, 128, new { Filename = filename, StartLayer = 0, Calibration_switch = 0, PrintPlatformType = 0, Tlp_Switch = 0 }, ct);
    }

    public async Task<bool> PausePrintAsync(string baseUrl, CancellationToken ct = default)
    {
        return await SendCommandAsync(baseUrl, 129, new { }, ct);
    }

    public async Task<bool> CancelPrintAsync(string baseUrl, CancellationToken ct = default)
    {
        return await SendCommandAsync(baseUrl, 130, new { }, ct);
    }

    public async Task<bool> ResumePrintAsync(string baseUrl, CancellationToken ct = default)
    {
        return await SendCommandAsync(baseUrl, 131, new { }, ct);
    }

    // Camera control methods
    public async Task<string?> GetCameraUrlAsync(string baseUrl, CancellationToken ct = default)
    {
        try
        {
            // SDCP cameras are typically accessible via HTTP streaming
            var cameraUrl = $"http://{GetHostFromUrl(baseUrl)}:8080/video";
            
            // Test if camera stream is available
            using var httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromSeconds(5);
            
            try
            {
                var response = await httpClient.GetAsync(cameraUrl, ct);
                if (response.IsSuccessStatusCode)
                {
                    return cameraUrl;
                }
            }
            catch
            {
                // Try alternative port
                cameraUrl = $"http://{GetHostFromUrl(baseUrl)}:3030/video";
                var response = await httpClient.GetAsync(cameraUrl, ct);
                if (response.IsSuccessStatusCode)
                {
                    return cameraUrl;
                }
            }
        }
        catch
        {
            // Camera not available
        }
        return null;
    }

    public async Task<string?> GetCameraSnapshotUrlAsync(string baseUrl, CancellationToken ct = default)
    {
        try
        {
            // SDCP camera snapshots are typically available via HTTP
            var snapshotUrl = $"http://{GetHostFromUrl(baseUrl)}:8080/snapshot";
            
            // Test if snapshot endpoint is available
            using var httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromSeconds(5);
            
            try
            {
                var response = await httpClient.GetAsync(snapshotUrl, ct);
                if (response.IsSuccessStatusCode)
                {
                    return snapshotUrl;
                }
            }
            catch
            {
                // Try alternative port
                snapshotUrl = $"http://{GetHostFromUrl(baseUrl)}:3030/snapshot";
                var response = await httpClient.GetAsync(snapshotUrl, ct);
                if (response.IsSuccessStatusCode)
                {
                    return snapshotUrl;
                }
            }
        }
        catch
        {
            // Snapshot not available
        }
        return null;
    }

    public async Task<bool> EnableCameraAsync(string baseUrl, CancellationToken ct = default)
    {
        return await SendCommandAsync(baseUrl, 386, new { Enable = 1 }, ct);
    }

    public async Task<bool> DisableCameraAsync(string baseUrl, CancellationToken ct = default)
    {
        return await SendCommandAsync(baseUrl, 386, new { Enable = 0 }, ct);
    }

    // File management methods
    public async Task<string[]> GetFileListAsync(string baseUrl, CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            var wsUrl = GetWebSocketUrl(baseUrl);
            using var ws = new ClientWebSocket();
            
            await ws.ConnectAsync(new Uri(wsUrl), cts.Token);
            
            // Send file list request (Cmd: 258)
            var requestId = Guid.NewGuid().ToString("N");
            var fileListRequest = new SdcpMessage<object>(
                "",
                new SdcpData<object>(
                    Cmd: 258,
                    Data: new { },
                    RequestID: requestId,
                    MainboardID: "",
                    TimeStamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                ),
                ""
            );

            var json = JsonSerializer.Serialize(fileListRequest, JsonOptions);
            var bytes = Encoding.UTF8.GetBytes(json);
            await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cts.Token);

            // Read response
            var buffer = new byte[8192];
            var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
            
            if (result.MessageType == WebSocketMessageType.Text)
            {
                var responseJson = Encoding.UTF8.GetString(buffer, 0, result.Count);
                // Parse file list response and return filenames
                // This would need to be implemented based on the actual SDCP file list response format
                return new[] { "placeholder.gcode" }; // Placeholder implementation
            }

            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", cts.Token);
            return Array.Empty<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private async Task<bool> SendCommandAsync<T>(string baseUrl, int cmd, T data, CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            var wsUrl = GetWebSocketUrl(baseUrl);
            using var ws = new ClientWebSocket();
            
            await ws.ConnectAsync(new Uri(wsUrl), cts.Token);
            
            var requestId = Guid.NewGuid().ToString("N");
            var command = new SdcpMessage<T>(
                "",
                new SdcpData<T>(
                    Cmd: cmd,
                    Data: data,
                    RequestID: requestId,
                    MainboardID: "",
                    TimeStamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                ),
                ""
            );

            var json = JsonSerializer.Serialize(command, JsonOptions);
            var bytes = Encoding.UTF8.GetBytes(json);
            await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cts.Token);

            // Read ACK response
            var buffer = new byte[4096];
            var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
            
            if (result.MessageType == WebSocketMessageType.Text)
            {
                var responseJson = Encoding.UTF8.GetString(buffer, 0, result.Count);
                var ackResponse = JsonSerializer.Deserialize<SdcpAckResponse>(responseJson, JsonOptions);
                
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", cts.Token);
                
                // ACK code 0 = success, anything else is error
                return ackResponse?.Data?.Data?.Ack == 0;
            }

            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", cts.Token);
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static string GetWebSocketUrl(string baseUrl)
    {
        // Convert HTTP(S) URL to WebSocket URL
        // SDCP WebSocket is available at ws://ip/websocket
        var normalizedUrl = NormalizeBaseUrl(baseUrl, 80);
        var uri = new Uri(normalizedUrl);
        var wsScheme = uri.Scheme == "https" ? "wss" : "ws";
        return $"{wsScheme}://{uri.Host}:{uri.Port}/websocket";
    }

    // File upload and management methods
    public async Task<bool> UploadGcodeAsync(string baseUrl, string fileName, Stream fileContent, CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(60)); // Allow more time for file uploads
            
            // SDCP file upload is typically done via HTTP POST to a specific endpoint
            // This implementation assumes a standard HTTP file upload endpoint
            var host = GetHostFromUrl(baseUrl);
            var uploadUrl = $"http://{host}/api/upload"; // Common SDCP upload endpoint
            
            using var httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromSeconds(60);
            
            using var formContent = new MultipartFormDataContent();
            using var streamContent = new StreamContent(fileContent);
            streamContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            formContent.Add(streamContent, "file", fileName);
            
            using var resp = await httpClient.PostAsync(uploadUrl, formContent, cts.Token);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    // The existing StartPrintAsync method already handles starting prints for SDCP
    // GetFileListAsync method already exists for SDCP

    private static string GetHostFromUrl(string baseUrl)
    {
        var normalizedUrl = NormalizeBaseUrl(baseUrl, 80);
        var uri = new Uri(normalizedUrl);
        return uri.Host;
    }

    private static string NormalizeBaseUrl(string url) => NormalizeBaseUrl(url, 80);

    public void Dispose()
    {
        // No resources to dispose in this implementation
    }
}
