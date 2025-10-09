using System.Text;
using System.Text.Json;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Services.Interfaces;

namespace Farm.Web.Api.Services;

public record PrinterStatus(bool IsOnline, string? State);
#pragma warning disable CA1056 // URI-like properties should not be strings
public record PrinterJob(string? PrintState, double? Progress, string? JobName, string? ThumbnailUrl);
public record PrinterCompositeStatus(
    bool IsOnline,
    string? State,
    double? Progress,
    string? JobName,
    string? ThumbnailUrl,
    string? CameraStreamUrl,
    string? CameraSnapshotUrl,
    double? X = null,
    double? Y = null,
    double? Z = null,
    double? HotendTemp = null,
    double? BedTemp = null,
    double? HotendTarget = null,
    double? BedTarget = null);
#pragma warning restore CA1056 // URI-like properties should not be strings

public partial class MoonrakerClient(HttpClient http, IUnifiedLoggingService logger) : PrinterClientBase, IMoonrakerClient
{
    private readonly HttpClient _http = http;
    private readonly IUnifiedLoggingService _logger = logger;

    private static string NormalizeBaseUrl(string url) => NormalizeBaseUrl(url, 7125);

    public async Task<PrinterStatus> GetStatusAsync(string baseUrl, CancellationToken ct = default)
    {
        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            Uri baseUri = new(NormalizeBaseUrl(baseUrl));
            Uri uri = new(baseUri, "printer/info");
            using HttpResponseMessage resp = await _http.GetAsync(uri, cts.Token);
            if (!resp.IsSuccessStatusCode)
            {
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

            return new PrinterStatus(true, state);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Expected when cancellation is requested
            throw;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, $"Failed to get printer status from {baseUrl}");
            return new PrinterStatus(false, null);
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            _logger.LogDebug(ex, $"Failed to get printer status from {baseUrl}");
            return new PrinterStatus(false, null);
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, $"Failed to get printer status from {baseUrl}");
            return new PrinterStatus(false, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, $"Failed to get printer status from {baseUrl}");
            return new PrinterStatus(false, null);
        }
    }

    public async Task<MoonrakerPrinterInfo?> GetPrinterInfoAsync(string baseUrl, CancellationToken ct = default)
    {
        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            Uri baseUri = new(NormalizeBaseUrl(baseUrl));
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
            _logger.LogDebug(ex, $"Failed to get printer info from {baseUrl}");
            return null;
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            _logger.LogDebug(ex, $"Failed to get printer info from {baseUrl}");
            return null;
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, $"Failed to get printer info from {baseUrl}");
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, $"Failed to get printer info from {baseUrl}");
            return null;
        }
    }

    public async Task<PrinterJob?> GetJobAsync(string baseUrl, CancellationToken ct = default)
    {
        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            Uri baseUri = new(NormalizeBaseUrl(baseUrl));
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
                if (statusEl.TryGetProperty("print_stats", out JsonElement ps) &&
                    ps.TryGetProperty("filename", out JsonElement fn) && fn.ValueKind == JsonValueKind.String)
                {
                    jobName = fn.GetString();
                }
            }

            // Try Klipper job queue for thumbnail path
            if (result.TryGetProperty("job_queue", out JsonElement jq) && jq.ValueKind == JsonValueKind.Object &&
                jq.TryGetProperty("thumbnails", out JsonElement thumbs) && thumbs.ValueKind == JsonValueKind.Array && thumbs.GetArrayLength() > 0)
            {
                JsonElement first = thumbs[0];
                if (first.TryGetProperty("relative_path", out JsonElement rp) && rp.ValueKind == JsonValueKind.String)
                {
                    Uri baseUri2 = new(NormalizeBaseUrl(baseUrl));
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
                    Uri baseUri3 = new(NormalizeBaseUrl(baseUrl));
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
                                Uri baseUriX = new(NormalizeBaseUrl(baseUrl));
                                Uri thumbUri2 = new(baseUriX, $"server/files/gcodes/{Uri.EscapeDataString(rp.GetString()!)}");
                                thumb = thumbUri2.ToString();
                            }
                        }
                    }
                }
                catch { }
            }

            return new PrinterJob(state, progress, jobName, thumb);
        }
        catch
        {
            return null;
        }
    }

    private async Task<string?> GetCameraSnapshotUrlAsync(string baseUrl, CancellationToken ct = default)
    {
        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            Uri baseUri = new(NormalizeBaseUrl(baseUrl));
            Uri listUri = new(baseUri, "server/webcams/list");
            using HttpResponseMessage resp = await _http.GetAsync(listUri, cts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                return null;
            }

            await using Stream stream = await resp.Content.ReadAsStreamAsync(cts.Token);
            using JsonDocument doc = await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token);
            JsonElement root = doc.RootElement;
            if ((root.TryGetProperty("webcams", out JsonElement cams) && cams.ValueKind == JsonValueKind.Array) ||
                (root.TryGetProperty("result", out JsonElement res) && res.ValueKind == JsonValueKind.Object && res.TryGetProperty("webcams", out cams) && cams.ValueKind == JsonValueKind.Array))
            {
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

                    // Try direct snapshot_url from listing
                    if (cam.TryGetProperty("snapshot_url", out JsonElement sn) && sn.ValueKind == JsonValueKind.String)
                    {
                        string? s = sn.GetString();
                        if (!string.IsNullOrWhiteSpace(s))
                        {
                            string baseNormSnap = NormalizeBaseUrl(baseUrl);
                            return NormalizeCameraUrl(s, baseNormSnap);
                        }
                    }

                    // Prefer resolved URLs via /server/webcams/test using uid or name
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
                        ? new Uri(new Uri(NormalizeBaseUrl(baseUrl)), $"server/webcams/test?uid={Uri.EscapeDataString(uid)}")
                        : (name is not null ? new Uri(new Uri(NormalizeBaseUrl(baseUrl)), $"server/webcams/test?name={Uri.EscapeDataString(name)}") : null);
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

                                if (troot.TryGetProperty("snapshot_url", out JsonElement tsu) && tsu.ValueKind == JsonValueKind.String)
                                {
                                    string? s = tsu.GetString();
                                    if (!string.IsNullOrWhiteSpace(s))
                                    {
                                        string baseNormLocal = NormalizeBaseUrl(baseUrl);
                                        return NormalizeCameraUrl(s, baseNormLocal);
                                    }
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
        }
        catch { }
        return null;
    }

    public async Task<byte[]?> GetCameraSnapshotAsync(string baseUrl, CancellationToken ct = default)
    {
        try
        {
            string? url = await GetCameraSnapshotUrlAsync(baseUrl, ct);
            if (string.IsNullOrWhiteSpace(url))
            {
                return null;
            }

            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            using HttpResponseMessage resp = await _http.GetAsync(new Uri(url!, UriKind.RelativeOrAbsolute), cts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                return null;
            }

            return await resp.Content.ReadAsByteArrayAsync(cts.Token);
        }
        catch { return null; }
    }

    public async Task<PrinterCompositeStatus> GetCompositeStatusAsync(string baseUrl, CancellationToken ct = default)
    {
        PrinterStatus status = await GetStatusAsync(baseUrl, ct);
        PrinterJob? job = await GetJobAsync(baseUrl, ct);
        // Try to read current position
        double? x = null, y = null, z = null;
        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            Uri baseUri = new(NormalizeBaseUrl(baseUrl));
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
                    { x = pos[0].GetDouble(); }
                    catch { }
                    try
                    { y = pos[1].GetDouble(); }
                    catch { }
                    try
                    { z = pos[2].GetDouble(); }
                    catch { }
                }
            }
        }
        catch
        {
        }
        string? state = job?.PrintState ?? status.State; // prefer print job state (printing, paused, complete) over system state
        // Query temps
        double? hotend = null, bed = null, hotendT = null, bedT = null;
        try
        {
            using CancellationTokenSource cts2 = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts2.CancelAfter(TimeSpan.FromSeconds(5));
            Uri baseUri2 = new(NormalizeBaseUrl(baseUrl));
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
                        { try { hotend = t.GetDouble(); } catch { } }
                        if (ex.TryGetProperty("target", out JsonElement tt) && tt.ValueKind is JsonValueKind.Number)
                        { try { hotendT = tt.GetDouble(); } catch { } }
                    }
                    if (status2.TryGetProperty("heater_bed", out JsonElement hb))
                    {
                        if (hb.TryGetProperty("temperature", out JsonElement t) && t.ValueKind is JsonValueKind.Number)
                        { try { bed = t.GetDouble(); } catch { } }
                        if (hb.TryGetProperty("target", out JsonElement tt) && tt.ValueKind is JsonValueKind.Number)
                        { try { bedT = tt.GetDouble(); } catch { } }
                    }
                }
            }
        }
        catch { }

        // Query camera info when online; webcam listing may still be available via Moonraker
        string? cam = null;
        string? snap = null;
        if (status.IsOnline)
        {
            (string? streamUrl, string? snapshotUrl) = await GetCameraUrlsAsync(baseUrl, ct);
            cam = streamUrl;
            snap = snapshotUrl;
        }
        return new PrinterCompositeStatus(status.IsOnline, state, job?.Progress, job?.JobName, job?.ThumbnailUrl, cam, snap, x, y, z, hotend, bed, hotendT, bedT);
    }

    public async Task<bool> SendHomeAsync(string baseUrl, CancellationToken ct = default)
        => await SendGcodeAsync(baseUrl, "G28", ct);

    public async Task<bool> HomeXYAsync(string baseUrl, CancellationToken ct = default)
        => await SendGcodeAsync(baseUrl, "G28 X Y", ct);

    public async Task<bool> HomeZAsync(string baseUrl, CancellationToken ct = default)
        => await SendGcodeAsync(baseUrl, "G28 Z", ct);

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

        return await SendGcodeAsync(baseUrl, cmds, ct);
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
        return await SendGcodeAsync(baseUrl, cmds, ct);
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

        return await SendGcodeAsync(baseUrl, string.Join(' ', parts), ct);
    }

    public async Task<bool> PauseAsync(string baseUrl, CancellationToken ct = default)
        => await SendGcodeAsync(baseUrl, "PAUSE", ct);

    public async Task<bool> ResumeAsync(string baseUrl, CancellationToken ct = default)
        => await SendGcodeAsync(baseUrl, "RESUME", ct);

    public async Task<bool> EmergencyStopAsync(string baseUrl, CancellationToken ct = default)
        => await SendGcodeAsync(baseUrl, "M112", ct);

    public async Task<bool> FirmwareRestartAsync(string baseUrl, CancellationToken ct = default)
        => await SendGcodeAsync(baseUrl, "FIRMWARE_RESTART", ct);

    public Task<bool> FirmwareRestartAsync(Uri baseUrl, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return FirmwareRestartAsync(baseUrl.ToString(), ct);
    }

    private async Task<bool> SendGcodeAsync(string baseUrl, string gcode, CancellationToken ct = default)
        => await SendGcodeAsync(baseUrl, new[] { gcode }, ct);

    private async Task<bool> SendGcodeAsync(string baseUrl, IEnumerable<string> gcodes, CancellationToken ct = default)
    {
        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            Uri baseUri4 = new(NormalizeBaseUrl(baseUrl));
            Uri scriptUri = new(baseUri4, "printer/gcode/script");
            using HttpResponseMessage resp = await _http.PostAsJsonAsync(scriptUri, new { script = string.Join("\n", gcodes) }, cts.Token);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    // Unified camera URL resolver: fetches both stream and snapshot from a single listing call, with test-resolution fallback
    private async Task<(string? stream, string? snapshot)> GetCameraUrlsAsync(string baseUrl, CancellationToken ct = default)
    {
        string? stream = null;
        string? snapshot = null;
        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            Uri baseUri = new(NormalizeBaseUrl(baseUrl));
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

            string baseNorm = NormalizeBaseUrl(baseUrl);
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
                    ? new Uri(new Uri(baseNorm), $"server/webcams/test?uid={Uri.EscapeDataString(uid)}")
                    : (name is not null ? new Uri(new Uri(baseNorm), $"server/webcams/test?name={Uri.EscapeDataString(name)}") : null);
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
                                stream = NormalizeCameraUrl(tsu.GetString(), baseNorm);
                            }

                            if (snapshot is null && troot.TryGetProperty("snapshot_url", out JsonElement ssu) && ssu.ValueKind == JsonValueKind.String)
                            {
                                snapshot = NormalizeCameraUrl(ssu.GetString(), baseNorm);
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
                        stream = NormalizeCameraUrl(s, baseNorm);
                    }
                }
                if (snapshot is null && cam.TryGetProperty("snapshot_url", out JsonElement sn) && sn.ValueKind == JsonValueKind.String)
                {
                    string? s = sn.GetString();
                    if (!string.IsNullOrWhiteSpace(s))
                    {
                        snapshot = NormalizeCameraUrl(s, baseNorm);
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
            _logger.LogDebug(ex, $"Failed to get camera URLs from {baseUrl}");
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            _logger.LogDebug(ex, $"Failed to get camera URLs from {baseUrl}");
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, $"Failed to get camera URLs from {baseUrl}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, $"Failed to get camera URLs from {baseUrl}");
        }
        return (stream, snapshot);
    }

    // File upload and management methods
    public async Task<bool> UploadGcodeAsync(string baseUrl, string fileName, Stream fileContent, CancellationToken ct = default)
    {
        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30)); // Allow more time for file uploads
            Uri baseUri = new(NormalizeBaseUrl(baseUrl));
            Uri uri = new(baseUri, "server/files/upload");

            using MultipartFormDataContent formContent = new();
            using StreamContent streamContent = new(fileContent);
            streamContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            formContent.Add(streamContent, "file", fileName);
            formContent.Add(new StringContent("gcodes"), "root"); // Upload to gcodes directory

            using HttpResponseMessage resp = await _http.PostAsync(uri, formContent, cts.Token);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> StartPrintAsync(string baseUrl, string fileName, CancellationToken ct = default)
    {
        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            Uri baseUri = new(NormalizeBaseUrl(baseUrl));
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
        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            Uri baseUri = new(NormalizeBaseUrl(baseUrl));
            Uri uri = new(baseUri, "server/files/list?root=gcodes");
            using HttpResponseMessage resp = await _http.GetAsync(uri, cts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                return Array.Empty<string>();
            }

            await using Stream stream = await resp.Content.ReadAsStreamAsync(cts.Token);
            using JsonDocument doc = await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token);
            JsonElement root = doc.RootElement;

            if (!root.TryGetProperty("result", out JsonElement result) ||
                result.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<string>();
            }

            List<string> files = new();
            foreach (JsonElement file in result.EnumerateArray())
            {
                if (file.TryGetProperty("path", out JsonElement path) &&
                    path.ValueKind == JsonValueKind.String)
                {
                    string? fileName = path.GetString();
                    if (!string.IsNullOrEmpty(fileName) && fileName.EndsWith(".gcode", StringComparison.OrdinalIgnoreCase))
                    {
                        files.Add(fileName);
                    }
                }
            }
            return files.ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    // ===== FILE OPERATIONS API =====

    /// <summary>
    /// Get list of available file roots
    /// </summary>
    public async Task<FileRoot[]> GetFileRootsAsync(string baseUrl, CancellationToken ct = default)
    {
        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            Uri baseUri = new(NormalizeBaseUrl(baseUrl));
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
    public async Task<DirectoryInfo?> GetDirectoryAsync(string baseUrl, string path, bool extended = false, CancellationToken ct = default)
    {
        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            // First try using REST API
            string encodedPath = Uri.EscapeDataString(path);
            Uri baseUri = new(NormalizeBaseUrl(baseUrl));
            Uri uri = new(baseUri, $"server/files/directory?path={encodedPath}&extended={(extended ? "true" : "false")}");

            using HttpResponseMessage resp = await _http.GetAsync(uri, cts.Token);
            if (resp.IsSuccessStatusCode)
            {
                try
                {
                    MoonrakerResponse<DirectoryInfo>? response = await resp.Content.ReadFromJsonAsync<MoonrakerResponse<DirectoryInfo>>(cancellationToken: cts.Token);
                    if (response?.Result != null)
                    {
                        return response.Result;
                    }
                }
                catch (JsonException ex)
                {
                    _logger.LogDebug($"Error parsing directory info from REST API: {ex.Message}");
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
                    _logger.LogDebug($"JSON-RPC error for {jsonRpcRequest.Method}: {jsonRpcResponse.Error.Message} (Code: {jsonRpcResponse.Error.Code})");

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
                            _logger.LogDebug($"JSON-RPC error for {jsonRpcRequest.Method}: {jsonRpcResponse.Error.Message} (Code: {jsonRpcResponse.Error.Code})");
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

                // Deserialize the result to DirectoryInfo
                string? resultJson = jsonRpcResponse.Result.ToString();
                DirectoryInfo? directoryInfo = JsonSerializer.Deserialize<DirectoryInfo>(resultJson ?? "{}");
                return directoryInfo;
            }
            catch (JsonException jex)
            {
                _logger.LogDebug(jex, $"Failed to parse JSON response: {jex.Message}");
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
            _logger.LogDebug(ex, $"Failed to get directory from {baseUrl}: {ex.Message}");
            return null;
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            _logger.LogDebug(ex, $"Failed to get directory from {baseUrl}: {ex.Message}");
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Catch any remaining exceptions (JSON serialization errors, etc.) to ensure method resilience
            _logger.LogDebug(ex, $"Failed to get directory from {baseUrl}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Create a new directory
    /// </summary>
    public async Task<DirectoryCreateResponse?> CreateDirectoryAsync(string baseUrl, string path, CancellationToken ct = default)
    {
        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            Uri baseUri = new(NormalizeBaseUrl(baseUrl));
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
    public async Task<bool> DeleteFileOrDirectoryAsync(string baseUrl, string path, bool force = false, CancellationToken ct = default)
    {
        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            string encodedPath = Uri.EscapeDataString(path);
            Uri baseUri = new(NormalizeBaseUrl(baseUrl));
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
    public async Task<bool> MoveFileAsync(string baseUrl, string source, string dest, CancellationToken ct = default)
    {
        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            Uri baseUri = new(NormalizeBaseUrl(baseUrl));
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
    public async Task<bool> CopyFileAsync(string baseUrl, string source, string dest, CancellationToken ct = default)
    {
        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            Uri baseUri = new(NormalizeBaseUrl(baseUrl));
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
    public async Task<GCodeMetadata?> GetFileMetadataAsync(string baseUrl, string filename, CancellationToken ct = default)
    {
        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            string encodedFilename = Uri.EscapeDataString(filename);
            Uri baseUri = new(NormalizeBaseUrl(baseUrl));
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
    /// Start a metadata scan for a file
    /// </summary>
    public async Task<bool> StartMetadataScanAsync(string baseUrl, string filename, CancellationToken ct = default)
    {
        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            Uri baseUri = new(NormalizeBaseUrl(baseUrl));
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
            _logger.LogDebug(ex, $"Failed to start metadata scan for {filename} at {baseUrl}");
            return false;
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            _logger.LogDebug(ex, $"Failed to start metadata scan for {filename} at {baseUrl}");
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, $"Failed to start metadata scan for {filename} at {baseUrl}");
            return false;
        }
    }

    /// <summary>
    /// Get a file thumbnail
    /// </summary>
    public async Task<byte[]?> GetFileThumbnailAsync(string baseUrl, string filename, CancellationToken ct = default)
    {
        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            string encodedFilename = Uri.EscapeDataString(filename);
            Uri baseUri = new(NormalizeBaseUrl(baseUrl));
            Uri uri = new(baseUri, $"server/files/thumbs/{encodedFilename}");
            using HttpResponseMessage resp = await _http.GetAsync(uri, cts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                return null;
            }

            return await resp.Content.ReadAsByteArrayAsync(cts.Token);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Download a file
    /// </summary>
    public async Task<byte[]?> DownloadFileAsync(string baseUrl, string filename, CancellationToken ct = default)
    {
        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30)); // Allow more time for downloads

            string encodedFilename = Uri.EscapeDataString(filename);
            Uri baseUri = new(NormalizeBaseUrl(baseUrl));
            Uri uri = new(baseUri, $"server/files/gcodes/{encodedFilename}");
            using HttpResponseMessage resp = await _http.GetAsync(uri, cts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                return null;
            }

            return await resp.Content.ReadAsByteArrayAsync(cts.Token);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Upload a file to a specific root directory
    /// </summary>
    public async Task<FileUploadResponse?> UploadFileAsync(string baseUrl, string root, string filename, Stream content,
        bool print = false, CancellationToken ct = default)
    {
        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(60)); // Allow more time for uploads
            Uri baseUri = new(NormalizeBaseUrl(baseUrl));
            Uri uri = new(baseUri, "server/files/upload");

            using MultipartFormDataContent formContent = new();
            using StreamContent streamContent = new(content);
            streamContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
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
    public async Task<FileUploadResponse?> UploadFileWithPathAsync(string baseUrl, string path, Stream content,
        bool print = false, CancellationToken ct = default)
    {
        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(60));
            Uri baseUri = new(NormalizeBaseUrl(baseUrl));
            Uri uri = new(baseUri, "server/files/upload");

            using MultipartFormDataContent formContent = new();
            using StreamContent streamContent = new(content);
            streamContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");

            string filename = System.IO.Path.GetFileName(path);
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
    public async Task<MoonrakerFileInfo[]> GetDetailedFileListAsync(string baseUrl, string root = "gcodes", string? path = null, CancellationToken ct = default)
    {
        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            Uri baseUri = new(NormalizeBaseUrl(baseUrl));
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
    public async Task<bool> DeleteFileAsync(string baseUrl, string path, CancellationToken ct = default)
    {
        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            string encodedPath = Uri.EscapeDataString(path);
            Uri baseUri = new(NormalizeBaseUrl(baseUrl));
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
    public async Task<Stream?> GetFileStreamAsync(string baseUrl, string filename, CancellationToken ct = default)
    {
        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30));

            string encodedFilename = Uri.EscapeDataString(filename);
            Uri baseUri = new(NormalizeBaseUrl(baseUrl));
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
    public async Task<HistoryListResponse?> GetHistoryListAsync(string baseUrl, int? limit = null, int? start = null, DateTime? since = null, DateTime? before = null, string? order = null, CancellationToken ct = default)
    {
        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            Uri baseUri = new(NormalizeBaseUrl(baseUrl));
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
            using HttpResponseMessage resp = await _http.GetAsync(uri, cts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                return null;
            }

            MoonrakerResponse<HistoryListResponse>? response = await resp.Content.ReadFromJsonAsync<MoonrakerResponse<HistoryListResponse>>(cancellationToken: cts.Token);
            return response?.Result;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, $"Failed to get history list from {baseUrl}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Get a specific history job by job ID
    /// </summary>
    public async Task<HistoryJob?> GetHistoryJobAsync(string baseUrl, string jobId, CancellationToken ct = default)
    {
        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            Uri baseUri = new(NormalizeBaseUrl(baseUrl));
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
            _logger.LogDebug(ex, $"Failed to get history job {jobId} from {baseUrl}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Delete a specific history job by job ID
    /// </summary>
    public async Task<bool> DeleteHistoryJobAsync(string baseUrl, string jobId, CancellationToken ct = default)
    {
        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            Uri baseUri = new(NormalizeBaseUrl(baseUrl));
            Uri uri = new(baseUri, $"server/history/job?uid={Uri.EscapeDataString(jobId)}");
            using HttpResponseMessage resp = await _http.DeleteAsync(uri, cts.Token);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, $"Failed to delete history job {jobId} from {baseUrl}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Get history totals and statistics
    /// </summary>
    public async Task<HistoryTotals?> GetHistoryTotalsAsync(string baseUrl, CancellationToken ct = default)
    {
        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            Uri baseUri = new(NormalizeBaseUrl(baseUrl));
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
            _logger.LogDebug(ex, $"Failed to get history totals from {baseUrl}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Reset history totals (clears all statistics)
    /// </summary>
    public async Task<bool> ResetHistoryTotalsAsync(string baseUrl, CancellationToken ct = default)
    {
        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            Uri baseUri = new(NormalizeBaseUrl(baseUrl));
            Uri uri = new(baseUri, "server/history/reset_totals");
            using HttpResponseMessage resp = await _http.PostAsync(uri, null, cts.Token);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, $"Failed to reset history totals from {baseUrl}: {ex.Message}");
            return false;
        }
    }

    // ===== SPOOLMAN API OPERATIONS =====

    /// <summary>
    /// Get Spoolman status and connection information
    /// </summary>
    public async Task<SpoolmanStatus?> GetSpoolmanStatusAsync(string baseUrl, CancellationToken ct = default)
    {
        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            Uri baseUri = new(NormalizeBaseUrl(baseUrl));
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
    public async Task<int?> GetSpoolmanActiveSpoolAsync(string baseUrl, CancellationToken ct = default)
    {
        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            Uri baseUri = new(NormalizeBaseUrl(baseUrl));
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
    public async Task<bool> SetSpoolmanActiveSpoolAsync(string baseUrl, int? spoolId, CancellationToken ct = default)
    {
        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            Uri baseUri = new(NormalizeBaseUrl(baseUrl));
            Uri uri = new(baseUri, "server/spoolman/spool_id");
            SpoolmanSpoolIdRequest request = new()
            { SpoolId = spoolId };
            using HttpResponseMessage resp = await _http.PostAsJsonAsync(uri, request, cts.Token);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Proxy a request to the Spoolman server
    /// </summary>
    public async Task<string?> SpoolmanProxyRequestAsync(string baseUrl, string method, string path,
        string? query = null, object? body = null, bool useV2Response = false, CancellationToken ct = default)
    {
        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30)); // Allow more time for proxy requests
            Uri baseUri = new(NormalizeBaseUrl(baseUrl));
            Uri uri = new(baseUri, "server/spoolman/proxy");
            SpoolmanProxyRequest request = new()
            {
                RequestMethod = method,
                Path = path,
                Query = query,
                Body = body,
                UseV2Response = useV2Response
            };

            using HttpResponseMessage resp = await _http.PostAsJsonAsync(uri, request, cts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                return null;
            }

            return await resp.Content.ReadAsStringAsync(cts.Token);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Get all spools from Spoolman via proxy
    /// </summary>
    public async Task<string?> GetSpoolmanSpoolsAsync(string baseUrl, CancellationToken ct = default)
    {
        return await SpoolmanProxyRequestAsync(baseUrl, "GET", "/api/v1/spool", ct: ct);
    }

    /// <summary>
    /// Get a specific spool by ID from Spoolman via proxy
    /// </summary>
    public async Task<string?> GetSpoolmanSpoolByIdAsync(string baseUrl, int spoolId, CancellationToken ct = default)
    {
        return await SpoolmanProxyRequestAsync(baseUrl, "GET", $"/api/v1/spool/{spoolId}", ct: ct);
    }

    /// <summary>
    /// Create a new spool in Spoolman via proxy
    /// </summary>
    public async Task<string?> CreateSpoolmanSpoolAsync(string baseUrl, object spoolData, CancellationToken ct = default)
    {
        return await SpoolmanProxyRequestAsync(baseUrl, "POST", "/api/v1/spool", body: spoolData, ct: ct);
    }

    /// <summary>
    /// Update a spool in Spoolman via proxy
    /// </summary>
    public async Task<string?> UpdateSpoolmanSpoolAsync(string baseUrl, int spoolId, object spoolData, CancellationToken ct = default)
    {
        return await SpoolmanProxyRequestAsync(baseUrl, "PATCH", $"/api/v1/spool/{spoolId}", body: spoolData, ct: ct);
    }

    /// <summary>
    /// Delete a spool from Spoolman via proxy
    /// </summary>
    public async Task<bool> DeleteSpoolmanSpoolAsync(string baseUrl, int spoolId, CancellationToken ct = default)
    {
        string? result = await SpoolmanProxyRequestAsync(baseUrl, "DELETE", $"/api/v1/spool/{spoolId}", ct: ct);
        return result != null;
    }

    /// <summary>
    /// Get all filaments from Spoolman via proxy
    /// </summary>
    public async Task<string?> GetSpoolmanFilamentsAsync(string baseUrl, CancellationToken ct = default)
    {
        return await SpoolmanProxyRequestAsync(baseUrl, "GET", "/api/v1/filament", ct: ct);
    }

    /// <summary>
    /// Get a specific filament by ID from Spoolman via proxy
    /// </summary>
    public async Task<string?> GetSpoolmanFilamentByIdAsync(string baseUrl, int filamentId, CancellationToken ct = default)
    {
        return await SpoolmanProxyRequestAsync(baseUrl, "GET", $"/api/v1/filament/{filamentId}", ct: ct);
    }

    /// <summary>
    /// Create a new filament in Spoolman via proxy
    /// </summary>
    public async Task<string?> CreateSpoolmanFilamentAsync(string baseUrl, object filamentData, CancellationToken ct = default)
    {
        return await SpoolmanProxyRequestAsync(baseUrl, "POST", "/api/v1/filament", body: filamentData, ct: ct);
    }

    /// <summary>
    /// Update a filament in Spoolman via proxy
    /// </summary>
    public async Task<string?> UpdateSpoolmanFilamentAsync(string baseUrl, int filamentId, object filamentData, CancellationToken ct = default)
    {
        return await SpoolmanProxyRequestAsync(baseUrl, "PATCH", $"/api/v1/filament/{filamentId}", body: filamentData, ct: ct);
    }

    /// <summary>
    /// Delete a filament from Spoolman via proxy
    /// </summary>
    public async Task<bool> DeleteSpoolmanFilamentAsync(string baseUrl, int filamentId, CancellationToken ct = default)
    {
        string? result = await SpoolmanProxyRequestAsync(baseUrl, "DELETE", $"/api/v1/filament/{filamentId}", ct: ct);
        return result != null;
    }

    /// <summary>
    /// Get all vendors from Spoolman via proxy
    /// </summary>
    public async Task<string?> GetSpoolmanVendorsAsync(string baseUrl, CancellationToken ct = default)
    {
        return await SpoolmanProxyRequestAsync(baseUrl, "GET", "/api/v1/vendor", ct: ct);
    }

    /// <summary>
    /// Get a specific vendor by ID from Spoolman via proxy
    /// </summary>
    public async Task<string?> GetSpoolmanVendorByIdAsync(string baseUrl, int vendorId, CancellationToken ct = default)
    {
        return await SpoolmanProxyRequestAsync(baseUrl, "GET", $"/api/v1/vendor/{vendorId}", ct: ct);
    }

    /// <summary>
    /// Create a new vendor in Spoolman via proxy
    /// </summary>
    public async Task<string?> CreateSpoolmanVendorAsync(string baseUrl, object vendorData, CancellationToken ct = default)
    {
        return await SpoolmanProxyRequestAsync(baseUrl, "POST", "/api/v1/vendor", body: vendorData, ct: ct);
    }

    /// <summary>
    /// Update a vendor in Spoolman via proxy
    /// </summary>
    public async Task<string?> UpdateSpoolmanVendorAsync(string baseUrl, int vendorId, object vendorData, CancellationToken ct = default)
    {
        return await SpoolmanProxyRequestAsync(baseUrl, "PATCH", $"/api/v1/vendor/{vendorId}", body: vendorData, ct: ct);
    }

    /// <summary>
    /// Delete a vendor from Spoolman via proxy
    /// </summary>
    public async Task<bool> DeleteSpoolmanVendorAsync(string baseUrl, int vendorId, CancellationToken ct = default)
    {
        string? result = await SpoolmanProxyRequestAsync(baseUrl, "DELETE", $"/api/v1/vendor/{vendorId}", ct: ct);
        return result != null;
    }

    /// <summary>
    /// Use a specific amount of filament from the active spool
    /// </summary>
    public async Task<bool> UseSpoolmanFilamentAsync(string baseUrl, double length, CancellationToken ct = default)
    {
        var body = new { used_length = length };
        string? result = await SpoolmanProxyRequestAsync(baseUrl, "PUT", "/api/v1/spool/use", body: body, ct: ct);
        return result != null;
    }

    /// <summary>
    /// Get Spoolman server information via proxy
    /// </summary>
    public async Task<string?> GetSpoolmanInfoAsync(string baseUrl, CancellationToken ct = default)
    {
        return await SpoolmanProxyRequestAsync(baseUrl, "GET", "/api/v1/info", ct: ct);
    }

    /// <summary>
    /// Get Spoolman health status via proxy
    /// </summary>
    public async Task<string?> GetSpoolmanHealthAsync(string baseUrl, CancellationToken ct = default)
    {
        return await SpoolmanProxyRequestAsync(baseUrl, "GET", "/api/v1/health", ct: ct);
    }

    /// <summary>
    /// Search spools in Spoolman with optional filters via proxy
    /// </summary>
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
    public async Task<bool> ArchiveSpoolmanSpoolAsync(string baseUrl, int spoolId, bool archived = true, CancellationToken ct = default)
    {
        var body = new { archived };
        string? result = await SpoolmanProxyRequestAsync(baseUrl, "PATCH", $"/api/v1/spool/{spoolId}", body: body, ct: ct);
        return result != null;
    }

    /// <summary>
    /// Get statistics from Spoolman via proxy
    /// </summary>
    public async Task<string?> GetSpoolmanStatsAsync(string baseUrl, CancellationToken ct = default)
    {
        return await SpoolmanProxyRequestAsync(baseUrl, "GET", "/api/v1/statistics", ct: ct);
    }

    /// <summary>
    /// Backup Spoolman database via proxy
    /// </summary>
    public async Task<string?> BackupSpoolmanAsync(string baseUrl, CancellationToken ct = default)
    {
        return await SpoolmanProxyRequestAsync(baseUrl, "POST", "/api/v1/backup", ct: ct);
    }

    /// <summary>
    /// Get external database integrations status from Spoolman via proxy  
    /// </summary>
    public async Task<string?> GetSpoolmanIntegrationsAsync(string baseUrl, CancellationToken ct = default)
    {
        return await SpoolmanProxyRequestAsync(baseUrl, "GET", "/api/v1/external", ct: ct);
    }
}

/// <summary>
/// Extension methods for history-related data conversion
/// </summary>
public static class HistoryExtensions
{
    /// <summary>
    /// Convert Unix timestamp (seconds) to DateTime
    /// </summary>
    public static DateTime ToDateTime(this double unixTimestamp)
    {
        return DateTimeOffset.FromUnixTimeSeconds((long)unixTimestamp).UtcDateTime;
    }

    /// <summary>
    /// Convert Unix timestamp (seconds) to DateTime, handling null values
    /// </summary>
    public static DateTime? ToDateTime(this double? unixTimestamp)
    {
        return unixTimestamp?.ToDateTime();
    }

    /// <summary>
    /// Get the start time as DateTime
    /// </summary>
    public static DateTime GetStartTimeAsDateTime(this HistoryJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        return job.StartTime.ToDateTime();
    }

    /// <summary>
    /// Get the end time as DateTime, if available
    /// </summary>
    public static DateTime? GetEndTimeAsDateTime(this HistoryJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        return job.EndTime?.ToDateTime();
    }

    /// <summary>
    /// Get print duration as TimeSpan
    /// </summary>
    public static TimeSpan GetPrintDuration(this HistoryJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        return TimeSpan.FromSeconds(job.PrintDuration);
    }

    /// <summary>
    /// Get total duration as TimeSpan
    /// </summary>
    public static TimeSpan GetTotalDuration(this HistoryJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        return TimeSpan.FromSeconds(job.TotalDuration);
    }

    /// <summary>
    /// Check if the job was completed successfully
    /// </summary>
    public static bool IsCompleted(this HistoryJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        return string.Equals(job.Status, "completed", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Check if the job was cancelled
    /// </summary>
    public static bool IsCancelled(this HistoryJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        return string.Equals(job.Status, "cancelled", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Check if the job had an error
    /// </summary>
    public static bool IsError(this HistoryJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        return string.Equals(job.Status, "error", StringComparison.OrdinalIgnoreCase);
    }
}
