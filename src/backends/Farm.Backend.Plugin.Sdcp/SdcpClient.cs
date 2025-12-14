using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Telemetry;
using Farm.Infrastructure.Services.Printers;

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


public sealed class SdcpClient : PrinterClientBase, ISdcpClient,
    ISupportsFileList,
    ISupportsCamera
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IUnifiedLoggingService _logger;

    // Adapter to allow construction with a single HttpClient instance when the
    // typed HttpClient activator attempts to construct SdcpClient(HttpClient, ...)
    private sealed class SingleClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;
        public SingleClientFactory(HttpClient client) => _client = client ?? throw new ArgumentNullException(nameof(client));
        public HttpClient CreateClient(string name) => _client;
    }

    // Primary constructor used by DI when an IHttpClientFactory is available
    public SdcpClient(IHttpClientFactory httpClientFactory, IUnifiedLoggingService logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    // Constructor used by AddHttpClient's typed client factory which provides HttpClient
    public SdcpClient(HttpClient httpClient, IUnifiedLoggingService logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _httpClientFactory = new SingleClientFactory(httpClient);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }
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
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10)); // SDCP may be slower than HTTP APIs

            string wsUrl = GetWebSocketUrl(baseUrl);
            using ClientWebSocket ws = new();

            await ws.ConnectAsync(new Uri(wsUrl), cts.Token);

            // Send status request (Cmd: 0)
            string requestId = Guid.NewGuid().ToString("N");
            SdcpMessage<object> statusRequest = new(
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

            string json = JsonSerializer.Serialize(statusRequest, JsonOptions);
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cts.Token);

            // Read response
            byte[] buffer = new byte[8192];
            WebSocketReceiveResult result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);

            if (result.MessageType == WebSocketMessageType.Text)
            {
                string responseJson = Encoding.UTF8.GetString(buffer, 0, result.Count);

                // Try to parse as status response
                try
                {
                    SdcpStatusResponse? statusResponse = JsonSerializer.Deserialize<SdcpStatusResponse>(responseJson, JsonOptions);
                    if (statusResponse?.Status?.PrintInfo != null)
                    {
                        string state = StatusCodeMap.GetValueOrDefault(statusResponse.Status.PrintInfo.Status, "unknown");
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
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            string wsUrl = GetWebSocketUrl(baseUrl);
            using ClientWebSocket ws = new();

            await ws.ConnectAsync(new Uri(wsUrl), cts.Token);

            // Send status request to get print info
            string requestId = Guid.NewGuid().ToString("N");
            SdcpMessage<object> statusRequest = new(
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

            string json = JsonSerializer.Serialize(statusRequest, JsonOptions);
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cts.Token);

            // Read response
            byte[] buffer = new byte[8192];
            WebSocketReceiveResult result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);

            if (result.MessageType == WebSocketMessageType.Text)
            {
                string responseJson = Encoding.UTF8.GetString(buffer, 0, result.Count);
                SdcpStatusResponse? statusResponse = JsonSerializer.Deserialize<SdcpStatusResponse>(responseJson, JsonOptions);

                if (statusResponse?.Status?.PrintInfo != null)
                {
                    SdcpPrintInfo printInfo = statusResponse.Status.PrintInfo;
                    string state = StatusCodeMap.GetValueOrDefault(printInfo.Status, "unknown");
                    double progress = printInfo.Progress / 100.0; // Convert percentage to decimal
                    string? jobName = string.IsNullOrWhiteSpace(printInfo.Filename) ? null :
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
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            string wsUrl = GetWebSocketUrl(baseUrl);
            using ClientWebSocket ws = new();

            await ws.ConnectAsync(new Uri(wsUrl), cts.Token);

            // Send status request
            string requestId = Guid.NewGuid().ToString("N");
            SdcpMessage<object> statusRequest = new(
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

            string json = JsonSerializer.Serialize(statusRequest, JsonOptions);
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cts.Token);

            // Read response
            byte[] buffer = new byte[8192];
            WebSocketReceiveResult result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);

            if (result.MessageType == WebSocketMessageType.Text)
            {
                string responseJson = Encoding.UTF8.GetString(buffer, 0, result.Count);
                SdcpStatusResponse? statusResponse = JsonSerializer.Deserialize<SdcpStatusResponse>(responseJson, JsonOptions);

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

                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", cts.Token);

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
            ServerUrl: printer.ServerUrl,
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
            OriginalServerUrl: printer.OriginalServerUrl,
            IpAddress: printer.IpAddress,
            BackendPort: printer.BackendPort,
            FrontendPort: printer.FrontendPort
        );
    }

    // Print control methods
    public async Task<bool> StartPrintAsync(string baseUrl, string filename, CancellationToken ct = default)
    {
        return await SendCommandAsync(baseUrl, 128, new { Filename = filename, StartLayer = 0, Calibration_switch = 0, PrintPlatformType = 0, Tlp_Switch = 0 }, ct);
    }

    public Task<bool> StartPrintAsync(Uri baseUrl, string filename, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(filename);
        return StartPrintAsync(baseUrl.ToString(), filename, ct);
    }

    public async Task<bool> PausePrintAsync(string baseUrl, CancellationToken ct = default)
    {
        return await SendCommandAsync(baseUrl, 129, new { }, ct);
    }

    public Task<bool> PausePrintAsync(Uri baseUrl, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return PausePrintAsync(baseUrl.ToString(), ct);
    }

    public async Task<bool> CancelPrintAsync(string baseUrl, CancellationToken ct = default)
    {
        return await SendCommandAsync(baseUrl, 130, new { }, ct);
    }

    public Task<bool> CancelPrintAsync(Uri baseUrl, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return CancelPrintAsync(baseUrl.ToString(), ct);
    }

    public async Task<bool> ResumePrintAsync(string baseUrl, CancellationToken ct = default)
    {
        return await SendCommandAsync(baseUrl, 131, new { }, ct);
    }

    public Task<bool> ResumePrintAsync(Uri baseUrl, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return ResumePrintAsync(baseUrl.ToString(), ct);
    }

    // Camera control methods
    public async Task<string?> GetCameraUrlAsync(string baseUrl, CancellationToken ct = default)
    {
        try
        {
            // SDCP cameras are typically accessible via HTTP streaming
            Uri baseUri = new(NormalizeBaseUrl(baseUrl, 80));
            Uri cameraUri = new UriBuilder
            {
                Scheme = Uri.UriSchemeHttp,
                Host = baseUri.Host,
                Port = 8080,
                Path = "/video"
            }.Uri;

            // Test if camera stream is available
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            try
            {
                using HttpClient client = _httpClientFactory.CreateClient();
                using HttpResponseMessage response = await client.GetAsync(cameraUri, cts.Token);
                if (response.IsSuccessStatusCode)
                {
                    return cameraUri.ToString();
                }
            }
            catch (HttpRequestException)
            {
                // Try alternative port
                cameraUri = new UriBuilder
                {
                    Scheme = Uri.UriSchemeHttp,
                    Host = baseUri.Host,
                    Port = 3030,
                    Path = "/video"
                }.Uri;
                using HttpClient client = _httpClientFactory.CreateClient();
                using HttpResponseMessage response = await client.GetAsync(cameraUri, cts.Token);
                if (response.IsSuccessStatusCode)
                {
                    return cameraUri.ToString();
                }
            }
            catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
            {
                // Timeout trying first port, try alternative port
                cameraUri = new UriBuilder
                {
                    Scheme = Uri.UriSchemeHttp,
                    Host = baseUri.Host,
                    Port = 3030,
                    Path = "/video"
                }.Uri;
                using HttpClient client = _httpClientFactory.CreateClient();
                using HttpResponseMessage response = await client.GetAsync(cameraUri, cts.Token);
                if (response.IsSuccessStatusCode)
                {
                    return cameraUri.ToString();
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Expected when cancellation is requested
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to get camera URL for {BaseUrl}", baseUrl);
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
            Uri snapshotUri = new UriBuilder
            {
                Scheme = Uri.UriSchemeHttp,
                Host = baseUri.Host,
                Port = 8080,
                Path = "/snapshot"
            }.Uri;

            // Test if snapshot endpoint is available
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            try
            {
                using HttpClient client = _httpClientFactory.CreateClient();
                using HttpResponseMessage response = await client.GetAsync(snapshotUri, cts.Token);
                if (response.IsSuccessStatusCode)
                {
                    return snapshotUri.ToString();
                }
            }
            catch (HttpRequestException)
            {
                // Try alternative port
                snapshotUri = new UriBuilder
                {
                    Scheme = Uri.UriSchemeHttp,
                    Host = baseUri.Host,
                    Port = 3030,
                    Path = "/snapshot"
                }.Uri;
                using HttpClient client = _httpClientFactory.CreateClient();
                using HttpResponseMessage response = await client.GetAsync(snapshotUri, cts.Token);
                if (response.IsSuccessStatusCode)
                {
                    return snapshotUri.ToString();
                }
            }
            catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
            {
                // Timeout trying first port, try alternative port
                snapshotUri = new UriBuilder
                {
                    Scheme = Uri.UriSchemeHttp,
                    Host = baseUri.Host,
                    Port = 3030,
                    Path = "/snapshot"
                }.Uri;
                using HttpClient client = _httpClientFactory.CreateClient();
                using HttpResponseMessage response = await client.GetAsync(snapshotUri, cts.Token);
                if (response.IsSuccessStatusCode)
                {
                    return snapshotUri.ToString();
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Expected when cancellation is requested
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to get camera snapshot URL for {BaseUrl}", baseUrl);
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
        return await SendCommandAsync(baseUrl, 386, new { Enable = 1 }, ct);
    }

    public Task<bool> EnableCameraAsync(Uri baseUrl, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return EnableCameraAsync(baseUrl.ToString(), ct);
    }

    public async Task<bool> DisableCameraAsync(string baseUrl, CancellationToken ct = default)
    {
        return await SendCommandAsync(baseUrl, 386, new { Enable = 0 }, ct);
    }

    public Task<bool> DisableCameraAsync(Uri baseUrl, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return DisableCameraAsync(baseUrl.ToString(), ct);
    }

    // File management methods
    public async Task<string[]> GetFileListAsync(string baseUrl, CancellationToken ct = default)
    {
        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            string wsUrl = GetWebSocketUrl(baseUrl);
            using ClientWebSocket ws = new();

            await ws.ConnectAsync(new Uri(wsUrl), cts.Token);

            // Send file list request (Cmd: 258)
            string requestId = Guid.NewGuid().ToString("N");
            SdcpMessage<object> fileListRequest = new(
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

            string json = JsonSerializer.Serialize(fileListRequest, JsonOptions);
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cts.Token);

            // Read response
            byte[] buffer = new byte[8192];
            WebSocketReceiveResult result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);

            if (result.MessageType == WebSocketMessageType.Text)
            {
                // Parse file list response and return filenames
                // This would need to be implemented based on the actual SDCP file list response format
                return ["placeholder.gcode"]; // Placeholder implementation
            }

            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", cts.Token);
            return [];
        }
        catch
        {
            return [];
        }
    }

    public Task<string[]> GetFileListAsync(Uri baseUrl, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return GetFileListAsync(baseUrl.ToString(), ct);
    }

    private static async Task<bool> SendCommandAsync<T>(string baseUrl, int cmd, T data, CancellationToken ct = default)
    {
        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            string wsUrl = GetWebSocketUrl(baseUrl);
            using ClientWebSocket ws = new();

            await ws.ConnectAsync(new Uri(wsUrl), cts.Token);

            string requestId = Guid.NewGuid().ToString("N");
            SdcpMessage<T> command = new(
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

            string json = JsonSerializer.Serialize(command, JsonOptions);
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cts.Token);

            // Read ACK response
            byte[] buffer = new byte[4096];
            WebSocketReceiveResult result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);

            if (result.MessageType == WebSocketMessageType.Text)
            {
                string responseJson = Encoding.UTF8.GetString(buffer, 0, result.Count);
                SdcpAckResponse? ackResponse = JsonSerializer.Deserialize<SdcpAckResponse>(responseJson, JsonOptions);

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
        string normalizedUrl = NormalizeBaseUrl(baseUrl, 80);
        Uri uri = new(normalizedUrl);
        bool isSecure = string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        UriBuilder ub = new()
        {
            Scheme = isSecure ? "wss" : "ws",
            Host = uri.Host,
            Port = uri.Port,
            Path = "/websocket"
        };
        return ub.Uri.ToString();
    }

    // File upload and management methods
    public async Task<bool> UploadGcodeAsync(string baseUrl, string fileName, Stream fileContent, CancellationToken ct = default)
    {
        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(60)); // Allow more time for file uploads

            // SDCP file upload is typically done via HTTP POST to a specific endpoint
            // This implementation assumes a standard HTTP file upload endpoint
            string host = GetHostFromUrl(baseUrl);
            Uri uploadUri = new UriBuilder
            {
                Scheme = Uri.UriSchemeHttp,
                Host = host,
                Port = -1, // preserve default port formatting (no explicit port)
                Path = "/api/upload"
            }.Uri; // Common SDCP upload endpoint

            using MultipartFormDataContent formContent = new();
            using StreamContent streamContent = new(fileContent);
            streamContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            formContent.Add(streamContent, "file", fileName);

            using HttpClient client = _httpClientFactory.CreateClient();
            using HttpResponseMessage resp = await client.PostAsync(uploadUri, formContent, cts.Token);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public Task<bool> UploadGcodeAsync(Uri baseUrl, string fileName, Stream fileContent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(fileContent);
        return UploadGcodeAsync(baseUrl.ToString(), fileName, fileContent, ct);
    }

    // The existing StartPrintAsync method already handles starting prints for SDCP
    // GetFileListAsync method already exists for SDCP

    private static string GetHostFromUrl(string baseUrl)
    {
        string normalizedUrl = NormalizeBaseUrl(baseUrl, 80);
        Uri uri = new(normalizedUrl);
        return uri.Host;
    }

    public void Dispose()
    {
        // No resources to dispose in this implementation
    }

    // ========== CAPABILITY INTERFACE IMPLEMENTATIONS ==========

    async Task<List<PrinterFileInfo>> ISupportsFileList.GetFileListAsync(string baseUrl, string? apiKey = null, CancellationToken ct = default)
    {
        var files = await GetFileListAsync(baseUrl, ct);
        return files?.Select(f => new PrinterFileInfo { Name = f, Path = f }).ToList() ?? new();
    }

    Task<string?> ISupportsCamera.GetCameraStreamUrlAsync(string baseUrl, int? frontendPort = null, string? apiKey = null, CancellationToken ct = default)
    {
        // SDCP doesn't expose camera URLs directly - would need implementation in derived SDCP support
        return Task.FromResult<string?>(null);
    }

    Task<string?> ISupportsCamera.GetCameraSnapshotUrlAsync(string baseUrl, int? frontendPort = null, string? apiKey = null, CancellationToken ct = default)
    {
        // SDCP doesn't expose camera URLs directly - would need implementation in derived SDCP support
        return Task.FromResult<string?>(null);
    }
}

#pragma warning restore CS1066
