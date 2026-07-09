#pragma warning disable CS1066, S1006 // Default parameters in explicit interface implementations are architecturally intentional

using System.IO;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Farm.Infrastructure;
using Farm.Infrastructure.Contracts.Printers.Moonraker;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Settings;
using Microsoft.Extensions.Logging;

namespace Farm.Backend.Plugin.Moonraker;

public class MoonrakerClient(HttpClient http, ILogger<MoonrakerClient> logger, BackendTimeoutSettings timeouts) : PrinterClientBase, IMoonrakerClient,
    ISupportsFileDownload,
    ISupportsFileList,
    ISupportsFileUpload,
    ISupportsFileDelete,
    ISupportsStartPrint,
    ISupportsUploadAndPrint,
    ISupportsControlOperations,
    ISupportsCamera,
    ISupportsConfiguredCameraDetection,
    ISupportsFileMetadata,
    ISupportsMovement,
    ISupportsTemperatureControl,
    ISupportsPrinterInformation,
    ISupportsHistory,
    ISupportsFilamentControl,
    ISupportsSpoolman,
    ISupportsStatus,
    ISupportsCompositeStatus,
    ISupportsControlRestart,
    ISupportsGcodeExecution,
    ISupportsObjectExclusion,
    ISupportsFilamentUsageQuery,
    ISupportsPerExtruderFilamentUsage
{
    private const int MaxExcludeObjectNameLength = 256;

    private readonly HttpClient _http = http;
    private readonly ILogger<MoonrakerClient> _logger = logger;
    private readonly BackendTimeoutSettings _timeouts = timeouts;
    private static readonly Regex SafeUnquotedObjectNamePattern = new(@"^[A-Za-z0-9_.:+/@-]+$", RegexOptions.Compiled);

    public async Task<PrinterStatus> GetStatusAsync(string baseUrl, CancellationToken ct = default)
    {
        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeouts.StatusPollTimeout);
            Uri baseUri = new(baseUrl);
            Uri uri = new(baseUri, "printer/info");
            _logger.LogDebug("[Moonraker] Querying status at: {Uri}", uri);
            using HttpResponseMessage resp = await _http.GetAsync(uri, cts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogDebug("[Moonraker] Status query failed with status {RespStatusCode} at {Uri}", resp.StatusCode, uri);
                return new PrinterStatus(false, null);
            }

            await using Stream stream = await resp.Content.ReadAsStreamAsync(cts.Token);
            using JsonDocument doc = await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token);
            string? state = null;
            JsonElement root = doc.RootElement;
            if (root.TryGetProperty("state", out JsonElement s1) && s1.ValueKind == JsonValueKind.String)
            {
                state = s1.GetString();
            }
            else if (root.TryGetProperty("result", out JsonElement result) && result.ValueKind == JsonValueKind.Object &&
                     result.TryGetProperty("state", out JsonElement s2) && s2.ValueKind == JsonValueKind.String)
            {
                state = s2.GetString();
            }

            _logger.LogDebug("[Moonraker] Status retrieved: state={State}", state);
            return new PrinterStatus(true, state);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Expected when cancellation is requested
            throw;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "[Moonraker] HTTP request failed for {BaseUrl}: {Message}", baseUrl, ex.Message);
            return new PrinterStatus(false, null);
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            _logger.LogDebug(ex, "[Moonraker] Timeout getting status from {BaseUrl}", baseUrl);
            return new PrinterStatus(false, null);
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "[Moonraker] JSON parse error from {BaseUrl}: {Message}", baseUrl, ex.Message);
            return new PrinterStatus(false, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "[Moonraker] Unexpected error getting status from {BaseUrl}: {Name}: {Message}", baseUrl, ex.GetType().Name, ex.Message);
            return new PrinterStatus(false, null);
        }
    }

    public async Task<MoonrakerPrinterInfo?> GetPrinterInfoAsync(string baseUrl, CancellationToken ct = default)
    {
        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeouts.StatusPollTimeout);
            Uri baseUri = new(baseUrl);
            Uri uri = new(baseUri, "printer/info");
            using HttpResponseMessage resp = await _http.GetAsync(uri, cts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                return null;
            }

            await using Stream stream = await resp.Content.ReadAsStreamAsync(cts.Token);
            using JsonDocument doc = await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token);
            JsonElement root = doc.RootElement;

            // Handle both direct response and wrapped response
            JsonElement infoElement = root;
            if (root.TryGetProperty("result", out JsonElement result) && result.ValueKind == JsonValueKind.Object)
            {
                infoElement = result;
            }

            return JsonSerializer.Deserialize<MoonrakerPrinterInfo>(infoElement.GetRawText());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Expected when cancellation is requested
            throw;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "Failed to get printer info from {BaseUrl}", baseUrl);
            return null;
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            _logger.LogDebug(ex, "Failed to get printer info from {BaseUrl}", baseUrl);
            return null;
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "Failed to get printer info from {BaseUrl}", baseUrl);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to get printer info from {BaseUrl}", baseUrl);
            return null;
        }
    }

    public async Task<PrinterJob?> GetJobAsync(string baseUrl, CancellationToken ct = default)
    {
        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeouts.StatusPollTimeout);
            Uri baseUri = new(baseUrl);
            Uri uri = new(baseUri, "printer/objects/query?print_stats&display_status&job_queue");
            using HttpResponseMessage resp = await _http.GetAsync(uri, cts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                return null;
            }

            await using Stream stream = await resp.Content.ReadAsStreamAsync(cts.Token);
            using JsonDocument doc = await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token);
            JsonElement root = doc.RootElement;
            if (!root.TryGetProperty("result", out JsonElement result))
            {
                return null;
            }

            string? state = null;
            if (result.TryGetProperty("status", out JsonElement statusNode) &&
                statusNode.ValueKind == JsonValueKind.Object &&
                statusNode.TryGetProperty("print_stats", out JsonElement psNode) &&
                psNode.ValueKind == JsonValueKind.Object &&
                psNode.TryGetProperty("state", out JsonElement stNode) &&
                stNode.ValueKind == JsonValueKind.String)
            {
                state = stNode.GetString();
            }

            // Only report job details when printing
            if (!string.Equals(state, "printing", StringComparison.OrdinalIgnoreCase))
            {
                return new PrinterJob(state, null, null, null);
            }

            double? progress = null;
            string? jobName = null;
            string? thumb = null;
            double? printDuration = null;

            if (result.TryGetProperty("status", out JsonElement statusEl))
            {
                if (statusEl.TryGetProperty("display_status", out JsonElement display) &&
                    display.TryGetProperty("progress", out JsonElement prog))
                {
                    double pv = 0;
                    try
                    {
                        pv = prog.GetDouble();
                    }
                    catch
                    {
                    }

                    progress = pv > 1.0 ? pv : pv * 100.0; // support 0..1 or 0..100
                }

                if (statusEl.TryGetProperty("print_stats", out JsonElement ps))
                {
                    if (ps.TryGetProperty("filename", out JsonElement fn) && fn.ValueKind == JsonValueKind.String)
                    {
                        jobName = fn.GetString();
                    }

                    if (ps.TryGetProperty("print_duration", out JsonElement pd) && pd.ValueKind == JsonValueKind.Number)
                    {
                        try
                        {
                            printDuration = pd.GetDouble();
                        }
                        catch
                        {
                        }
                    }
                }
            }

            // Try Klipper job queue for thumbnail path
            if (result.TryGetProperty("job_queue", out JsonElement jq) && jq.ValueKind == JsonValueKind.Object &&
                jq.TryGetProperty("thumbnails", out JsonElement thumbs) && thumbs.ValueKind == JsonValueKind.Array && thumbs.GetArrayLength() > 0)
            {
                JsonElement first = thumbs[0];
                if (first.TryGetProperty("relative_path", out JsonElement rp) && rp.ValueKind == JsonValueKind.String)
                {
                    Uri baseUri2 = new(baseUrl);
                    string relPath = Uri.EscapeDataString(rp.GetString()!);
                    Uri thumbUri = new(baseUri2, $"server/files/gcodes/{relPath}");
                    thumb = thumbUri.ToString();
                }
            }

            // Fallback: query file metadata for thumbnails if not found yet
            if (thumb is null && !string.IsNullOrWhiteSpace(jobName))
            {
                try
                {
                    Uri baseUri3 = new(baseUrl);
                    Uri metaUri = new(baseUri3, $"server/files/metadata?filename={Uri.EscapeDataString(jobName)}");
                    using HttpResponseMessage mresp = await _http.GetAsync(metaUri, cts.Token);
                    if (mresp.IsSuccessStatusCode)
                    {
                        await using Stream mstream = await mresp.Content.ReadAsStreamAsync(cts.Token);
                        using JsonDocument mdoc = await JsonDocument.ParseAsync(mstream, cancellationToken: cts.Token);
                        JsonElement mroot = mdoc.RootElement;
                        if (mroot.TryGetProperty("result", out JsonElement mres) &&
                            mres.TryGetProperty("thumbnails", out JsonElement mthumbs) &&
                            mthumbs.ValueKind == JsonValueKind.Array && mthumbs.GetArrayLength() > 0)
                        {
                            JsonElement first = mthumbs[0];
                            if (first.TryGetProperty("relative_path", out JsonElement rp) && rp.ValueKind == JsonValueKind.String)
                            {
                                Uri baseUriX = new(baseUrl);
                                Uri thumbUri2 = new(baseUriX, $"server/files/gcodes/{Uri.EscapeDataString(rp.GetString()!)}");
                                thumb = thumbUri2.ToString();
                            }
                        }
                    }
                }
                catch
                {
                }
            }

            return new PrinterJob(state, progress, jobName, thumb, printDuration);
        }
        catch
        {
            return null;
        }
    }

    public Task<string?> GetCameraStreamUrlAsync(string baseUrl, int? frontendPort = null, CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                return Task.FromResult<string?>(null);
            }

            Uri baseUri = new(baseUrl);
            int port = frontendPort ?? (baseUri.Scheme == "https" ? 443 : 80);

            UriBuilder builder = new(baseUri)
            {
                Port = port,
                Path = "/webcam/",
                Query = "action=stream"
            };

            return Task.FromResult<string?>(builder.Uri.ToString());
        }
        catch
        {
            return Task.FromResult<string?>(null);
        }
    }

    public Task<string?> GetCameraStreamUrlAsync(Uri baseUrl, int? frontendPort = null, CancellationToken ct = default)
    {
        return GetCameraStreamUrlAsync(baseUrl.ToString(), frontendPort, ct);
    }

    public Task<string?> GetCameraSnapshotUrlAsync(string baseUrl, int? frontendPort = null, CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                return Task.FromResult<string?>(null);
            }

            Uri baseUri = new(baseUrl);
            int port = frontendPort ?? (baseUri.Scheme == "https" ? 443 : 80);

            UriBuilder builder = new(baseUri)
            {
                Port = port,
                Path = "/webcam/",
                Query = "action=snapshot"
            };

            return Task.FromResult<string?>(builder.Uri.ToString());
        }
        catch
        {
            return Task.FromResult<string?>(null);
        }
    }

    public Task<string?> GetCameraSnapshotUrlAsync(Uri baseUrl, int? frontendPort = null, CancellationToken ct = default)
    {
        return GetCameraSnapshotUrlAsync(baseUrl.ToString(), frontendPort, ct);
    }

    public async Task<byte[]?> GetCameraSnapshotAsync(string baseUrl, CancellationToken ct = default)
    {
        try
        {
            string? url = await GetCameraSnapshotUrlAsync(baseUrl, null, ct);
            if (string.IsNullOrWhiteSpace(url))
            {
                return null;
            }

            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeouts.StatusPollTimeout);
            using HttpResponseMessage resp = await _http.GetAsync(new Uri(url!, UriKind.RelativeOrAbsolute), cts.Token);
            return !resp.IsSuccessStatusCode ? null : await resp.Content.ReadAsByteArrayAsync(cts.Token);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Queries the Moonraker API for actual configured camera URLs.
    /// Returns the first enabled camera's stream and snapshot URLs from the /server/webcams/list API.
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server.</param>
    /// <param name="frontendPort">The optional frontend port to use for camera URLs.</param>
    /// <param name="ct">Cancellation token to cancel the operation.</param>
    public async Task<(string? StreamUrl, string? SnapshotUrl)> GetConfiguredCameraUrlsAsync(string baseUrl, int? frontendPort = null, CancellationToken ct = default)
    {
        try
        {
            // Get the raw camera URLs from the API (which handles relative URL resolution)
            (string? stream, string? snapshot) = await GetCameraUrlsAsync(baseUrl, ct);

            // If we got URLs from the API, they should already be normalized
            // But we can optionally apply frontendPort if it differs from what the API returned
            // For now, just return what the API provided
            return (stream, snapshot);
        }
        catch
        {
            return (null, null);
        }
    }

    public async Task<PrinterCompositeStatus> GetCompositeStatusAsync(string baseUrl, CancellationToken ct = default)
    {
        _logger.LogDebug("[Moonraker] GetCompositeStatusAsync: baseUrl={BaseUrl}", baseUrl);
        PrinterStatus status = await GetStatusAsync(baseUrl, ct);
        _logger.LogDebug("[Moonraker] GetCompositeStatusAsync: status.IsOnline={StatusIsOnline}, status.State={StatusState}", status.IsOnline, status.State);
        PrinterJob? job = await GetJobAsync(baseUrl, ct);

        // Try to read current position
        double? x = null, y = null, z = null;
        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeouts.StatusPollTimeout);
            Uri baseUri = new(baseUrl);
            Uri posUri = new(baseUri, "printer/objects/query?toolhead=position");
            using HttpResponseMessage resp = await _http.GetAsync(posUri, cts.Token);
            if (resp.IsSuccessStatusCode)
            {
                await using Stream stream = await resp.Content.ReadAsStreamAsync(cts.Token);
                using JsonDocument doc = await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token);
                JsonElement root = doc.RootElement;
                if (root.TryGetProperty("result", out JsonElement result) &&
                    result.TryGetProperty("status", out JsonElement statusNode) &&
                    statusNode.TryGetProperty("toolhead", out JsonElement th) &&
                    th.TryGetProperty("position", out JsonElement pos) && pos.ValueKind == JsonValueKind.Array && pos.GetArrayLength() >= 3)
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
            }
        }
        catch
        {
        }

        // Prefer print job state (printing, paused, complete) over system state, but not for error states
        // If system is shutdown/error, that takes precedence over print_stats state
        string? state = null;
        if (!string.IsNullOrEmpty(status.State) &&
            (status.State.Equals("shutdown", StringComparison.OrdinalIgnoreCase) ||
             status.State.Equals("error", StringComparison.OrdinalIgnoreCase)))
        {
            // System is in error state, use system state
            state = status.State;
        }
        else
        {
            // Otherwise prefer job state if available
            state = job?.PrintState ?? status.State;
        }

        // Query temps
        double? hotend = null, bed = null, hotendT = null, bedT = null;
        try
        {
            using CancellationTokenSource cts2 = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts2.CancelAfter(_timeouts.StatusPollTimeout);
            Uri baseUri2 = new(baseUrl);
            Uri tempsUri = new(baseUri2, "printer/objects/query?extruder&heater_bed");
            using HttpResponseMessage resp2 = await _http.GetAsync(tempsUri, cts2.Token);
            if (resp2.IsSuccessStatusCode)
            {
                await using Stream stream2 = await resp2.Content.ReadAsStreamAsync(cts2.Token);
                using JsonDocument doc2 = await JsonDocument.ParseAsync(stream2, cancellationToken: cts2.Token);
                JsonElement root2 = doc2.RootElement;
                if (root2.TryGetProperty("result", out JsonElement result2) && result2.TryGetProperty("status", out JsonElement status2))
                {
                    if (status2.TryGetProperty("extruder", out JsonElement ex))
                    {
                        if (ex.TryGetProperty("temperature", out JsonElement t) && t.ValueKind is JsonValueKind.Number)
                        {
                            try
                            {
                                hotend = t.GetDouble();
                            }
                            catch
                            {
                            }
                        }

                        if (ex.TryGetProperty("target", out JsonElement tt) && tt.ValueKind is JsonValueKind.Number)
                        {
                            try
                            {
                                hotendT = tt.GetDouble();
                            }
                            catch
                            {
                            }
                        }
                    }

                    if (status2.TryGetProperty("heater_bed", out JsonElement hb))
                    {
                        if (hb.TryGetProperty("temperature", out JsonElement t) && t.ValueKind is JsonValueKind.Number)
                        {
                            try
                            {
                                bed = t.GetDouble();
                            }
                            catch
                            {
                            }
                        }

                        if (hb.TryGetProperty("target", out JsonElement tt) && tt.ValueKind is JsonValueKind.Number)
                        {
                            try
                            {
                                bedT = tt.GetDouble();
                            }
                            catch
                            {
                            }
                        }
                    }
                }
            }
        }
        catch
        {
        }

        // Query camera info when online; webcam listing may still be available via Moonraker
        string? cam = null;
        string? snap = null;
        if (status.IsOnline)
        {
            (string? streamUrl, string? snapshotUrl) = await GetCameraUrlsAsync(baseUrl, ct);
            cam = streamUrl;
            snap = snapshotUrl;
        }

        // Calculate estimated time remaining from progress and elapsed print duration
        double? printTimeLeftSeconds = null;
        if (job?.Progress is > 0 and < 100 && job.PrintDurationSeconds is > 0)
        {
            double progressFraction = job.Progress.Value / 100.0;
            printTimeLeftSeconds = job.PrintDurationSeconds.Value * (1.0 - progressFraction) / progressFraction;
        }

        return new PrinterCompositeStatus(status.IsOnline, state, job?.Progress, job?.JobName, job?.ThumbnailUrl, cam, snap, x, y, z, hotend, bed, hotendT, bedT, PrintTimeLeftSeconds: printTimeLeftSeconds);
    }

    public Task<PrinterDto> CreatePrinterDtoAsync(
        Printer printer,
        PrinterCompositeStatus status,
        PrinterSpoolInfoDto? spoolInfo,
        CancellationToken ct = default)
    {
        // Camera URLs are now resolved from Cameras table by PrintersService.GetPrinterDtoAsync
        // (bead 2 compat layer). Plugin sets null; the caller overrides with Camera table data.

        // Construct backend-specific PrinterDto
        return Task.FromResult(new PrinterDto(
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
            CameraStreamUrl: null,
            CameraSnapshotUrl: null,
            X: status.X,
            Y: status.Y,
            Z: status.Z,
            HotendTemp: status.HotendTemp,
            BedTemp: status.BedTemp,
            HotendTarget: status.HotendTarget,
            BedTarget: status.BedTarget,
            Backend: PrinterBackend.Moonraker,
            ApiKey: printer.ApiKey,
            Username: printer.Username,
            Password: printer.Password,
            OriginalServerUrl: printer.OriginalServerUrl,
            BackendPort: printer.BackendPort,
            FrontendPort: printer.FrontendPort,
            SpoolInfo: spoolInfo,
            BackendUrl: printer.BackendUrl,
            FrontendUrl: printer.FrontendUrl,
            Location: printer.Location == null ? null : new LocationSummaryDto(printer.Location.Id, printer.Location.Name, printer.Location.Description),
            ObicoEnabled: printer.ObicoEnabled));
    }

    public async Task<bool> SendHomeAsync(string baseUrl, CancellationToken ct = default)
        => await SendGcodePrivateAsync(baseUrl, "G28", ct);

    // Interface implementation (no credential)
    public Task<bool> HomeXYAsync(string baseUrl, CancellationToken ct = default)
        => HomeXYAsync(baseUrl, null, ct);

    // Interface implementation (no credential)
    public Task<bool> HomeZAsync(string baseUrl, CancellationToken ct = default)
        => HomeZAsync(baseUrl, null, ct);

    // Overload with credential for ISupportsMovement
    public async Task<bool> HomeXYAsync(string baseUrl, PrinterCredential? credential, CancellationToken ct = default)
    {
        _ = credential;
        return await SendGcodePrivateAsync(baseUrl, "G28 X Y", ct);
    }

    // Overload with credential for ISupportsMovement
    public async Task<bool> HomeZAsync(string baseUrl, PrinterCredential? credential, CancellationToken ct = default)
    {
        _ = credential;
        return await SendGcodePrivateAsync(baseUrl, "G28 Z", ct);
    }

    public async Task<bool> SetTempsAsync(string baseUrl, double? hotend = null, double? bed = null, CancellationToken ct = default)
    {
        List<string> cmds = new();
        if (hotend is not null)
        {
            cmds.Add($"M104 S{hotend:0}");
        }

        if (bed is not null)
        {
            cmds.Add($"M140 S{bed:0}");
        }

        return await SendGcodePrivateAsync(baseUrl, cmds, ct);
    }

    public async Task<bool> MoveAsync(string baseUrl, double? x = null, double? y = null, double? z = null, double? f = null, CancellationToken ct = default)
    {
        List<string> parts = new() { "G91", "G0" };
        if (x is not null)
        {
            parts.Add($"X{x:0.###}");
        }

        if (y is not null)
        {
            parts.Add($"Y{y:0.###}");
        }

        if (z is not null)
        {
            parts.Add($"Z{z:0.###}");
        }

        if (f is not null)
        {
            parts.Add($"F{f:0.###}");
        }

        string[] cmds = new[] { string.Join(' ', parts), "G90" };
        return await SendGcodePrivateAsync(baseUrl, cmds, ct);
    }

    public async Task<bool> MoveToAsync(string baseUrl, double? x = null, double? y = null, double? z = null, double? f = null, CancellationToken ct = default)
    {
        List<string> parts = new() { "G90", "G0" };
        if (x is not null)
        {
            parts.Add($"X{x:0.###}");
        }

        if (y is not null)
        {
            parts.Add($"Y{y:0.###}");
        }

        if (z is not null)
        {
            parts.Add($"Z{z:0.###}");
        }

        if (f is not null)
        {
            parts.Add($"F{f:0.###}");
        }

        return await SendGcodePrivateAsync(baseUrl, string.Join(' ', parts), ct);
    }

    public async Task<bool> PauseAsync(string baseUrl, CancellationToken ct = default)
        => await PostPrintControlAsync(baseUrl, "/printer/print/pause", ct);

    public async Task<bool> CancelPrintAsync(string baseUrl, CancellationToken ct = default)
        => await PostPrintControlAsync(baseUrl, "/printer/print/cancel", ct);

    public Task<bool> CancelPrintAsync(Uri baseUrl, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return CancelPrintAsync(baseUrl.ToString(), ct);
    }

    public async Task<bool> ResumeAsync(string baseUrl, CancellationToken ct = default)
        => await PostPrintControlAsync(baseUrl, "/printer/print/resume", ct);

    private async Task<bool> PostPrintControlAsync(string baseUrl, string relativePath, CancellationToken ct = default)
    {
        static Uri WithPort(Uri uri, int port)
        {
            UriBuilder ub = new(uri)
            {
                Port = port
            };

            return ub.Uri;
        }

        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeouts.PrintControlTimeout);

            Uri baseUri = new(baseUrl);
            Uri endpoint = new(baseUri, relativePath);

            using HttpResponseMessage resp = await _http.PostAsync(endpoint, content: null, cts.Token);
            if (resp.IsSuccessStatusCode)
            {
                return true;
            }

            // Common misconfiguration: Moonraker is on 7125 but baseUrl points to a web UI port.
            // Retry against 7125 when the first attempt fails and we're not already using 7125.
            if (baseUri.Port != 7125)
            {
                Uri retryEndpoint = new(WithPort(baseUri, 7125), relativePath);
                using HttpResponseMessage retryResp = await _http.PostAsync(retryEndpoint, content: null, cts.Token);
                return retryResp.IsSuccessStatusCode;
            }

            return false;
        }
        catch
        {
            try
            {
                Uri baseUri = new(baseUrl);
                if (baseUri.Port == 7125)
                {
                    return false;
                }

                using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(_timeouts.StatusPollTimeout);

                Uri retryEndpoint = new(WithPort(baseUri, 7125), relativePath);
                using HttpResponseMessage retryResp = await _http.PostAsync(retryEndpoint, content: null, cts.Token);
                return retryResp.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }
    }

    public async Task<bool> EmergencyStopAsync(string baseUrl, CancellationToken ct = default)
        => await SendGcodePrivateAsync(baseUrl, "M112", ct);

    public async Task<bool> FirmwareRestartAsync(string baseUrl, CancellationToken ct = default)
        => await SendGcodePrivateAsync(baseUrl, "FIRMWARE_RESTART", ct);

    public Task<bool> FirmwareRestartAsync(Uri baseUrl, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return FirmwareRestartAsync(baseUrl.ToString(), ct);
    }

    public async Task<bool> DisableMotorsAsync(string baseUrl, CancellationToken ct = default)
        => await SendGcodePrivateAsync(baseUrl, "M84", ct);

    public Task<bool> DisableMotorsAsync(Uri baseUrl, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return DisableMotorsAsync(baseUrl.ToString(), ct);
    }

    public async Task<bool> SendGcodeAsync(string baseUrl, string gcode, CancellationToken ct = default)
        => await SendGcodePrivateAsync(baseUrl, gcode, ct);

    public Task<bool> SendGcodeAsync(Uri baseUrl, string gcode, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return SendGcodeAsync(baseUrl.ToString(), gcode, ct);
    }

    public async Task<PrintJobObjectListDto?> GetCurrentJobObjectsAsync(string baseUrl, PrinterCredential? credential = null, CancellationToken ct = default)
    {
        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeouts.StatusPollTimeout);

            Uri baseUri = new(baseUrl);
            Uri uri = new(baseUri, "printer/objects/query?print_stats&exclude_object");
            using HttpResponseMessage resp = await _http.GetAsync(uri, cts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                return null;
            }

            await using Stream stream = await resp.Content.ReadAsStreamAsync(cts.Token);
            using JsonDocument doc = await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token);
            if (!doc.RootElement.TryGetProperty("result", out JsonElement result) ||
                !result.TryGetProperty("status", out JsonElement status))
            {
                return null;
            }

            if (!IsPrinting(status))
            {
                return new PrintJobObjectListDto(Guid.Empty, null, Array.Empty<PrintJobObjectDto>());
            }

            string? jobName = TryGetJobName(status);
            HashSet<string> excludedObjects = GetStringSet(status, "exclude_object", "excluded_objects");
            string? currentObject = TryGetNestedString(status, "exclude_object", "current_object");

            List<PrintJobObjectDto> objects = GetLiveExcludeObjects(status, excludedObjects, currentObject);
            if (objects.Count == 0 && !string.IsNullOrWhiteSpace(jobName))
            {
                GCodeMetadata? metadata = await GetFileMetadataAsync(baseUrl, jobName, cts.Token);
                objects = GetMetadataObjects(metadata, excludedObjects, currentObject);
            }

            return new PrintJobObjectListDto(Guid.Empty, jobName, objects);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    public async Task<bool> ExcludeObjectAsync(string baseUrl, string objectName, CancellationToken ct = default)
    {
        string command = BuildExcludeObjectCommand(objectName);
        return await SendGcodePrivateAsync(baseUrl, command, ct);
    }

    public static string BuildExcludeObjectCommand(string objectName)
    {
        if (string.IsNullOrWhiteSpace(objectName))
        {
            throw new ArgumentException("Object name is required.", nameof(objectName));
        }

        if (objectName.Length > MaxExcludeObjectNameLength)
        {
            throw new ArgumentException("Object name is too long.", nameof(objectName));
        }

        if (objectName.Any(char.IsControl) || objectName.Contains(';', StringComparison.Ordinal))
        {
            throw new ArgumentException("Object name contains invalid characters.", nameof(objectName));
        }

        if (SafeUnquotedObjectNamePattern.IsMatch(objectName))
        {
            return $"EXCLUDE_OBJECT NAME={objectName}";
        }

        string escapedName = objectName
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);

        return $"EXCLUDE_OBJECT NAME=\"{escapedName}\"";
    }

    private static bool IsPrinting(JsonElement status)
    {
        return status.TryGetProperty("print_stats", out JsonElement printStats) &&
            printStats.ValueKind == JsonValueKind.Object &&
            printStats.TryGetProperty("state", out JsonElement state) &&
            state.ValueKind == JsonValueKind.String &&
            string.Equals(state.GetString(), "printing", StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryGetJobName(JsonElement status)
    {
        if (status.TryGetProperty("print_stats", out JsonElement printStats) &&
            printStats.ValueKind == JsonValueKind.Object &&
            printStats.TryGetProperty("filename", out JsonElement filename) &&
            filename.ValueKind == JsonValueKind.String)
        {
            return filename.GetString();
        }

        return null;
    }

    private static string? TryGetNestedString(JsonElement parent, string objectName, string propertyName)
    {
        if (parent.TryGetProperty(objectName, out JsonElement nested) &&
            nested.ValueKind == JsonValueKind.Object &&
            nested.TryGetProperty(propertyName, out JsonElement value) &&
            value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        return null;
    }

    private static HashSet<string> GetStringSet(JsonElement parent, string objectName, string propertyName)
    {
        HashSet<string> values = new(StringComparer.Ordinal);
        if (!parent.TryGetProperty(objectName, out JsonElement nested) ||
            nested.ValueKind != JsonValueKind.Object ||
            !nested.TryGetProperty(propertyName, out JsonElement array) ||
            array.ValueKind != JsonValueKind.Array)
        {
            return values;
        }

        foreach (JsonElement item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
            {
                values.Add(item.GetString()!);
            }
        }

        return values;
    }

    private static List<PrintJobObjectDto> GetLiveExcludeObjects(JsonElement status, HashSet<string> excludedObjects, string? currentObject)
    {
        if (!status.TryGetProperty("exclude_object", out JsonElement excludeObject) ||
            excludeObject.ValueKind != JsonValueKind.Object ||
            !excludeObject.TryGetProperty("objects", out JsonElement objectsElement) ||
            objectsElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        List<PrintJobObjectDto> objects = [];
        foreach (JsonElement objectElement in objectsElement.EnumerateArray())
        {
            if (objectElement.ValueKind != JsonValueKind.Object ||
                !objectElement.TryGetProperty("name", out JsonElement nameElement) ||
                nameElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            string? name = nameElement.GetString();
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            objects.Add(new PrintJobObjectDto(
                name,
                excludedObjects.Contains(name),
                string.Equals(name, currentObject, StringComparison.Ordinal)));
        }

        return objects;
    }

    private static List<PrintJobObjectDto> GetMetadataObjects(GCodeMetadata? metadata, HashSet<string> excludedObjects, string? currentObject)
    {
        if (metadata?.ObjectInfo is not { Length: > 0 })
        {
            return [];
        }

        return metadata.ObjectInfo
            .Select(o => o.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .Select(name => new PrintJobObjectDto(
                name!,
                excludedObjects.Contains(name!),
                string.Equals(name, currentObject, StringComparison.Ordinal)))
            .ToList();
    }

    // ISupportsFilamentControl implementation
    public async Task<bool> LoadFilamentAsync(string baseUrl, CancellationToken ct = default)
        => await SendGcodePrivateAsync(baseUrl, "LOAD_FILAMENT", ct);

    public async Task<bool> UnloadFilamentAsync(string baseUrl, CancellationToken ct = default)
        => await SendGcodePrivateAsync(baseUrl, "UNLOAD_FILAMENT", ct);

    public async Task<bool> ChangeFilamentAsync(string baseUrl, CancellationToken ct = default)
        => await SendGcodePrivateAsync(baseUrl, "M600", ct);

    private async Task<bool> SendGcodePrivateAsync(string baseUrl, string gcode, CancellationToken ct = default)
        => await SendGcodePrivateAsync(baseUrl, new[] { gcode }, ct);

    private async Task<bool> SendGcodePrivateAsync(string baseUrl, IEnumerable<string> gcodes, CancellationToken ct = default)
    {
        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeouts.StatusPollTimeout);
            Uri baseUri4 = new(baseUrl);
            Uri scriptUri = new(baseUri4, "printer/gcode/script");
            using HttpResponseMessage resp = await _http.PostAsJsonAsync(scriptUri, new { script = string.Join("\n", gcodes) }, cts.Token);

            // Secondary defense (#317): translate firmware-level busy signals to PrinterBackendBusyException
            // so the controller returns HTTP 409 Conflict to the caller.
            //
            // 409 Conflict — Moonraker/firmware explicitly blocked the command (gcode queue locked).
            //   Always treat as printer-busy-printing.
            //
            // 503 Service Unavailable — Moonraker returns this when Klippy is unavailable
            //   (disconnected, shutdown, or error state), NOT when the printer is actively printing.
            //   Inspect the JSON body: only raise PrinterBackendBusyException when the message
            //   contains printer-busy keywords (e.g. "printing", "busy"); otherwise treat as a
            //   transient backend-unavailable condition and return false.
            //   This narrows the overly-broad "503 = busy" rule from #317 (Bishop's review #318).
            if (resp.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                throw new PrinterBackendBusyException(
                    $"Moonraker refused gcode (409 Conflict) at {baseUrl}.");
            }

            if (resp.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable)
            {
                string errorBody = await resp.Content.ReadAsStringAsync(cts.Token);
                if (IsMoonrakerBusyPrintingBody(errorBody))
                {
                    throw new PrinterBackendBusyException(
                        $"Moonraker refused gcode (503 printing-busy) at {baseUrl}.");
                }

                // Klippy unavailable / error state — not a printer-busy signal.
                return false;
            }

            return resp.IsSuccessStatusCode;
        }
        catch (PrinterBackendBusyException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Returns true when a Moonraker 503 error body indicates the printer is actively printing
    /// (as opposed to Klippy being disconnected or in a shutdown/error state).
    /// Moonraker 503 bodies are JSON: <c>{"error": "WebRequestError", "message": "..."}</c>.
    ///
    /// Phrase allowlist (case-insensitive) — only unambiguous printer-job-busy signals:
    ///   "printer is printing"           — gcode rejected while a job is active
    ///   "printer is currently printing" — Klipper firmware variant
    ///   "printer is busy"               — firmware variant
    ///   "printer busy"                  — older firmware variant
    ///   "sd busy"                       — SD-card busy variant
    ///
    /// Bare "busy" and bare "printing" are intentionally excluded: they over-match Klippy
    /// startup states (e.g. "Klippy is busy initializing") and error messages that mention
    /// "printing" in a non-job-busy context. Prefer false negatives over false positives —
    /// a miss returns false (backend unavailable), not a wrong 409.
    /// </summary>
    private static bool IsMoonrakerBusyPrintingBody(string body)
    {
        string lower = body.ToLowerInvariant();
        return lower.Contains("printer is printing")
            || lower.Contains("printer is currently printing")
            || lower.Contains("printer is busy")
            || lower.Contains("printer busy")
            || lower.Contains("sd busy");
    }

    // Unified camera URL resolver: fetches both stream and snapshot from a single listing call, with test-resolution fallback
    private async Task<(string? Stream, string? Snapshot)> GetCameraUrlsAsync(string baseUrl, CancellationToken ct = default)
    {
        string? stream = null;
        string? snapshot = null;
        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeouts.StatusPollTimeout);
            Uri baseUri = new(baseUrl);
            Uri listUri = new(baseUri, "server/webcams/list");
            using HttpResponseMessage resp = await _http.GetAsync(listUri, cts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                return (null, null);
            }

            await using Stream streamContent = await resp.Content.ReadAsStreamAsync(cts.Token);
            using JsonDocument doc = await JsonDocument.ParseAsync(streamContent, cancellationToken: cts.Token);
            JsonElement root = doc.RootElement;
            if (!((root.TryGetProperty("webcams", out JsonElement cams) && cams.ValueKind == JsonValueKind.Array) ||
                  (root.TryGetProperty("result", out JsonElement res) && res.ValueKind == JsonValueKind.Object && res.TryGetProperty("webcams", out cams) && cams.ValueKind == JsonValueKind.Array)))
            {
                return (null, null);
            }

            foreach (JsonElement cam in cams.EnumerateArray())
            {
                bool enabled = true;
                if (cam.TryGetProperty("enabled", out JsonElement en))
                {
                    if (en.ValueKind == JsonValueKind.False)
                    {
                        enabled = false;
                    }
                    else if (en.ValueKind == JsonValueKind.True)
                    {
                        enabled = true;
                    }
                }

                if (!enabled)
                {
                    continue;
                }

                // Try to resolve via /server/webcams/test using uid or name
                string? uid = null;
                if (cam.TryGetProperty("uid", out JsonElement uidEl) && uidEl.ValueKind == JsonValueKind.String)
                {
                    uid = uidEl.GetString();
                }

                string? name = null;
                if (cam.TryGetProperty("name", out JsonElement nmEl) && nmEl.ValueKind == JsonValueKind.String)
                {
                    name = nmEl.GetString();
                }

                Uri? testUri = uid is not null
                    ? new Uri(new Uri(baseUrl), $"server/webcams/test?uid={Uri.EscapeDataString(uid)}")
                    : (name is not null ? new Uri(new Uri(baseUrl), $"server/webcams/test?name={Uri.EscapeDataString(name)}") : null);
                if (testUri is not null)
                {
                    try
                    {
                        using HttpResponseMessage tresp = await _http.PostAsync(testUri, content: null, cts.Token);
                        if (tresp.IsSuccessStatusCode)
                        {
                            await using Stream tstream = await tresp.Content.ReadAsStreamAsync(cts.Token);
                            using JsonDocument tdoc = await JsonDocument.ParseAsync(tstream, cancellationToken: cts.Token);
                            JsonElement troot = tdoc.RootElement;
                            if (troot.TryGetProperty("result", out JsonElement tresult))
                            {
                                troot = tresult;
                            }

                            if (stream is null && troot.TryGetProperty("stream_url", out JsonElement tsu) && tsu.ValueKind == JsonValueKind.String)
                            {
                                stream = NormalizeCameraUrl(tsu.GetString(), baseUrl);
                            }

                            if (snapshot is null && troot.TryGetProperty("snapshot_url", out JsonElement ssu) && ssu.ValueKind == JsonValueKind.String)
                            {
                                snapshot = NormalizeCameraUrl(ssu.GetString(), baseUrl);
                            }

                            if (stream is not null && snapshot is not null)
                            {
                                return (stream, snapshot);
                            }
                        }
                    }
                    catch (HttpRequestException)
                    {
                        // Test endpoint not available, continue with fallback
                    }
                    catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
                    {
                        // Timeout during test, continue with fallback
                    }
                    catch (JsonException)
                    {
                        // Invalid JSON response, continue with fallback
                    }
                }

                // Fallback to raw listing values, normalizing relative paths
                if (stream is null && cam.TryGetProperty("stream_url", out JsonElement su) && su.ValueKind == JsonValueKind.String)
                {
                    string? s = su.GetString();
                    if (!string.IsNullOrWhiteSpace(s))
                    {
                        stream = NormalizeCameraUrl(s, baseUrl);
                    }
                }

                if (snapshot is null && cam.TryGetProperty("snapshot_url", out JsonElement sn) && sn.ValueKind == JsonValueKind.String)
                {
                    string? s = sn.GetString();
                    if (!string.IsNullOrWhiteSpace(s))
                    {
                        snapshot = NormalizeCameraUrl(s, baseUrl);
                    }
                }

                if (stream is not null && snapshot is not null)
                {
                    return (stream, snapshot);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Expected when cancellation is requested
            throw;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "Failed to get camera URLs from {BaseUrl}", baseUrl);
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            _logger.LogDebug(ex, "Failed to get camera URLs from {BaseUrl}", baseUrl);
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "Failed to get camera URLs from {BaseUrl}", baseUrl);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to get camera URLs from {BaseUrl}", baseUrl);
        }

        return (stream, snapshot);
    }

    // File upload and management methods
    public async Task<bool> UploadGcodeAsync(string baseUrl, string fileName, Stream fileContent, CancellationToken ct = default)
        => await UploadGcodeAsync(baseUrl, fileName, fileContent, print: false, ct);

    /// <summary>
    /// Uploads a G-code file to the Moonraker printer, optionally starting the print immediately
    /// via the <c>print=true</c> form parameter. Using this avoids a second HTTP round-trip.
    /// </summary>
    public async Task<bool> UploadGcodeAsync(string baseUrl, string fileName, Stream fileContent, bool print, CancellationToken ct = default)
    {
        try
        {
            Uri baseUri = new(baseUrl);
            Uri uri = new(baseUri, "server/files/upload");

            long streamLength = fileContent.CanSeek ? fileContent.Length : -1;
            _logger.LogInformation("[Moonraker] Uploading G-code file {FileName} to {Uri}, stream length: {StreamLength}, print: {Print}", fileName, uri, streamLength, print);

            using MultipartFormDataContent formContent = new();
            using StreamContent streamContent = new(fileContent);
            streamContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            formContent.Add(streamContent, "file", fileName);
            formContent.Add(new StringContent("gcodes"), "root"); // Upload to gcodes directory

            if (print)
            {
                formContent.Add(new StringContent("true"), "print");
            }

            // IMPORTANT: don't use an absolute time limit for uploads here.
            // We rely on caller cancellation and HttpClient is configured with an infinite Timeout.
            using HttpResponseMessage resp = await _http.PostAsync(uri, formContent, ct);

            if (!resp.IsSuccessStatusCode)
            {
                string responseBody = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("[Moonraker] Upload failed with status {RespStatusCode}: {ResponseBody}", resp.StatusCode, responseBody);
            }
            else
            {
                _logger.LogInformation("[Moonraker] Upload succeeded for {FileName}{PrintSuffix}", fileName, print ? " (print started)" : string.Empty);
            }

            return resp.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError("[Moonraker] Exception during G-code upload for {FileName} to {BaseUrl}: {Message}", fileName, baseUrl, ex.Message);
            return false;
        }
    }

    public async Task<bool> StartPrintAsync(string baseUrl, string fileName, CancellationToken ct = default)
    {
        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeouts.PrintControlTimeout);

            Uri baseUri = new(baseUrl);
            Uri uri = new(baseUri, "printer/print/start");
            var payload = new { filename = fileName };

            using HttpResponseMessage resp = await _http.PostAsJsonAsync(uri, payload, cts.Token);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<string[]> GetFileListAsync(string baseUrl, CancellationToken ct = default)
    {
        // Call the new method that returns full file info, then extract just the paths for backward compatibility
        List<PrinterFileInfo> fileInfoList = await GetFileListWithMetadataAsync(baseUrl, ct);
        return fileInfoList.Select(f => f.Path).ToArray();
    }

    /// <summary>
    /// Get list of G-code files with metadata (size, modified date) from Moonraker.
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server.</param>
    /// <param name="ct">Cancellation token to cancel the operation.</param>
    private async Task<List<PrinterFileInfo>> GetFileListWithMetadataAsync(string baseUrl, CancellationToken ct = default)
    {
        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeouts.CommandTimeout);

            Uri baseUri = new(baseUrl);
            Uri uri = new(baseUri, "server/files/list?root=gcodes");
            using HttpResponseMessage resp = await _http.GetAsync(uri, cts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                return [];
            }

            await using Stream stream = await resp.Content.ReadAsStreamAsync(cts.Token);
            using JsonDocument doc = await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token);
            JsonElement root = doc.RootElement;

            if (!root.TryGetProperty("result", out JsonElement result) ||
                result.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            List<PrinterFileInfo> files = new();
            foreach (JsonElement file in result.EnumerateArray())
            {
                if (file.TryGetProperty("path", out JsonElement path) &&
                    path.ValueKind == JsonValueKind.String)
                {
                    string? fileName = path.GetString();
                    if (!string.IsNullOrEmpty(fileName) && fileName.EndsWith(".gcode", StringComparison.OrdinalIgnoreCase))
                    {
                        // Extract size if available
                        long? size = null;
                        if (file.TryGetProperty("size", out JsonElement sizeElement) &&
                            sizeElement.ValueKind == JsonValueKind.Number)
                        {
                            size = sizeElement.GetInt64();
                        }

                        // Extract modified timestamp if available (convert to Unix timestamp in seconds)
                        long? modified = null;
                        if (file.TryGetProperty("modified", out JsonElement modifiedElement) &&
                            modifiedElement.ValueKind == JsonValueKind.Number)
                        {
                            double timestamp = modifiedElement.GetDouble();
                            modified = (long)timestamp;
                        }

                        // Get thumbnail URL for this file
                        string? thumbnailUrl = await GetThumbnailUrlAsync(baseUrl, fileName, cts.Token);

                        files.Add(new PrinterFileInfo
                        {
                            Name = fileName,
                            Path = fileName,
                            Size = size,
                            Modified = modified,
                            ThumbnailUrl = thumbnailUrl
                        });
                    }
                }
            }

            return files;
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Gets the thumbnail URL for a gcode file if available.
    /// Returns null if no thumbnail is found.
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server.</param>
    /// <param name="filename">The name of the gcode file.</param>
    /// <param name="ct">Cancellation token to cancel the operation.</param>
    private async Task<string?> GetThumbnailUrlAsync(string baseUrl, string filename, CancellationToken ct = default)
    {
        try
        {
            List<(int Width, int Height, string RelativePath)> thumbnails = await GetFileThumbnailsAsync(baseUrl, filename, ct);
            if (thumbnails != null && thumbnails.Count > 0)
            {
                // Use the largest thumbnail available
                (int Width, int Height, string RelativePath) largestThumbnail = thumbnails.OrderByDescending(t => t.Width * t.Height).FirstOrDefault();
                if (!string.IsNullOrEmpty(largestThumbnail.RelativePath))
                {
                    // Build absolute thumbnail URL
                    return $"{baseUrl}/server/files/gcodes/{Uri.EscapeDataString(largestThumbnail.RelativePath)}";
                }
            }
        }
        catch
        {
            // Silently fail if thumbnail retrieval fails
        }

        return null;
    }

    // ===== FILE OPERATIONS API =====

    /// <summary>
    /// Get list of available file roots
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server.</param>
    /// <param name="ct">Cancellation token to cancel the operation.</param>
    public async Task<FileRoot[]> GetFileRootsAsync(string baseUrl, CancellationToken ct = default)
    {
        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeouts.CommandTimeout);

            Uri baseUri = new(baseUrl);
            Uri uri = new(baseUri, "server/files/roots");
            using HttpResponseMessage resp = await _http.GetAsync(uri, cts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                return Array.Empty<FileRoot>();
            }

            MoonrakerResponse<FileRoot[]>? response = await resp.Content.ReadFromJsonAsync<MoonrakerResponse<FileRoot[]>>(cancellationToken: cts.Token);
            return response?.Result ?? Array.Empty<FileRoot>();
        }
        catch
        {
            return Array.Empty<FileRoot>();
        }
    }

    /// <summary>
    /// Get directory information with optional filtering
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server.</param>
    /// <param name="path">The directory path to query.</param>
    /// <param name="extended">Whether to include extended file information.</param>
    /// <param name="ct">Cancellation token to cancel the operation.</param>
    public async Task<MoonrakerDirectoryInfo?> GetDirectoryAsync(string baseUrl, string path, bool extended = false, CancellationToken ct = default)
    {
        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeouts.CommandTimeout);

            // First try using REST API
            string encodedPath = Uri.EscapeDataString(path);
            Uri baseUri = new(baseUrl);
            Uri uri = new(baseUri, $"server/files/directory?path={encodedPath}&extended={(extended ? "true" : "false")}");

            using HttpResponseMessage resp = await _http.GetAsync(uri, cts.Token);
            if (resp.IsSuccessStatusCode)
            {
                try
                {
                    MoonrakerResponse<MoonrakerDirectoryInfo>? response = await resp.Content.ReadFromJsonAsync<MoonrakerResponse<MoonrakerDirectoryInfo>>(cancellationToken: cts.Token);
                    if (response?.Result != null)
                    {
                        return response.Result;
                    }
                }
                catch (JsonException ex)
                {
                    _logger.LogDebug("Error parsing directory info from REST API: {Message}", ex.Message);

                    // Continue to fallback method
                }
            }

            // If REST API fails, try using JSON-RPC
            JsonRpcRequest jsonRpcRequest = new()
            {
                Method = "server.files.get_directory",
                Params = new Dictionary<string, object>
                {
                    ["path"] = path,
                    ["extended"] = extended
                },
                Id = 1
            };

            Uri jsonRpcUri = new(baseUri, "websocket");
            string jsonContent = JsonSerializer.Serialize(jsonRpcRequest);
            using StringContent content = new(jsonContent, Encoding.UTF8, "application/json");

            using HttpResponseMessage jsonRpcResp = await _http.PostAsync(jsonRpcUri, content, cts.Token);
            if (!jsonRpcResp.IsSuccessStatusCode)
            {
                return null;
            }

            string responseJson = await jsonRpcResp.Content.ReadAsStringAsync(cts.Token);

            try
            {
                JsonRpcResponse? jsonRpcResponse = JsonSerializer.Deserialize<JsonRpcResponse>(responseJson);

                if (jsonRpcResponse?.Error != null)
                {
                    _logger.LogDebug("JSON-RPC error for {JsonRpcRequestMethod}: {Message} (Code: {Code})", jsonRpcRequest.Method, jsonRpcResponse.Error.Message, jsonRpcResponse.Error.Code);

                    // Special handling for URL parameter error
                    if (jsonRpcResponse.Error.Message.Contains("No data for argument: url", StringComparison.OrdinalIgnoreCase) ||
                        jsonRpcResponse.Error.Code == 400)
                    {
                        // Try again with URL parameter included
                        jsonRpcRequest.Params = new Dictionary<string, object>
                        {
                            ["path"] = path,
                            ["extended"] = extended,
                            ["url"] = "http://printfarmer-api:5088" // Add URL parameter
                        };

                        jsonContent = JsonSerializer.Serialize(jsonRpcRequest);
                        using StringContent retryContent = new(jsonContent, Encoding.UTF8, "application/json");

                        using HttpResponseMessage retryResp = await _http.PostAsync(jsonRpcUri, retryContent, cts.Token);
                        if (!retryResp.IsSuccessStatusCode)
                        {
                            return null;
                        }

                        responseJson = await retryResp.Content.ReadAsStringAsync(cts.Token);
                        jsonRpcResponse = JsonSerializer.Deserialize<JsonRpcResponse>(responseJson);

                        if (jsonRpcResponse?.Error != null)
                        {
                            _logger.LogDebug("JSON-RPC error for {JsonRpcRequestMethod}: {Message} (Code: {Code})", jsonRpcRequest.Method, jsonRpcResponse.Error.Message, jsonRpcResponse.Error.Code);
                            return null;
                        }
                    }
                    else
                    {
                        return null;
                    }
                }

                if (jsonRpcResponse?.Result == null)
                {
                    return null;
                }

                // Deserialize the result to MoonrakerDirectoryInfo
                string? resultJson = jsonRpcResponse.Result.ToString();
                MoonrakerDirectoryInfo? directoryInfo = JsonSerializer.Deserialize<MoonrakerDirectoryInfo>(resultJson ?? "{}");
                return directoryInfo;
            }
            catch (JsonException jex)
            {
                _logger.LogDebug(jex, "Failed to parse JSON response: {JexMessage}", jex.Message);
                return null;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Expected when cancellation is requested
            throw;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "Failed to get directory from {BaseUrl}: {Message}", baseUrl, ex.Message);
            return null;
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            _logger.LogDebug(ex, "Failed to get directory from {BaseUrl}: {Message}", baseUrl, ex.Message);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Catch any remaining exceptions (JSON serialization errors, etc.) to ensure method resilience
            _logger.LogDebug(ex, "Failed to get directory from {BaseUrl}: {Message}", baseUrl, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Create a new directory
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server.</param>
    /// <param name="path">The path of the directory to create.</param>
    /// <param name="ct">Cancellation token to cancel the operation.</param>
    public async Task<DirectoryCreateResponse?> CreateDirectoryAsync(string baseUrl, string path, CancellationToken ct = default)
    {
        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeouts.CommandTimeout);
            Uri baseUri = new(baseUrl);
            Uri uri = new(baseUri, "server/files/directory");
            DirectoryCreateRequest request = new()
            { Path = path };
            using HttpResponseMessage resp = await _http.PostAsJsonAsync(uri, request, cts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                return null;
            }

            MoonrakerResponse<DirectoryCreateResponse>? response = await resp.Content.ReadFromJsonAsync<MoonrakerResponse<DirectoryCreateResponse>>(cancellationToken: cts.Token);
            return response?.Result;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Delete a file or directory
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server.</param>
    /// <param name="path">The path of the file or directory to delete.</param>
    /// <param name="force">Whether to force deletion of non-empty directories.</param>
    /// <param name="ct">Cancellation token to cancel the operation.</param>
    public async Task<bool> DeleteFileOrDirectoryAsync(string baseUrl, string path, bool force = false, CancellationToken ct = default)
    {
        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeouts.CommandTimeout);

            string encodedPath = Uri.EscapeDataString(path);
            Uri baseUri = new(baseUrl);
            Uri uri = new(baseUri, $"server/files/directory?path={encodedPath}&force={(force ? "true" : "false")}");
            using HttpResponseMessage resp = await _http.DeleteAsync(uri, cts.Token);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Move or rename a file/directory
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server.</param>
    /// <param name="source">The source path of the file or directory.</param>
    /// <param name="dest">The destination path for the file or directory.</param>
    /// <param name="ct">Cancellation token to cancel the operation.</param>
    public async Task<bool> MoveFileAsync(string baseUrl, string source, string dest, CancellationToken ct = default)
    {
        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeouts.CommandTimeout);
            Uri baseUri = new(baseUrl);
            Uri uri = new(baseUri, "server/files/move");
            FileMoveRequest request = new()
            { Source = source, Dest = dest };
            using HttpResponseMessage resp = await _http.PostAsJsonAsync(uri, request, cts.Token);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Copy a file
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server.</param>
    /// <param name="source">The source path of the file to copy.</param>
    /// <param name="dest">The destination path for the copied file.</param>
    /// <param name="ct">Cancellation token to cancel the operation.</param>
    public async Task<bool> CopyFileAsync(string baseUrl, string source, string dest, CancellationToken ct = default)
    {
        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeouts.CommandTimeout);
            Uri baseUri = new(baseUrl);
            Uri uri = new(baseUri, "server/files/copy");
            FileCopyRequest request = new()
            { Source = source, Dest = dest };
            using HttpResponseMessage resp = await _http.PostAsJsonAsync(uri, request, cts.Token);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Get file metadata for G-Code files
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server.</param>
    /// <param name="filename">The name of the G-Code file.</param>
    /// <param name="ct">Cancellation token to cancel the operation.</param>
    public async Task<GCodeMetadata?> GetFileMetadataAsync(string baseUrl, string filename, CancellationToken ct = default)
    {
        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeouts.CommandTimeout);

            string encodedFilename = Uri.EscapeDataString(filename);
            Uri baseUri = new(baseUrl);
            Uri uri = new(baseUri, $"server/files/metadata?filename={encodedFilename}");
            using HttpResponseMessage resp = await _http.GetAsync(uri, cts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                return null;
            }

            MoonrakerResponse<GCodeMetadata>? response = await resp.Content.ReadFromJsonAsync<MoonrakerResponse<GCodeMetadata>>(cancellationToken: cts.Token);
            return response?.Result;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Gets thumbnail information for a G-code file using Moonraker's dedicated thumbnails API endpoint.
    /// This is more efficient than GetFileMetadataAsync when only thumbnails are needed.
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server.</param>
    /// <param name="filename">The name of the G-code file.</param>
    /// <param name="ct">Cancellation token to cancel the operation.</param>
    public async Task<List<(int Width, int Height, string RelativePath)>> GetFileThumbnailsAsync(string baseUrl, string filename, CancellationToken ct = default)
    {
        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeouts.CommandTimeout);

            string encodedFilename = Uri.EscapeDataString(filename);
            Uri baseUri = new(baseUrl);
            Uri uri = new(baseUri, $"server/files/thumbnails?filename={encodedFilename}");
            using HttpResponseMessage resp = await _http.GetAsync(uri, cts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                return [];
            }

            MoonrakerResponse<List<ThumbnailInfo>>? response = await resp.Content.ReadFromJsonAsync<MoonrakerResponse<List<ThumbnailInfo>>>(cancellationToken: cts.Token);
            return response?.Result == null || response.Result.Count == 0
                ? []
                : response.Result
                .Select(t => (t.Width, t.Height, t.RelativePath))
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Start a metadata scan for a file
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server.</param>
    /// <param name="filename">The name of the file to scan.</param>
    /// <param name="ct">Cancellation token to cancel the operation.</param>
    public async Task<bool> StartMetadataScanAsync(string baseUrl, string filename, CancellationToken ct = default)
    {
        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeouts.CommandTimeout);
            Uri baseUri = new(baseUrl);
            Uri uri = new(baseUri, "server/files/metascan");
            MetadataScanRequest request = new()
            { Filename = filename };
            using HttpResponseMessage resp = await _http.PostAsJsonAsync(uri, request, cts.Token);
            return resp.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Expected when cancellation is requested
            throw;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "Failed to start metadata scan for {Filename} at {BaseUrl}", filename, baseUrl);
            return false;
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            _logger.LogDebug(ex, "Failed to start metadata scan for {Filename} at {BaseUrl}", filename, baseUrl);
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to start metadata scan for {Filename} at {BaseUrl}", filename, baseUrl);
            return false;
        }
    }

    /// <summary>
    /// Get a file thumbnail
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server.</param>
    /// <param name="filename">The name of the file.</param>
    /// <param name="ct">Cancellation token to cancel the operation.</param>
    public async Task<byte[]?> GetFileThumbnailAsync(string baseUrl, string filename, CancellationToken ct = default)
    {
        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeouts.CommandTimeout);

            string encodedFilename = Uri.EscapeDataString(filename);
            Uri baseUri = new(baseUrl);
            Uri uri = new(baseUri, $"server/files/thumbs/{encodedFilename}");
            using HttpResponseMessage resp = await _http.GetAsync(uri, cts.Token);
            return !resp.IsSuccessStatusCode ? null : await resp.Content.ReadAsByteArrayAsync(cts.Token);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Gets the URL of the largest available thumbnail for a G-code file using the Moonraker thumbnails API.
    /// This endpoint is more efficient than GetFileMetadataAsync when only thumbnail information is needed.
    /// The response returns thumbnail metadata including relative paths, allowing efficient direct construction of URLs.
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server.</param>
    /// <param name="filename">The name of the G-code file.</param>
    /// <param name="ct">Cancellation token to cancel the operation.</param>
    public async Task<string?> GetFileThumbnailUrlAsync(string baseUrl, string filename, CancellationToken ct = default)
    {
        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeouts.CommandTimeout);

            string encodedFilename = Uri.EscapeDataString(filename);
            Uri baseUri = new(baseUrl);
            Uri uri = new(baseUri, $"server/files/thumbnails?filename={encodedFilename}");
            using HttpResponseMessage resp = await _http.GetAsync(uri, cts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                return null;
            }

            // Parse the response which contains thumbnail metadata array
            MoonrakerResponse<ThumbnailInfo[]>? response = await resp.Content.ReadFromJsonAsync<MoonrakerResponse<ThumbnailInfo[]>>(cancellationToken: cts.Token);
            if (response?.Result == null || response.Result.Length == 0)
            {
                return null;
            }

            // Find the largest thumbnail by pixel count
            ThumbnailInfo? largestThumbnail = response.Result
                .OrderByDescending(t => t.Width * t.Height)
                .FirstOrDefault();

            return largestThumbnail?.RelativePath;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Download a file from Moonraker
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server.</param>
    /// <param name="filename">The name of the file to download.</param>
    /// <param name="ct">Cancellation token to cancel the operation.</param>
    /// <remarks>
    /// Moonraker filenames come with "gcodes/" prefix (e.g., "gcodes/file.gcode").
    /// The URL constructed is: /server/files/gcodes/...
    /// </remarks>
    public async Task<byte[]?> DownloadFileAsync(string baseUrl, string filename, CancellationToken ct = default)
    {
        _logger.LogInformation("[Moonraker] DownloadFileAsync starting: filename='{Filename}', baseUrl='{BaseUrl}'", filename, baseUrl);

        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeouts.FileDownloadTimeout);

            // Encode each path segment separately to preserve forward slashes
            // e.g., "folder/subfolder/file.gcode" -> "folder/subfolder/file.gcode" (only special chars encoded)
            string[] pathSegments = filename.Split('/');
            string encodedFilename = string.Join("/", pathSegments.Select(Uri.EscapeDataString));

            Uri baseUri = new(baseUrl);
            Uri uri = new(baseUri, $"server/files/gcodes/{encodedFilename}");

            _logger.LogDebug("[Moonraker] Downloading file from URL: {Uri}", uri);

            using HttpResponseMessage resp = await _http.GetAsync(uri, cts.Token);

            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("[Moonraker] Download failed: StatusCode={RespStatusCode}, ReasonPhrase='{RespReasonPhrase}', URL='{Uri}'", resp.StatusCode, resp.ReasonPhrase, uri);
                return null;
            }

            byte[] content = await resp.Content.ReadAsByteArrayAsync(cts.Token);

            if (content == null || content.Length == 0)
            {
                _logger.LogWarning("[Moonraker] Download returned empty content for file '{Filename}'. StatusCode={RespStatusCode}, ContentLength={ContentLength}, ContentType={ContentType}", filename, resp.StatusCode, resp.Content.Headers.ContentLength, resp.Content.Headers.ContentType);
                return null;
            }

            _logger.LogInformation("[Moonraker] Successfully downloaded file '{Filename}': {ContentLength} bytes", filename, content.Length);
            return content;
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogWarning(ex, "[Moonraker] Download timeout for file '{Filename}' after 30 seconds", filename);
            return null;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "[Moonraker] HTTP error downloading file '{Filename}': {Message}", filename, ex.Message);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Moonraker] Unexpected error downloading file '{Filename}': {Name}: {Message}", filename, ex.GetType().Name, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Upload a file to a specific root directory
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server.</param>
    /// <param name="root">The root directory to upload to.</param>
    /// <param name="filename">The name of the file to upload.</param>
    /// <param name="content">The file content stream.</param>
    /// <param name="print">Whether to start printing after upload.</param>
    /// <param name="ct">Cancellation token to cancel the operation.</param>
    public async Task<FileUploadResponse?> UploadFileAsync(string baseUrl, string root, string filename, Stream content,
        bool print = false, CancellationToken ct = default)
    {
        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeouts.FileUploadTimeout);
            Uri baseUri = new(baseUrl);
            Uri uri = new(baseUri, "server/files/upload");

            using MultipartFormDataContent formContent = new();
            using StreamContent streamContent = new(content);
            streamContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            formContent.Add(streamContent, "file", filename);
            formContent.Add(new StringContent(root), "root");

            if (print)
            {
                formContent.Add(new StringContent("true"), "print");
            }

            using HttpResponseMessage resp = await _http.PostAsync(uri, formContent, cts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                return null;
            }

            MoonrakerResponse<FileUploadResponse>? response = await resp.Content.ReadFromJsonAsync<MoonrakerResponse<FileUploadResponse>>(cancellationToken: cts.Token);
            return response?.Result;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Upload a file with path (can create subdirectories)
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server.</param>
    /// <param name="path">The full path including filename for the upload.</param>
    /// <param name="content">The file content stream.</param>
    /// <param name="print">Whether to start printing after upload.</param>
    /// <param name="ct">Cancellation token to cancel the operation.</param>
    public async Task<FileUploadResponse?> UploadFileWithPathAsync(string baseUrl, string path, Stream content,
        bool print = false, CancellationToken ct = default)
    {
        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeouts.FileUploadTimeout);
            Uri baseUri = new(baseUrl);
            Uri uri = new(baseUri, "server/files/upload");

            using MultipartFormDataContent formContent = new();
            using StreamContent streamContent = new(content);
            streamContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

            string filename = Path.GetFileName(path);
            formContent.Add(streamContent, "file", filename);
            formContent.Add(new StringContent(path), "path");

            if (print)
            {
                formContent.Add(new StringContent("true"), "print");
            }

            using HttpResponseMessage resp = await _http.PostAsync(uri, formContent, cts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                return null;
            }

            MoonrakerResponse<FileUploadResponse>? response = await resp.Content.ReadFromJsonAsync<MoonrakerResponse<FileUploadResponse>>(cancellationToken: cts.Token);
            return response?.Result;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Get detailed file list with extended information
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server.</param>
    /// <param name="root">The root directory to list files from.</param>
    /// <param name="path">Optional subdirectory path to list.</param>
    /// <param name="ct">Cancellation token to cancel the operation.</param>
    public async Task<MoonrakerFileInfo[]> GetDetailedFileListAsync(string baseUrl, string root = "gcodes", string? path = null, CancellationToken ct = default)
    {
        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeouts.PrintControlTimeout);
            Uri baseUri = new(baseUrl);
            string relative = $"server/files/list?root={Uri.EscapeDataString(root)}&extended=true";
            if (!string.IsNullOrEmpty(path))
            {
                relative += $"&path={Uri.EscapeDataString(path)}";
            }

            Uri uri = new(baseUri, relative);
            using HttpResponseMessage resp = await _http.GetAsync(uri, cts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                return [];
            }

            MoonrakerResponse<MoonrakerFileInfo[]>? response = await resp.Content.ReadFromJsonAsync<MoonrakerResponse<MoonrakerFileInfo[]>>(cancellationToken: cts.Token);
            return response?.Result ?? [];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Delete a specific file
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server.</param>
    /// <param name="path">The path of the file to delete.</param>
    /// <param name="ct">Cancellation token to cancel the operation.</param>
    public async Task<bool> DeleteFileAsync(string baseUrl, string path, CancellationToken ct = default)
    {
        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeouts.CommandTimeout);

            string encodedPath = Uri.EscapeDataString(path);
            Uri baseUri = new(baseUrl);
            Uri uri = new(baseUri, $"server/files/gcodes/{encodedPath}");
            using HttpResponseMessage resp = await _http.DeleteAsync(uri, cts.Token);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Get file contents as stream
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server.</param>
    /// <param name="filename">The name of the file to stream.</param>
    /// <param name="ct">Cancellation token to cancel the operation.</param>
    public async Task<Stream?> GetFileStreamAsync(string baseUrl, string filename, CancellationToken ct = default)
    {
        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeouts.PrintControlTimeout);

            string encodedFilename = Uri.EscapeDataString(filename);
            Uri baseUri = new(baseUrl);
            Uri uri = new(baseUri, $"server/files/gcodes/{encodedFilename}");
            using HttpResponseMessage resp = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                return null;
            }

            // Read the content into a MemoryStream to ensure proper disposal
            byte[] content = await resp.Content.ReadAsByteArrayAsync(cts.Token);
            return new MemoryStream(content);
        }
        catch
        {
            return null;
        }
    }

    // ===== HISTORY API OPERATIONS =====

    /// <summary>
    /// List print history jobs with optional filtering parameters
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server.</param>
    /// <param name="limit">Maximum number of jobs to return.</param>
    /// <param name="start">Index to start from for pagination.</param>
    /// <param name="since">Filter jobs since this date.</param>
    /// <param name="before">Filter jobs before this date.</param>
    /// <param name="order">Sort order for the results.</param>
    /// <param name="ct">Cancellation token to cancel the operation.</param>
    public async Task<HistoryListResponse?> GetHistoryListAsync(string baseUrl, int? limit = null, int? start = null, DateTime? since = null, DateTime? before = null, string? order = null, CancellationToken ct = default)
    {
        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeouts.CommandTimeout);
            Uri baseUri = new(baseUrl);
            string relative = "server/history/list";
            List<string> queryParams = new();

            if (limit.HasValue)
            {
                queryParams.Add($"limit={limit.Value}");
            }

            if (start.HasValue)
            {
                queryParams.Add($"start={start.Value}");
            }

            if (since.HasValue)
            {
                queryParams.Add($"since={((DateTimeOffset)since.Value).ToUnixTimeSeconds()}");
            }

            if (before.HasValue)
            {
                queryParams.Add($"before={((DateTimeOffset)before.Value).ToUnixTimeSeconds()}");
            }

            if (!string.IsNullOrWhiteSpace(order))
            {
                queryParams.Add($"order={Uri.EscapeDataString(order)}");
            }

            if (queryParams.Count > 0)
            {
                relative += "?" + string.Join("&", queryParams);
            }

            Uri uri = new(baseUri, relative);
            _logger.LogInformation("[Moonraker] Fetching history from {Uri}", uri);
            using HttpResponseMessage resp = await _http.GetAsync(uri, cts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("[Moonraker] History API returned {RespStatusCode} from {Uri}", resp.StatusCode, uri);
                return null;
            }

            string content = await resp.Content.ReadAsStringAsync(cts.Token);
            _logger.LogDebug("[Moonraker] History response: {Value0}...", content.Substring(0, Math.Min(200, content.Length)));
            MoonrakerResponse<HistoryListResponse>? response = await resp.Content.ReadFromJsonAsync<MoonrakerResponse<HistoryListResponse>>(cancellationToken: cts.Token);
            if (response?.Result == null)
            {
                _logger.LogWarning($"[Moonraker] History response deserialization returned null");
                return null;
            }

            _logger.LogInformation("[Moonraker] Successfully fetched {Count} history items", response.Result.Count);
            return response.Result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogDebug("History request cancelled by user");
            throw;
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            _logger.LogWarning(ex, "[Moonraker] History request timed out for {BaseUrl}", baseUrl);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Moonraker] Failed to get history list from {BaseUrl}: {Message}", baseUrl, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Get a specific history job by job ID
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server.</param>
    /// <param name="jobId">The unique identifier of the history job.</param>
    /// <param name="ct">Cancellation token to cancel the operation.</param>
    public async Task<HistoryJob?> GetHistoryJobAsync(string baseUrl, string jobId, CancellationToken ct = default)
    {
        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeouts.CommandTimeout);

            Uri baseUri = new(baseUrl);
            Uri uri = new(baseUri, $"server/history/job?uid={Uri.EscapeDataString(jobId)}");
            using HttpResponseMessage resp = await _http.GetAsync(uri, cts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                return null;
            }

            MoonrakerResponse<HistoryJob>? response = await resp.Content.ReadFromJsonAsync<MoonrakerResponse<HistoryJob>>(cancellationToken: cts.Token);
            return response?.Result;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to get history job {JobId} from {BaseUrl}: {Message}", jobId, baseUrl, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Delete a specific history job by job ID
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server.</param>
    /// <param name="jobId">The unique identifier of the history job to delete.</param>
    /// <param name="ct">Cancellation token to cancel the operation.</param>
    public async Task<bool> DeleteHistoryJobAsync(string baseUrl, string jobId, CancellationToken ct = default)
    {
        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeouts.CommandTimeout);

            Uri baseUri = new(baseUrl);
            Uri uri = new(baseUri, $"server/history/job?uid={Uri.EscapeDataString(jobId)}");
            using HttpResponseMessage resp = await _http.DeleteAsync(uri, cts.Token);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to delete history job {JobId} from {BaseUrl}: {Message}", jobId, baseUrl, ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Get history totals and statistics
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server.</param>
    /// <param name="ct">Cancellation token to cancel the operation.</param>
    public async Task<HistoryTotals?> GetHistoryTotalsAsync(string baseUrl, CancellationToken ct = default)
    {
        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeouts.CommandTimeout);

            Uri baseUri = new(baseUrl);
            Uri uri = new(baseUri, "server/history/totals");
            using HttpResponseMessage resp = await _http.GetAsync(uri, cts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                return null;
            }

            MoonrakerResponse<HistoryTotals>? response = await resp.Content.ReadFromJsonAsync<MoonrakerResponse<HistoryTotals>>(cancellationToken: cts.Token);
            return response?.Result;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to get history totals from {BaseUrl}: {Message}", baseUrl, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Reset history totals (clears all statistics)
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server.</param>
    /// <param name="ct">Cancellation token to cancel the operation.</param>
    public async Task<bool> ResetHistoryTotalsAsync(string baseUrl, CancellationToken ct = default)
    {
        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeouts.CommandTimeout);

            Uri baseUri = new(baseUrl);
            Uri uri = new(baseUri, "server/history/reset_totals");
            using HttpResponseMessage resp = await _http.PostAsync(uri, null, cts.Token);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to reset history totals from {BaseUrl}: {Message}", baseUrl, ex.Message);
            return false;
        }
    }

    // ===== SPOOLMAN API OPERATIONS =====

    /// <summary>
    /// Get Spoolman status and connection information
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server.</param>
    /// <param name="ct">Cancellation token to cancel the operation.</param>
    public async Task<SpoolmanStatus?> GetSpoolmanStatusAsync(string baseUrl, CancellationToken ct = default)
    {
        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeouts.CommandTimeout);

            Uri baseUri = new(baseUrl);
            Uri uri = new(baseUri, "server/spoolman/status");
            using HttpResponseMessage resp = await _http.GetAsync(uri, cts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                return null;
            }

            MoonrakerResponse<SpoolmanStatus>? response = await resp.Content.ReadFromJsonAsync<MoonrakerResponse<SpoolmanStatus>>(cancellationToken: cts.Token);
            return response?.Result;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Get the currently active spool ID
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server.</param>
    /// <param name="ct">Cancellation token to cancel the operation.</param>
    public async Task<int?> GetSpoolmanActiveSpoolAsync(string baseUrl, CancellationToken ct = default)
    {
        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeouts.CommandTimeout);

            Uri baseUri = new(baseUrl);
            Uri uri = new(baseUri, "server/spoolman/spool_id");
            using HttpResponseMessage resp = await _http.GetAsync(uri, cts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                return null;
            }

            MoonrakerResponse<SpoolmanSpoolIdResponse>? response = await resp.Content.ReadFromJsonAsync<MoonrakerResponse<SpoolmanSpoolIdResponse>>(cancellationToken: cts.Token);
            return response?.Result?.SpoolId;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Set the active spool ID in Spoolman
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server.</param>
    /// <param name="spoolId">The spool ID to set as active, or null to clear.</param>
    /// <param name="ct">Cancellation token to cancel the operation.</param>
    public async Task<bool> SetSpoolmanActiveSpoolAsync(string baseUrl, int? spoolId, CancellationToken ct = default)
    {
        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeouts.CommandTimeout);
            Uri baseUri = new(baseUrl);
            Uri uri = new(baseUri, "server/spoolman/spool_id");

            // Moonraker expects spool_id as an int when setting, but the key must be
            // OMITTED (not null) to clear. Sending spool_id:null causes a 400 because
            // Moonraker's Python code tries int(None) and fails.
            object payload = spoolId.HasValue
                ? new SpoolmanSpoolIdRequest { SpoolId = spoolId }
                : new { };

            using HttpResponseMessage resp = await _http.PostAsJsonAsync(uri, payload, cts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                string body = await resp.Content.ReadAsStringAsync(cts.Token);
                _logger.LogWarning("SetSpoolmanActiveSpoolAsync: Moonraker returned {RespStatusCode} for spool_id={SpoolId} on {Uri}. Body: {Body}", (int)resp.StatusCode, spoolId, uri, body);
            }

            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SetSpoolmanActiveSpoolAsync: Exception for spool_id={SpoolId} on {BaseUrl}", spoolId, baseUrl);
            return false;
        }
    }

    /// <summary>
    /// Proxy a request to the Spoolman server
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server.</param>
    /// <param name="method">The HTTP method for the proxied request.</param>
    /// <param name="path">The Spoolman API path to request.</param>
    /// <param name="query">Optional query string parameters.</param>
    /// <param name="body">Optional request body object.</param>
    /// <param name="useV2Response">Whether to use V2 response format.</param>
    /// <param name="ct">Cancellation token to cancel the operation.</param>
    public async Task<string?> SpoolmanProxyRequestAsync(string baseUrl, string method, string path,
        string? query = null, object? body = null, bool useV2Response = false, CancellationToken ct = default)
    {
        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeouts.PrintControlTimeout);
            Uri baseUri = new(baseUrl);
            Uri uri = new(baseUri, "server/spoolman/proxy");

            // Moonraker proxy expects paths starting with /v1, not /api/v1
            string proxyPath = path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)
                ? path[4..] // Strip "/api" prefix
                : path;

            SpoolmanProxyRequest request = new()
            {
                RequestMethod = method,
                Path = proxyPath,
                Query = query,
                Body = body,
                UseV2Response = useV2Response
            };

            using HttpResponseMessage resp = await _http.PostAsJsonAsync(uri, request, cts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                return null;
            }

            string json = await resp.Content.ReadAsStringAsync(cts.Token);

            // Moonraker wraps Spoolman responses in {"result": ...} — unwrap it
            try
            {
                using JsonDocument doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("result", out JsonElement resultEl))
                {
                    return resultEl.GetRawText();
                }
            }
            catch
            {
                /* If unwrap fails, return raw */
            }

            return json;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Get all spools from Spoolman via proxy
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server.</param>
    /// <param name="ct">Cancellation token to cancel the operation.</param>
    public async Task<string?> GetSpoolmanSpoolsAsync(string baseUrl, CancellationToken ct = default)
    {
        return await SpoolmanProxyRequestAsync(baseUrl, "GET", "/api/v1/spool", ct: ct);
    }

    /// <summary>
    /// Get a specific spool by ID from Spoolman via proxy
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server.</param>
    /// <param name="spoolId">The unique identifier of the spool.</param>
    /// <param name="ct">Cancellation token to cancel the operation.</param>
    public async Task<string?> GetSpoolmanSpoolByIdAsync(string baseUrl, int spoolId, CancellationToken ct = default)
    {
        return await SpoolmanProxyRequestAsync(baseUrl, "GET", $"/api/v1/spool/{spoolId}", ct: ct);
    }

    /// <summary>
    /// Create a new spool in Spoolman via proxy
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server.</param>
    /// <param name="spoolData">The spool data to create.</param>
    /// <param name="ct">Cancellation token to cancel the operation.</param>
    public async Task<string?> CreateSpoolmanSpoolAsync(string baseUrl, object spoolData, CancellationToken ct = default)
    {
        return await SpoolmanProxyRequestAsync(baseUrl, "POST", "/api/v1/spool", body: spoolData, ct: ct);
    }

    /// <summary>
    /// Update a spool in Spoolman via proxy
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server.</param>
    /// <param name="spoolId">The unique identifier of the spool to update.</param>
    /// <param name="spoolData">The updated spool data.</param>
    /// <param name="ct">Cancellation token to cancel the operation.</param>
    public async Task<string?> UpdateSpoolmanSpoolAsync(string baseUrl, int spoolId, object spoolData, CancellationToken ct = default)
    {
        return await SpoolmanProxyRequestAsync(baseUrl, "PATCH", $"/api/v1/spool/{spoolId}", body: spoolData, ct: ct);
    }

    /// <summary>
    /// Delete a spool from Spoolman via proxy
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server.</param>
    /// <param name="spoolId">The unique identifier of the spool to delete.</param>
    /// <param name="ct">Cancellation token to cancel the operation.</param>
    public async Task<bool> DeleteSpoolmanSpoolAsync(string baseUrl, int spoolId, CancellationToken ct = default)
    {
        string? result = await SpoolmanProxyRequestAsync(baseUrl, "DELETE", $"/api/v1/spool/{spoolId}", ct: ct);
        return result != null;
    }

    /// <summary>
    /// Get all filaments from Spoolman via proxy
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server.</param>
    /// <param name="ct">Cancellation token to cancel the operation.</param>
    public async Task<string?> GetSpoolmanFilamentsAsync(string baseUrl, CancellationToken ct = default)
    {
        return await SpoolmanProxyRequestAsync(baseUrl, "GET", "/api/v1/filament", ct: ct);
    }

    /// <summary>
    /// Get a specific filament by ID from Spoolman via proxy
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server.</param>
    /// <param name="filamentId">The unique identifier of the filament.</param>
    /// <param name="ct">Cancellation token to cancel the operation.</param>
    public async Task<string?> GetSpoolmanFilamentByIdAsync(string baseUrl, int filamentId, CancellationToken ct = default)
    {
        return await SpoolmanProxyRequestAsync(baseUrl, "GET", $"/api/v1/filament/{filamentId}", ct: ct);
    }

    /// <summary>
    /// Create a new filament in Spoolman via proxy
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server.</param>
    /// <param name="filamentData">The filament data to create.</param>
    /// <param name="ct">Cancellation token to cancel the operation.</param>
    public async Task<string?> CreateSpoolmanFilamentAsync(string baseUrl, object filamentData, CancellationToken ct = default)
    {
        return await SpoolmanProxyRequestAsync(baseUrl, "POST", "/api/v1/filament", body: filamentData, ct: ct);
    }

    /// <summary>
    /// Update a filament in Spoolman via proxy
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server.</param>
    /// <param name="filamentId">The unique identifier of the filament to update.</param>
    /// <param name="filamentData">The updated filament data.</param>
    /// <param name="ct">Cancellation token to cancel the operation.</param>
    public async Task<string?> UpdateSpoolmanFilamentAsync(string baseUrl, int filamentId, object filamentData, CancellationToken ct = default)
    {
        return await SpoolmanProxyRequestAsync(baseUrl, "PATCH", $"/api/v1/filament/{filamentId}", body: filamentData, ct: ct);
    }

    /// <summary>
    /// Delete a filament from Spoolman via proxy
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server.</param>
    /// <param name="filamentId">The unique identifier of the filament to delete.</param>
    /// <param name="ct">Cancellation token to cancel the operation.</param>
    public async Task<bool> DeleteSpoolmanFilamentAsync(string baseUrl, int filamentId, CancellationToken ct = default)
    {
        string? result = await SpoolmanProxyRequestAsync(baseUrl, "DELETE", $"/api/v1/filament/{filamentId}", ct: ct);
        return result != null;
    }

    /// <summary>
    /// Get all vendors from Spoolman via proxy
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server.</param>
    /// <param name="ct">Cancellation token to cancel the operation.</param>
    public async Task<string?> GetSpoolmanVendorsAsync(string baseUrl, CancellationToken ct = default)
    {
        return await SpoolmanProxyRequestAsync(baseUrl, "GET", "/api/v1/vendor", ct: ct);
    }

    /// <summary>
    /// Get a specific vendor by ID from Spoolman via proxy
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server.</param>
    /// <param name="vendorId">The unique identifier of the vendor.</param>
    /// <param name="ct">Cancellation token to cancel the operation.</param>
    public async Task<string?> GetSpoolmanVendorByIdAsync(string baseUrl, int vendorId, CancellationToken ct = default)
    {
        return await SpoolmanProxyRequestAsync(baseUrl, "GET", $"/api/v1/vendor/{vendorId}", ct: ct);
    }

    /// <summary>
    /// Create a new vendor in Spoolman via proxy
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server.</param>
    /// <param name="vendorData">The vendor data to create.</param>
    /// <param name="ct">Cancellation token to cancel the operation.</param>
    public async Task<string?> CreateSpoolmanVendorAsync(string baseUrl, object vendorData, CancellationToken ct = default)
    {
        return await SpoolmanProxyRequestAsync(baseUrl, "POST", "/api/v1/vendor", body: vendorData, ct: ct);
    }

    /// <summary>
    /// Update a vendor in Spoolman via proxy
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server.</param>
    /// <param name="vendorId">The unique identifier of the vendor to update.</param>
    /// <param name="vendorData">The updated vendor data.</param>
    /// <param name="ct">Cancellation token to cancel the operation.</param>
    public async Task<string?> UpdateSpoolmanVendorAsync(string baseUrl, int vendorId, object vendorData, CancellationToken ct = default)
    {
        return await SpoolmanProxyRequestAsync(baseUrl, "PATCH", $"/api/v1/vendor/{vendorId}", body: vendorData, ct: ct);
    }

    /// <summary>
    /// Delete a vendor from Spoolman via proxy
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server.</param>
    /// <param name="vendorId">The unique identifier of the vendor to delete.</param>
    /// <param name="ct">Cancellation token to cancel the operation.</param>
    public async Task<bool> DeleteSpoolmanVendorAsync(string baseUrl, int vendorId, CancellationToken ct = default)
    {
        string? result = await SpoolmanProxyRequestAsync(baseUrl, "DELETE", $"/api/v1/vendor/{vendorId}", ct: ct);
        return result != null;
    }

    /// <summary>
    /// Use a specific amount of filament from the active spool
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server.</param>
    /// <param name="length">The length of filament used in millimeters.</param>
    /// <param name="ct">Cancellation token to cancel the operation.</param>
    public async Task<bool> UseSpoolmanFilamentAsync(string baseUrl, double length, CancellationToken ct = default)
    {
        var body = new { used_length = length };
        string? result = await SpoolmanProxyRequestAsync(baseUrl, "PUT", "/api/v1/spool/use", body: body, ct: ct);
        return result != null;
    }

    /// <summary>
    /// Get Spoolman server information via proxy
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server.</param>
    /// <param name="ct">Cancellation token to cancel the operation.</param>
    public async Task<string?> GetSpoolmanInfoAsync(string baseUrl, CancellationToken ct = default)
    {
        return await SpoolmanProxyRequestAsync(baseUrl, "GET", "/api/v1/info", ct: ct);
    }

    /// <summary>
    /// Get Spoolman health status via proxy
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server.</param>
    /// <param name="ct">Cancellation token to cancel the operation.</param>
    public async Task<string?> GetSpoolmanHealthAsync(string baseUrl, CancellationToken ct = default)
    {
        return await SpoolmanProxyRequestAsync(baseUrl, "GET", "/api/v1/health", ct: ct);
    }

    /// <summary>
    /// Search spools in Spoolman with optional filters via proxy
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server.</param>
    /// <param name="query">Optional search query string.</param>
    /// <param name="allowArchived">Whether to include archived spools in results.</param>
    /// <param name="limit">Maximum number of results to return.</param>
    /// <param name="offset">Number of results to skip for pagination.</param>
    /// <param name="ct">Cancellation token to cancel the operation.</param>
    public async Task<string?> SearchSpoolmanSpoolsAsync(string baseUrl, string? query = null,
        bool? allowArchived = null, int? limit = null, int? offset = null, CancellationToken ct = default)
    {
        List<string> queryParams = new();
        if (!string.IsNullOrEmpty(query))
        {
            queryParams.Add($"search={Uri.EscapeDataString(query)}");
        }

        if (allowArchived.HasValue)
        {
            queryParams.Add($"allow_archived={(allowArchived.Value ? "true" : "false")}");
        }

        if (limit.HasValue)
        {
            queryParams.Add($"limit={limit.Value}");
        }

        if (offset.HasValue)
        {
            queryParams.Add($"offset={offset.Value}");
        }

        string? queryString = queryParams.Count > 0 ? string.Join("&", queryParams) : null;
        return await SpoolmanProxyRequestAsync(baseUrl, "GET", "/api/v1/spool", query: queryString, ct: ct);
    }

    /// <summary>
    /// Search filaments in Spoolman with optional filters via proxy
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server.</param>
    /// <param name="query">Optional search query string.</param>
    /// <param name="limit">Maximum number of results to return.</param>
    /// <param name="offset">Number of results to skip for pagination.</param>
    /// <param name="ct">Cancellation token to cancel the operation.</param>
    public async Task<string?> SearchSpoolmanFilamentsAsync(string baseUrl, string? query = null,
        int? limit = null, int? offset = null, CancellationToken ct = default)
    {
        List<string> queryParams = new();
        if (!string.IsNullOrEmpty(query))
        {
            queryParams.Add($"search={Uri.EscapeDataString(query)}");
        }

        if (limit.HasValue)
        {
            queryParams.Add($"limit={limit.Value}");
        }

        if (offset.HasValue)
        {
            queryParams.Add($"offset={offset.Value}");
        }

        string? queryString = queryParams.Count > 0 ? string.Join("&", queryParams) : null;
        return await SpoolmanProxyRequestAsync(baseUrl, "GET", "/api/v1/filament", query: queryString, ct: ct);
    }

    /// <summary>
    /// Archive/unarchive a spool in Spoolman via proxy
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server.</param>
    /// <param name="spoolId">The unique identifier of the spool to archive.</param>
    /// <param name="archived">Whether to archive or unarchive the spool.</param>
    /// <param name="ct">Cancellation token to cancel the operation.</param>
    public async Task<bool> ArchiveSpoolmanSpoolAsync(string baseUrl, int spoolId, bool archived = true, CancellationToken ct = default)
    {
        var body = new { archived };
        string? result = await SpoolmanProxyRequestAsync(baseUrl, "PATCH", $"/api/v1/spool/{spoolId}", body: body, ct: ct);
        return result != null;
    }

    /// <summary>
    /// Get statistics from Spoolman via proxy
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server.</param>
    /// <param name="ct">Cancellation token to cancel the operation.</param>
    public async Task<string?> GetSpoolmanStatsAsync(string baseUrl, CancellationToken ct = default)
    {
        return await SpoolmanProxyRequestAsync(baseUrl, "GET", "/api/v1/statistics", ct: ct);
    }

    /// <summary>
    /// Backup Spoolman database via proxy
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server.</param>
    /// <param name="ct">Cancellation token to cancel the operation.</param>
    public async Task<string?> BackupSpoolmanAsync(string baseUrl, CancellationToken ct = default)
    {
        return await SpoolmanProxyRequestAsync(baseUrl, "POST", "/api/v1/backup", ct: ct);
    }

    /// <summary>
    /// Get external database integrations status from Spoolman via proxy
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server.</param>
    /// <param name="ct">Cancellation token to cancel the operation.</param>
    public async Task<string?> GetSpoolmanIntegrationsAsync(string baseUrl, CancellationToken ct = default)
    {
        return await SpoolmanProxyRequestAsync(baseUrl, "GET", "/api/v1/external", ct: ct);
    }

    // Explicit interface implementations for capability markers

    /// <summary>
    /// ISupportsFileDownload implementation - downloads a file from the printer.
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server.</param>
    /// <param name="filePath">The path of the file to download.</param>
    /// <param name="ct">Cancellation token to cancel the operation.</param>
    async Task<byte[]?> ISupportsFileDownload.DownloadFileAsync(string baseUrl, string filePath, CancellationToken ct)
        => await DownloadFileAsync(baseUrl, filePath, ct);

    /// <summary>
    /// ISupportsFileList implementation - gets the list of files on the printer.
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server.</param>
    /// <param name="credential">Optional printer credential for authentication.</param>
    /// <param name="ct">Cancellation token to cancel the operation.</param>
    async Task<List<PrinterFileInfo>> ISupportsFileList.GetFileListAsync(string baseUrl, PrinterCredential? credential = null, CancellationToken ct = default)
    {
        // Use the new method that extracts file metadata including size
        return await GetFileListWithMetadataAsync(baseUrl, ct);
    }

#pragma warning disable S1006 // Default parameters in explicit interface implementation
#pragma warning disable CA1033 // Type implements interfaces that specify default parameter values

    /// <summary>
    /// ISupportsFileUpload implementation - uploads a G-code file to the printer.
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server.</param>
    /// <param name="fileName">The name of the file to upload.</param>
    /// <param name="fileContent">The file content stream.</param>
    /// <param name="credential">Optional printer credential for authentication.</param>
    /// <param name="ct">Cancellation token to cancel the operation.</param>
    async Task<bool> ISupportsFileUpload.UploadGcodeAsync(string baseUrl, string fileName, Stream fileContent, PrinterCredential? credential = null, CancellationToken ct = default)
        => await UploadGcodeAsync(baseUrl, fileName, fileContent, ct);

    /// <summary>
    /// ISupportsStartPrint implementation - starts a print job for the specified file.
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server.</param>
    /// <param name="fileName">The name of the file to print.</param>
    /// <param name="credential">Optional printer credential for authentication.</param>
    /// <param name="ct">Cancellation token to cancel the operation.</param>
    async Task<bool> ISupportsStartPrint.StartPrintAsync(string baseUrl, string fileName, PrinterCredential? credential = null, CancellationToken ct = default)
        => await StartPrintAsync(baseUrl, fileName, ct);

    /// <summary>
    /// Uploads a G-code file and immediately starts printing it on a Moonraker printer.
    /// Moonraker does not require a delay between upload and start.
    /// </summary>
    async Task<UploadAndPrintResult> ISupportsUploadAndPrint.UploadAndStartPrintAsync(string baseUrl, string fileName, Stream fileContent, PrinterCredential? credential, IProgress<UploadAndPrintStage>? progress, CancellationToken ct)
    {
        progress?.Report(UploadAndPrintStage.Uploading);

        // Single HTTP call: upload with print=true so Moonraker starts printing immediately
        bool success = await UploadGcodeAsync(baseUrl, fileName, fileContent, print: true, ct);
        if (!success)
        {
            _logger.LogWarning("[Moonraker] UploadAndStartPrint: upload+print failed for {FileName}", fileName);
            progress?.Report(UploadAndPrintStage.Failed);
            return UploadAndPrintResult.Fail(UploadAndPrintStage.Uploading, $"Failed to upload and start print of {fileName}");
        }

        _logger.LogInformation("[Moonraker] UploadAndStartPrint: upload+print succeeded for {FileName} in single call", fileName);
        progress?.Report(UploadAndPrintStage.Completed);
        return UploadAndPrintResult.Ok();
    }

    /// <summary>
    /// ISupportsControlOperations implementations - pause, resume, and cancel operations.
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server.</param>
    /// <param name="credential">Optional printer credential for authentication.</param>
    /// <param name="ct">Cancellation token to cancel the operation.</param>
    async Task<bool> ISupportsControlOperations.PauseAsync(string baseUrl, PrinterCredential? credential = null, CancellationToken ct = default)
        => await PauseAsync(baseUrl, ct);

    async Task<bool> ISupportsControlOperations.ResumeAsync(string baseUrl, PrinterCredential? credential = null, CancellationToken ct = default)
        => await ResumeAsync(baseUrl, ct);

    async Task<bool> ISupportsControlOperations.CancelAsync(string baseUrl, PrinterCredential? credential = null, CancellationToken ct = default)
        => await CancelPrintAsync(baseUrl, ct);

    /// <summary>
    /// ISupportsCamera implementations - get camera stream and snapshot URLs.
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server.</param>
    /// <param name="frontendPort">Optional frontend port for camera URL.</param>
    /// <param name="credential">Optional printer credential for authentication.</param>
    /// <param name="ct">Cancellation token to cancel the operation.</param>
    async Task<string?> ISupportsCamera.GetCameraStreamUrlAsync(string baseUrl, int? frontendPort = null, PrinterCredential? credential = null, CancellationToken ct = default)
        => await GetCameraStreamUrlAsync(baseUrl, frontendPort, ct: ct);

    async Task<string?> ISupportsCamera.GetCameraSnapshotUrlAsync(string baseUrl, int? frontendPort = null, PrinterCredential? credential = null, CancellationToken ct = default)
        => await GetCameraSnapshotUrlAsync(baseUrl, ct: ct);

    /// <summary>
    /// ISupportsConfiguredCameraDetection implementation - detects actually configured cameras.
    /// This queries the Moonraker API to find cameras that are actually present on the printer.
    /// Returns (null, null) if no cameras are found, preventing false positives.
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server.</param>
    /// <param name="frontendPort">Optional frontend port for camera URL.</param>
    /// <param name="credential">Optional printer credential for authentication.</param>
    /// <param name="ct">Cancellation token to cancel the operation.</param>
    async Task<(string? StreamUrl, string? SnapshotUrl)> ISupportsConfiguredCameraDetection.DetectConfiguredCameraUrlsAsync(string baseUrl, int? frontendPort = null, PrinterCredential? credential = null, CancellationToken ct = default)
    {
        // Query the actual API to get configured camera URLs
        (string? stream, string? snapshot) = await GetCameraUrlsAsync(baseUrl, ct);
        return (stream, snapshot);
    }

    /// <summary>
    /// ISupportsFileMetadata implementation - gets metadata for a file on the printer.
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server.</param>
    /// <param name="filePath">The path of the file to get metadata for.</param>
    /// <param name="credential">Optional printer credential for authentication.</param>
    /// <param name="ct">Cancellation token to cancel the operation.</param>
    async Task<PrinterFileMetadata?> ISupportsFileMetadata.GetFileMetadataAsync(string baseUrl, string filePath, PrinterCredential? credential = null, CancellationToken ct = default)
    {
        GCodeMetadata? metadata = await GetFileMetadataAsync(baseUrl, filePath, ct);
        if (metadata == null)
        {
            return null;
        }

        var result = new PrinterFileMetadata
        {
            FilePath = filePath,
            PrintTime = metadata.EstimatedTime != null ? metadata.EstimatedTime.Value / 60.0 : null,
            LayerHeight = metadata.LayerHeight,
            FirstLayerExtrTemp = metadata.FirstLayerExtrTemp,
            FirstLayerBedTemp = metadata.FirstLayerBedTemp,
            ObjectHeight = metadata.ObjectHeight,
            ExtrUsedFilament = metadata.FilamentTotal
        };

        // Extract thumbnail information
        if (metadata.Thumbnails != null && metadata.Thumbnails.Length > 0)
        {
            result.Thumbnails = metadata.Thumbnails
                .Where(t => !string.IsNullOrEmpty(t.RelativePath))
                .Select(t => (t.Width, t.Height, t.RelativePath))
                .ToList();
        }

        return result;
    }

    /// <summary>
    /// ISupportsMovement implementations - home and move operations.
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server.</param>
    /// <param name="credential">Optional printer credential for authentication.</param>
    /// <param name="ct">Cancellation token to cancel the operation.</param>
    async Task<bool> ISupportsMovement.HomeAsync(string baseUrl, PrinterCredential? credential = null, CancellationToken ct = default)
        => await SendHomeAsync(baseUrl, ct);

    async Task<bool> ISupportsMovement.SendHomeAsync(string baseUrl, CancellationToken ct = default)
        => await SendHomeAsync(baseUrl, ct);

    async Task<bool> ISupportsMovement.HomeXYAsync(string baseUrl, PrinterCredential? credential, CancellationToken ct)
        => await HomeXYAsync(baseUrl, credential, ct);

    async Task<bool> ISupportsMovement.HomeZAsync(string baseUrl, PrinterCredential? credential, CancellationToken ct)
        => await HomeZAsync(baseUrl, credential, ct);

    async Task<bool> ISupportsMovement.MoveAsync(string baseUrl, double? x = null, double? y = null, double? z = null, double? f = null, PrinterCredential? credential = null, CancellationToken ct = default)
        => await MoveAsync(baseUrl, x, y, z, f, ct: ct);

    async Task<bool> ISupportsMovement.MoveToAsync(string baseUrl, double? x = null, double? y = null, double? z = null, double? f = null, PrinterCredential? credential = null, CancellationToken ct = default)
        => await MoveToAsync(baseUrl, x, y, z, f, ct);

    /// <summary>
    /// ISupportsTemperatureControl implementation - set temperatures.
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server.</param>
    /// <param name="hotendTemp">Optional target hotend temperature in Celsius.</param>
    /// <param name="bedTemp">Optional target bed temperature in Celsius.</param>
    /// <param name="credential">Optional printer credential for authentication.</param>
    /// <param name="ct">Cancellation token to cancel the operation.</param>
    async Task<bool> ISupportsTemperatureControl.SetTemperaturesAsync(string baseUrl, double? hotendTemp = null, double? bedTemp = null, PrinterCredential? credential = null, CancellationToken ct = default)
        => await SetTempsAsync(baseUrl, hotendTemp, bedTemp, ct);

    /// <summary>
    /// ISupportsHistory implementations - get and manage print history.
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server.</param>
    /// <param name="limit">Maximum number of jobs to return.</param>
    /// <param name="start">Index to start from for pagination.</param>
    /// <param name="since">Filter to only return jobs started after this UTC timestamp.</param>
    /// <param name="credential">Optional printer credential for authentication.</param>
    /// <param name="ct">Cancellation token to cancel the operation.</param>
    async Task<HistoryListResponse?> ISupportsHistory.GetHistoryListAsync(string baseUrl, int? limit = null, int? start = null, DateTime? since = null, PrinterCredential? credential = null, CancellationToken ct = default)
        => await GetHistoryListAsync(baseUrl, limit, start, since: since, ct: ct);

    async Task<HistoryJob?> ISupportsHistory.GetHistoryJobAsync(string baseUrl, string jobId, PrinterCredential? credential = null, CancellationToken ct = default)
        => await GetHistoryJobAsync(baseUrl, jobId, ct);

    async Task<HistoryTotals?> ISupportsHistory.GetHistoryTotalsAsync(string baseUrl, PrinterCredential? credential = null, CancellationToken ct = default)
        => await GetHistoryTotalsAsync(baseUrl, ct);

    async Task<bool> ISupportsHistory.DeleteHistoryJobAsync(string baseUrl, string jobId, PrinterCredential? credential = null, CancellationToken ct = default)
        => await DeleteHistoryJobAsync(baseUrl, jobId, ct);

    async Task<bool> ISupportsFileDelete.DeleteFileAsync(string baseUrl, string filePath, PrinterCredential? credential, CancellationToken ct)
        => await DeleteFileAsync(baseUrl, filePath, ct);

    /// <summary>
    /// ISupportsPrinterInformation implementation - get detailed printer information.
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server.</param>
    /// <param name="credential">Optional printer credential for authentication.</param>
    /// <param name="ct">Cancellation token to cancel the operation.</param>
    async Task<StandardPrinterInfo> ISupportsPrinterInformation.GetPrinterInformationAsync(string baseUrl, PrinterCredential? credential = null, CancellationToken ct = default)
    {
        MoonrakerPrinterInfo? info = await GetPrinterInfoAsync(baseUrl, ct);
        ServerInfo? server = await GetServerInfoAsync(baseUrl, ct);
        return new StandardPrinterInfo
        {
            Name = info?.Hostname ?? "Unknown",
            Firmware = info?.SoftwareVersion ?? "Unknown",
            BackendVersion = string.IsNullOrWhiteSpace(server?.MoonrakerVersion) ? null : server!.MoonrakerVersion,
            ApiVersion = string.IsNullOrWhiteSpace(server?.ApiVersionString) ? null : server!.ApiVersionString,
            Model = info?.ConfigFile ?? "Unknown"
        };
    }

    private async Task<ServerInfo?> GetServerInfoAsync(string baseUrl, CancellationToken ct)
    {
        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeouts.StatusPollTimeout);

            Uri baseUri = new(baseUrl);
            Uri uri = new(baseUri, "server/info");
            using HttpResponseMessage resp = await _http.GetAsync(uri, cts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                return null;
            }

            MoonrakerResponse<ServerInfo>? parsed = await resp.Content.ReadFromJsonAsync<MoonrakerResponse<ServerInfo>>(cancellationToken: cts.Token);
            return parsed?.Result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }
#pragma warning restore CA1033
#pragma warning restore S1006

    // ========== URI OVERLOADS ==========
    // These methods accept Uri objects instead of string baseUrl for analyzer CA1054 compliance

    // Status and Job Information
    public Task<PrinterStatus> GetStatusAsync(Uri baseUrl, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return GetStatusAsync(baseUrl.ToString(), ct);
    }

    public Task<MoonrakerPrinterInfo?> GetPrinterInfoAsync(Uri baseUrl, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return GetPrinterInfoAsync(baseUrl.ToString(), ct);
    }

    public Task<PrinterJob?> GetJobAsync(Uri baseUrl, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return GetJobAsync(baseUrl.ToString(), ct);
    }

    public Task<PrinterCompositeStatus> GetCompositeStatusAsync(Uri baseUrl, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return GetCompositeStatusAsync(baseUrl.ToString(), ct);
    }

    // Camera Operations
    public Task<byte[]?> GetCameraSnapshotAsync(Uri baseUrl, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return GetCameraSnapshotAsync(baseUrl.ToString(), ct);
    }

    public Task<(string? StreamUrl, string? SnapshotUrl)> GetConfiguredCameraUrlsAsync(Uri baseUrl, int? frontendPort = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return GetConfiguredCameraUrlsAsync(baseUrl.ToString(), frontendPort, ct);
    }

    // Printer Control Operations
    public Task<bool> SendHomeAsync(Uri baseUrl, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return SendHomeAsync(baseUrl.ToString(), ct);
    }

    public Task<bool> HomeXYAsync(Uri baseUrl, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return HomeXYAsync(baseUrl.ToString(), ct);
    }

    public Task<bool> HomeZAsync(Uri baseUrl, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return HomeZAsync(baseUrl.ToString(), ct);
    }

    public Task<bool> SetTempsAsync(Uri baseUrl, double? hotend = null, double? bed = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return SetTempsAsync(baseUrl.ToString(), hotend, bed, ct);
    }

    public Task<bool> MoveAsync(Uri baseUrl, double? x = null, double? y = null, double? z = null, double? f = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return MoveAsync(baseUrl.ToString(), x, y, z, f, ct);
    }

    public Task<bool> MoveToAsync(Uri baseUrl, double? x = null, double? y = null, double? z = null, double? f = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return MoveToAsync(baseUrl.ToString(), x, y, z, f, ct);
    }

    // Print Job Control
    public Task<bool> PauseAsync(Uri baseUrl, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return PauseAsync(baseUrl.ToString(), ct);
    }

    public Task<bool> ResumeAsync(Uri baseUrl, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return ResumeAsync(baseUrl.ToString(), ct);
    }

    public Task<bool> EmergencyStopAsync(Uri baseUrl, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return EmergencyStopAsync(baseUrl.ToString(), ct);
    }

    public Task<bool> StartPrintAsync(Uri baseUrl, string fileName, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(fileName);
        return StartPrintAsync(baseUrl.ToString(), fileName, ct);
    }

    // File Operations
    public Task<string[]> GetFileListAsync(Uri baseUrl, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return GetFileListAsync(baseUrl.ToString(), ct);
    }

    public Task<FileRoot[]> GetFileRootsAsync(Uri baseUrl, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return GetFileRootsAsync(baseUrl.ToString(), ct);
    }

    public Task<MoonrakerDirectoryInfo?> GetDirectoryAsync(Uri baseUrl, string path, bool extended = false, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(path);
        return GetDirectoryAsync(baseUrl.ToString(), path, extended, ct);
    }

    public Task<DirectoryCreateResponse?> CreateDirectoryAsync(Uri baseUrl, string path, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(path);
        return CreateDirectoryAsync(baseUrl.ToString(), path, ct);
    }

    public Task<bool> DeleteFileOrDirectoryAsync(Uri baseUrl, string path, bool force = false, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(path);
        return DeleteFileOrDirectoryAsync(baseUrl.ToString(), path, force, ct);
    }

    public Task<bool> MoveFileAsync(Uri baseUrl, string source, string dest, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(dest);
        return MoveFileAsync(baseUrl.ToString(), source, dest, ct);
    }

    public Task<bool> CopyFileAsync(Uri baseUrl, string source, string dest, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(dest);
        return CopyFileAsync(baseUrl.ToString(), source, dest, ct);
    }

    public Task<bool> DeleteFileAsync(Uri baseUrl, string path, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(path);
        return DeleteFileAsync(baseUrl.ToString(), path, ct);
    }

    public Task<Stream?> GetFileStreamAsync(Uri baseUrl, string filename, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(filename);
        return GetFileStreamAsync(baseUrl.ToString(), filename, ct);
    }

    // File Metadata and Content
    public Task<GCodeMetadata?> GetFileMetadataAsync(Uri baseUrl, string filename, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(filename);
        return GetFileMetadataAsync(baseUrl.ToString(), filename, ct);
    }

    public Task<bool> StartMetadataScanAsync(Uri baseUrl, string filename, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(filename);
        return StartMetadataScanAsync(baseUrl.ToString(), filename, ct);
    }

    public Task<List<(int Width, int Height, string RelativePath)>> GetFileThumbnailsAsync(Uri baseUrl, string filename, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(filename);
        return GetFileThumbnailsAsync(baseUrl.ToString(), filename, ct);
    }

    public Task<byte[]?> GetFileThumbnailAsync(Uri baseUrl, string filename, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(filename);
        return GetFileThumbnailAsync(baseUrl.ToString(), filename, ct);
    }

    public Task<string?> GetFileThumbnailUrlAsync(Uri baseUrl, string filename, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(filename);
        return GetFileThumbnailUrlAsync(baseUrl.ToString(), filename, ct);
    }

    public Task<byte[]?> DownloadFileAsync(Uri baseUrl, string filename, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(filename);
        return DownloadFileAsync(baseUrl.ToString(), filename, ct);
    }

    public Task<MoonrakerFileInfo[]> GetDetailedFileListAsync(Uri baseUrl, string root = "gcodes", string? path = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return GetDetailedFileListAsync(baseUrl.ToString(), root, path, ct);
    }

    // File Uploads
    public Task<bool> UploadGcodeAsync(Uri baseUrl, string fileName, Stream fileContent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(fileContent);
        return UploadGcodeAsync(baseUrl.ToString(), fileName, fileContent, ct);
    }

    public Task<FileUploadResponse?> UploadFileAsync(Uri baseUrl, string root, string filename, Stream content, bool print = false, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(filename);
        ArgumentNullException.ThrowIfNull(content);
        return UploadFileAsync(baseUrl.ToString(), root, filename, content, print, ct);
    }

    public Task<FileUploadResponse?> UploadFileWithPathAsync(Uri baseUrl, string path, Stream content, bool print = false, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(content);
        return UploadFileWithPathAsync(baseUrl.ToString(), path, content, print, ct);
    }

    // History Operations
    public Task<HistoryListResponse?> GetHistoryListAsync(Uri baseUrl, int? limit = null, int? start = null, DateTime? since = null, DateTime? before = null, string? order = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return GetHistoryListAsync(baseUrl.ToString(), limit, start, since, before, order, ct);
    }

    public Task<HistoryJob?> GetHistoryJobAsync(Uri baseUrl, string jobId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(jobId);
        return GetHistoryJobAsync(baseUrl.ToString(), jobId, ct);
    }

    public Task<bool> DeleteHistoryJobAsync(Uri baseUrl, string jobId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(jobId);
        return DeleteHistoryJobAsync(baseUrl.ToString(), jobId, ct);
    }

    public Task<HistoryTotals?> GetHistoryTotalsAsync(Uri baseUrl, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return GetHistoryTotalsAsync(baseUrl.ToString(), ct);
    }

    public Task<bool> ResetHistoryTotalsAsync(Uri baseUrl, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return ResetHistoryTotalsAsync(baseUrl.ToString(), ct);
    }

    // Spoolman Integration
    public Task<SpoolmanStatus?> GetSpoolmanStatusAsync(Uri baseUrl, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return GetSpoolmanStatusAsync(baseUrl.ToString(), ct);
    }

    public Task<int?> GetSpoolmanActiveSpoolAsync(Uri baseUrl, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return GetSpoolmanActiveSpoolAsync(baseUrl.ToString(), ct);
    }

    public Task<bool> SetSpoolmanActiveSpoolAsync(Uri baseUrl, int? spoolId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return SetSpoolmanActiveSpoolAsync(baseUrl.ToString(), spoolId, ct);
    }

    public Task<string?> SpoolmanProxyRequestAsync(Uri baseUrl, string method, string path, string? query = null, object? body = null, bool useV2Response = false, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(path);
        return SpoolmanProxyRequestAsync(baseUrl.ToString(), method, path, query, body, useV2Response, ct);
    }

    // Spoolman Spool Operations
    public Task<string?> GetSpoolmanSpoolsAsync(Uri baseUrl, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return GetSpoolmanSpoolsAsync(baseUrl.ToString(), ct);
    }

    public Task<string?> GetSpoolmanSpoolByIdAsync(Uri baseUrl, int spoolId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return GetSpoolmanSpoolByIdAsync(baseUrl.ToString(), spoolId, ct);
    }

    public Task<string?> CreateSpoolmanSpoolAsync(Uri baseUrl, object spoolData, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(spoolData);
        return CreateSpoolmanSpoolAsync(baseUrl.ToString(), spoolData, ct);
    }

    public Task<string?> UpdateSpoolmanSpoolAsync(Uri baseUrl, int spoolId, object spoolData, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(spoolData);
        return UpdateSpoolmanSpoolAsync(baseUrl.ToString(), spoolId, spoolData, ct);
    }

    public Task<bool> DeleteSpoolmanSpoolAsync(Uri baseUrl, int spoolId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return DeleteSpoolmanSpoolAsync(baseUrl.ToString(), spoolId, ct);
    }

    // Spoolman Filament Operations
    public Task<string?> GetSpoolmanFilamentsAsync(Uri baseUrl, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return GetSpoolmanFilamentsAsync(baseUrl.ToString(), ct);
    }

    public Task<string?> GetSpoolmanFilamentByIdAsync(Uri baseUrl, int filamentId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return GetSpoolmanFilamentByIdAsync(baseUrl.ToString(), filamentId, ct);
    }

    public Task<string?> CreateSpoolmanFilamentAsync(Uri baseUrl, object filamentData, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(filamentData);
        return CreateSpoolmanFilamentAsync(baseUrl.ToString(), filamentData, ct);
    }

    public Task<string?> UpdateSpoolmanFilamentAsync(Uri baseUrl, int filamentId, object filamentData, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(filamentData);
        return UpdateSpoolmanFilamentAsync(baseUrl.ToString(), filamentId, filamentData, ct);
    }

    public Task<bool> DeleteSpoolmanFilamentAsync(Uri baseUrl, int filamentId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return DeleteSpoolmanFilamentAsync(baseUrl.ToString(), filamentId, ct);
    }

    // Spoolman Vendor Operations
    public Task<string?> GetSpoolmanVendorsAsync(Uri baseUrl, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return GetSpoolmanVendorsAsync(baseUrl.ToString(), ct);
    }

    public Task<string?> GetSpoolmanVendorByIdAsync(Uri baseUrl, int vendorId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return GetSpoolmanVendorByIdAsync(baseUrl.ToString(), vendorId, ct);
    }

    public Task<string?> CreateSpoolmanVendorAsync(Uri baseUrl, object vendorData, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(vendorData);
        return CreateSpoolmanVendorAsync(baseUrl.ToString(), vendorData, ct);
    }

    public Task<string?> UpdateSpoolmanVendorAsync(Uri baseUrl, int vendorId, object vendorData, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(vendorData);
        return UpdateSpoolmanVendorAsync(baseUrl.ToString(), vendorId, vendorData, ct);
    }

    public Task<bool> DeleteSpoolmanVendorAsync(Uri baseUrl, int vendorId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return DeleteSpoolmanVendorAsync(baseUrl.ToString(), vendorId, ct);
    }

    // Spoolman Utility and Advanced Operations
    public Task<bool> UseSpoolmanFilamentAsync(Uri baseUrl, double length, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return UseSpoolmanFilamentAsync(baseUrl.ToString(), length, ct);
    }

    public Task<string?> GetSpoolmanInfoAsync(Uri baseUrl, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return GetSpoolmanInfoAsync(baseUrl.ToString(), ct);
    }

    public Task<string?> GetSpoolmanHealthAsync(Uri baseUrl, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return GetSpoolmanHealthAsync(baseUrl.ToString(), ct);
    }

    public Task<string?> SearchSpoolmanSpoolsAsync(Uri baseUrl, string? query = null, bool? allowArchived = null, int? limit = null, int? offset = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return SearchSpoolmanSpoolsAsync(baseUrl.ToString(), query, allowArchived, limit, offset, ct);
    }

    public Task<string?> SearchSpoolmanFilamentsAsync(Uri baseUrl, string? query = null, int? limit = null, int? offset = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return SearchSpoolmanFilamentsAsync(baseUrl.ToString(), query, limit, offset, ct);
    }

    public Task<bool> ArchiveSpoolmanSpoolAsync(Uri baseUrl, int spoolId, bool archived = true, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return ArchiveSpoolmanSpoolAsync(baseUrl.ToString(), spoolId, archived, ct);
    }

    public Task<string?> GetSpoolmanStatsAsync(Uri baseUrl, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return GetSpoolmanStatsAsync(baseUrl.ToString(), ct);
    }

    public Task<string?> BackupSpoolmanAsync(Uri baseUrl, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return BackupSpoolmanAsync(baseUrl.ToString(), ct);
    }

    public Task<string?> GetSpoolmanIntegrationsAsync(Uri baseUrl, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return GetSpoolmanIntegrationsAsync(baseUrl.ToString(), ct);
    }

    /// <summary>
    /// ISupportsFilamentUsageQuery implementation — uses Moonraker history API to get actual filament usage.
    /// Converts mm extrusion to grams using slicer metadata (filament_weight_total / filament_total ratio).
    /// </summary>
#pragma warning disable CA1033
    async Task<double?> ISupportsFilamentUsageQuery.GetLastJobFilamentUsageGramsAsync(string baseUrl, PrinterCredential? credential, CancellationToken ct)
#pragma warning restore CA1033
    {
        try
        {
            // Get most recent completed job from history
            HistoryListResponse? history = await GetHistoryListAsync(baseUrl, limit: 1, order: "desc", ct: ct);
            HistoryJob? lastJob = history?.Jobs?.FirstOrDefault();
            if (lastJob is null || lastJob.FilamentUsed <= 0)
            {
                return null;
            }

            double actualMm = lastJob.FilamentUsed;

            // Try to get metadata for mm→grams conversion
            if (!string.IsNullOrEmpty(lastJob.Filename))
            {
                try
                {
                    GCodeMetadata? metadata = await GetFileMetadataAsync(baseUrl, lastJob.Filename, ct);
                    if (metadata?.FilamentWeightTotal is > 0 && metadata?.FilamentTotal is > 0)
                    {
                        // Convert using slicer's mm/grams ratio
                        double gramsPerMm = metadata.FilamentWeightTotal.Value / metadata.FilamentTotal.Value;
                        return actualMm * gramsPerMm;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Could not fetch metadata for {Filename}, falling back to mm value", lastJob.Filename);
                }
            }

            // Fallback: return mm value with a note — caller should handle conversion
            // Standard PLA 1.75mm: ~1g per 335mm, but this varies by material
            _logger.LogWarning("No slicer metadata available for mm→grams conversion, using default PLA density estimate");
            return actualMm / 335.0; // Rough PLA estimate
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get filament usage from Moonraker history");
            return null;
        }
    }

    /// <summary>
    /// Retrieves per-extruder filament usage for the most recently completed print job.
    /// Checks Moonraker history metadata for per-extruder keys (filament_used_0, filament_used_1, etc.)
    /// and converts from mm to grams using slicer metadata ratio.
    /// Returns null if per-extruder data is unavailable (falls back to single-total).
    /// </summary>
#pragma warning disable CA1033
    async Task<Dictionary<int, double>?> ISupportsPerExtruderFilamentUsage.GetLastJobFilamentUsagePerExtruderAsync(
        string baseUrl, PrinterCredential? credential, CancellationToken ct)
#pragma warning restore CA1033
    {
        try
        {
            // 1. Get most recent completed job from history
            HistoryListResponse? history = await GetHistoryListAsync(baseUrl, limit: 1, order: "desc", ct: ct);
            HistoryJob? lastJob = history?.Jobs?.FirstOrDefault();
            if (lastJob is null)
            {
                return null;
            }

            // 2. Get file metadata for mm-to-grams conversion ratio
            GCodeMetadata? metadata = null;
            double gramsPerMm = 1.0 / 335.0; // PLA default fallback
            if (!string.IsNullOrEmpty(lastJob.Filename))
            {
                try
                {
                    metadata = await GetFileMetadataAsync(baseUrl, lastJob.Filename, ct);
                    if (metadata?.FilamentWeightTotal is > 0 && metadata?.FilamentTotal is > 0)
                    {
                        gramsPerMm = metadata.FilamentWeightTotal.Value / metadata.FilamentTotal.Value;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Could not fetch metadata for per-extruder conversion, using default density");
                }
            }

            // 3. Try to get per-extruder data from history job metadata
            if (lastJob.Metadata is { Count: > 0 })
            {
                var perExtruder = new Dictionary<int, double>();

                // Check for filament_used_X keys (some Moonraker versions with multi-extruder support)
                // Reasonable max toolheads = 16
                for (int i = 0; i < 16; i++)
                {
                    if (lastJob.Metadata.TryGetValue($"filament_used_{i}", out object? val))
                    {
                        double mm = 0;
                        if (val is JsonElement je && je.ValueKind == JsonValueKind.Number && je.TryGetDouble(out double jeDouble))
                        {
                            mm = jeDouble;
                        }
                        else if (val is double dbl)
                        {
                            mm = dbl;
                        }
                        else if (val is int intVal)
                        {
                            mm = intVal;
                        }
                        else if (val is long longVal)
                        {
                            mm = longVal;
                        }

                        if (mm > 0)
                        {
                            perExtruder[i] = mm * gramsPerMm;
                        }
                    }
                }

                // Only return if we found multiple extruders (multi-toolhead scenario)
                if (perExtruder.Count > 1)
                {
                    return perExtruder;
                }
            }

            // No per-extruder data available; caller will use single-total fallback
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get per-extruder filament usage from Moonraker history");
            return null;
        }
    }

    /// <summary>
    /// Response model for thumbnail information from Moonraker API
    /// </summary>
    /// <param name="Width">The width of the thumbnail in pixels.</param>
    /// <param name="Height">The height of the thumbnail in pixels.</param>
    /// <param name="Size">The size of the thumbnail in bytes.</param>
    /// <param name="RelativePath">The relative path to the thumbnail file.</param>
    private record ThumbnailInfo(
        int Width,
        int Height,
        long Size,
        [property: System.Text.Json.Serialization.JsonPropertyName("thumbnail_path")]
        string RelativePath);
}
