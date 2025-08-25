using System.Net.Http.Json;
using System.Text.Json;

namespace Farm.Web.Server.Services;

public record PrinterStatus(bool IsOnline, string? State);
public record PrinterJob(string? PrintState, double? Progress, string? JobName, string? ThumbnailUrl);
public record PrinterCompositeStatus(
    bool IsOnline,
    string? State,
    double? Progress,
    string? JobName,
    string? ThumbnailUrl,
    string? CameraStreamUrl,
    double? X = null,
    double? Y = null,
    double? Z = null,
    double? HotendTemp = null,
    double? BedTemp = null,
    double? HotendTarget = null,
    double? BedTarget = null);

public class MoonrakerClient(HttpClient http)
{
    private static string NormalizeBaseUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return url;
        var trimmed = url.Trim();
        if (!trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = "http://" + trimmed;
        }
        try
        {
            var ub = new UriBuilder(trimmed);
            if (ub.Port == -1)
            {
                ub.Port = 7125;
            }
            return ub.Uri.ToString().TrimEnd('/');
        }
        catch
        {
            return url.TrimEnd('/');
        }
    }

    public async Task<PrinterStatus> GetStatusAsync(string baseUrl, CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            var url = $"{NormalizeBaseUrl(baseUrl)}/printer/info";
            using var resp = await http.GetAsync(url, cts.Token);
            if (!resp.IsSuccessStatusCode) return new PrinterStatus(false, null);
            await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token);
            string? state = null;
            var root = doc.RootElement;
            if (root.TryGetProperty("state", out var s1) && s1.ValueKind == JsonValueKind.String)
                state = s1.GetString();
            else if (root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object &&
                     result.TryGetProperty("state", out var s2) && s2.ValueKind == JsonValueKind.String)
                state = s2.GetString();
            return new PrinterStatus(true, state);
        }
        catch
        {
            return new PrinterStatus(false, null);
        }
    }

    public async Task<PrinterJob?> GetJobAsync(string baseUrl, CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            var url = $"{NormalizeBaseUrl(baseUrl)}/printer/objects/query?print_stats&display_status&job_queue";
            using var resp = await http.GetAsync(url, cts.Token);
            if (!resp.IsSuccessStatusCode) return null;
            await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token);
            var root = doc.RootElement;
            if (!root.TryGetProperty("result", out var result)) return null;
            string? state = null;
            if (result.TryGetProperty("status", out var statusNode) &&
                statusNode.ValueKind == JsonValueKind.Object &&
                statusNode.TryGetProperty("print_stats", out var psNode) &&
                psNode.ValueKind == JsonValueKind.Object &&
                psNode.TryGetProperty("state", out var stNode) &&
                stNode.ValueKind == JsonValueKind.String)
            {
                state = stNode.GetString();
            }
            // Only report job details when printing
            if (!string.Equals(state, "printing", StringComparison.OrdinalIgnoreCase))
                return new PrinterJob(state, null, null, null);

            double? progress = null;
            string? jobName = null;
            string? thumb = null;

            if (result.TryGetProperty("status", out var statusEl))
            {
                if (statusEl.TryGetProperty("display_status", out var display) &&
                    display.TryGetProperty("progress", out var prog))
                {
                    double pv;
                    try { pv = prog.GetDouble(); }
                    catch { pv = 0; }
                    progress = pv > 1.0 ? pv : pv * 100.0; // support 0..1 or 0..100
                }
                if (statusEl.TryGetProperty("print_stats", out var ps))
                {
                    if (ps.TryGetProperty("filename", out var fn) && fn.ValueKind == JsonValueKind.String)
                        jobName = fn.GetString();
                }
            }

            // Try Klipper job queue for thumbnail path
            if (result.TryGetProperty("job_queue", out var jq) && jq.ValueKind == JsonValueKind.Object)
            {
                if (jq.TryGetProperty("thumbnails", out var thumbs) && thumbs.ValueKind == JsonValueKind.Array && thumbs.GetArrayLength() > 0)
                {
                    var first = thumbs[0];
                    if (first.TryGetProperty("relative_path", out var rp) && rp.ValueKind == JsonValueKind.String)
                    {
                        var baseNormalized = NormalizeBaseUrl(baseUrl);
                        thumb = $"{baseNormalized}/server/files/gcodes/{Uri.EscapeDataString(rp.GetString()!)}";
                    }
                }
            }

            // Fallback: query file metadata for thumbnails if not found yet
            if (thumb is null && !string.IsNullOrWhiteSpace(jobName))
            {
                try
                {
                    var metaUrl = $"{NormalizeBaseUrl(baseUrl)}/server/files/metadata?filename={Uri.EscapeDataString(jobName)}";
                    using var mresp = await http.GetAsync(metaUrl, cts.Token);
                    if (mresp.IsSuccessStatusCode)
                    {
                        await using var mstream = await mresp.Content.ReadAsStreamAsync(cts.Token);
                        using var mdoc = await JsonDocument.ParseAsync(mstream, cancellationToken: cts.Token);
                        var mroot = mdoc.RootElement;
                        if (mroot.TryGetProperty("result", out var mres) &&
                            mres.TryGetProperty("thumbnails", out var mthumbs) &&
                            mthumbs.ValueKind == JsonValueKind.Array && mthumbs.GetArrayLength() > 0)
                        {
                            var first = mthumbs[0];
                            if (first.TryGetProperty("relative_path", out var rp) && rp.ValueKind == JsonValueKind.String)
                            {
                                var baseNorm = NormalizeBaseUrl(baseUrl);
                                thumb = $"{baseNorm}/server/files/gcodes/{Uri.EscapeDataString(rp.GetString()!)}";
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

    private async Task<string?> GetCameraStreamUrlAsync(string baseUrl, CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            var url = $"{NormalizeBaseUrl(baseUrl)}/server/webcams/list";
            using var resp = await http.GetAsync(url, cts.Token);
            if (!resp.IsSuccessStatusCode) return null;
            await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token);
            var root = doc.RootElement;
            // Spec may be { webcams: [...] } or { result: { webcams: [...] } }
            JsonElement cams;
            if ((root.TryGetProperty("webcams", out cams) && cams.ValueKind == JsonValueKind.Array) ||
                (root.TryGetProperty("result", out var res) && res.ValueKind == JsonValueKind.Object && res.TryGetProperty("webcams", out cams) && cams.ValueKind == JsonValueKind.Array))
            {
                foreach (var cam in cams.EnumerateArray())
                {
                    bool enabled = true;
                    if (cam.TryGetProperty("enabled", out var en))
                    {
                        if (en.ValueKind == JsonValueKind.False) enabled = false;
                        else if (en.ValueKind == JsonValueKind.True) enabled = true;
                    }
                    if (!enabled) continue;
                    // Prefer resolved urls via /server/webcams/test using uid when available
                    string? uid = null;
                    if (cam.TryGetProperty("uid", out var uidEl) && uidEl.ValueKind == JsonValueKind.String)
                        uid = uidEl.GetString();
                    string? name = null;
                    if (cam.TryGetProperty("name", out var nmEl) && nmEl.ValueKind == JsonValueKind.String)
                        name = nmEl.GetString();

                    try
                    {
                        var baseNorm = NormalizeBaseUrl(baseUrl);
                        var testUrl = uid is not null
                            ? $"{baseNorm}/server/webcams/test?uid={Uri.EscapeDataString(uid)}"
                            : (name is not null ? $"{baseNorm}/server/webcams/test?name={Uri.EscapeDataString(name)}" : null);
                        if (testUrl is not null)
                        {
                            using var tresp = await http.PostAsync(testUrl, content: null, cts.Token);
                            if (tresp.IsSuccessStatusCode)
                            {
                                await using var tstream = await tresp.Content.ReadAsStreamAsync(cts.Token);
                                using var tdoc = await JsonDocument.ParseAsync(tstream, cancellationToken: cts.Token);
                                var troot = tdoc.RootElement;
                                // May be wrapped or direct
                                if (troot.TryGetProperty("result", out var tresult))
                                    troot = tresult;
                                if (troot.TryGetProperty("stream_url", out var tsu) && tsu.ValueKind == JsonValueKind.String)
                                {
                                    var s = tsu.GetString();
                                    if (!string.IsNullOrWhiteSpace(s)) return s;
                                }
                            }
                        }
                    }
                    catch { }

                    // Fallback: try raw stream/snapshot_url from listing
                    string? urlStr = null;
                    if (cam.TryGetProperty("stream_url", out var su) && su.ValueKind == JsonValueKind.String)
                        urlStr = su.GetString();
                    if (string.IsNullOrWhiteSpace(urlStr) && cam.TryGetProperty("snapshot_url", out var sn) && sn.ValueKind == JsonValueKind.String)
                        urlStr = sn.GetString();
                    if (!string.IsNullOrWhiteSpace(urlStr))
                    {
                        var s = urlStr!;
                        if (Uri.TryCreate(s, UriKind.Absolute, out var abs)) return abs.ToString();
                        var baseNorm = NormalizeBaseUrl(baseUrl);
                        var rel = s.StartsWith('/') ? s : "/" + s;
                        return baseNorm + rel;
                    }
                }
            }
            // If list is empty or doesn't include a usable URL, try common fallback endpoints
            var baseNorm2 = NormalizeBaseUrl(baseUrl);
            var guesses = new (string snapshot, string stream)[]
            {
                ("/webcam/?action=snapshot", "/webcam/?action=stream"),
                ("/webcam?action=snapshot", "/webcam?action=stream"),
                ("/webcam/snapshot", "/webcam/stream"),
            };
            foreach (var g in guesses)
            {
                try
                {
                    using var headReq = new HttpRequestMessage(HttpMethod.Head, baseNorm2 + g.snapshot);
                    using var headResp = await http.SendAsync(headReq, cts.Token);
                    if (headResp.IsSuccessStatusCode)
                        return baseNorm2 + g.stream;
                }
                catch { /* ignore and continue */ }
                // Try lightweight GET if HEAD not supported
                try
                {
                    using var getReq = new HttpRequestMessage(HttpMethod.Get, baseNorm2 + g.snapshot);
                    using var getResp = await http.SendAsync(getReq, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                    if (getResp.IsSuccessStatusCode)
                    {
                        if (getResp.Content.Headers.ContentType?.MediaType is string mt &&
                            (mt.StartsWith("image/", StringComparison.OrdinalIgnoreCase) || mt.Contains("multipart", StringComparison.OrdinalIgnoreCase)))
                        {
                            return baseNorm2 + g.stream;
                        }
                        // Some cams don't set content-type correctly; accept success anyway
                        return baseNorm2 + g.stream;
                    }
                }
                catch { /* ignore and continue */ }
            }
        }
        catch { }
        return null;
    }

    public async Task<PrinterCompositeStatus> GetCompositeStatusAsync(string baseUrl, CancellationToken ct = default)
    {
        var status = await GetStatusAsync(baseUrl, ct);
        var job = await GetJobAsync(baseUrl, ct);
        // Try to read current position
        double? x=null, y=null, z=null;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            var url = $"{NormalizeBaseUrl(baseUrl)}/printer/objects/query?toolhead=position";
            using var resp = await http.GetAsync(url, cts.Token);
            if (resp.IsSuccessStatusCode)
            {
                await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token);
                var root = doc.RootElement;
                if (root.TryGetProperty("result", out var result) &&
                    result.TryGetProperty("status", out var statusNode) &&
                    statusNode.TryGetProperty("toolhead", out var th) &&
                    th.TryGetProperty("position", out var pos) && pos.ValueKind == JsonValueKind.Array && pos.GetArrayLength()>=3)
                {
                    try { x = pos[0].GetDouble(); } catch {}
                    try { y = pos[1].GetDouble(); } catch {}
                    try { z = pos[2].GetDouble(); } catch {}
                }
            }
        }
        catch { }
        var state = job?.PrintState ?? status.State; // prefer print job state when available
        // Query temps
        double? hotend=null, bed=null, hotendT=null, bedT=null;
        try
        {
            using var cts2 = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts2.CancelAfter(TimeSpan.FromSeconds(5));
            var url2 = $"{NormalizeBaseUrl(baseUrl)}/printer/objects/query?extruder&heater_bed";
            using var resp2 = await http.GetAsync(url2, cts2.Token);
            if (resp2.IsSuccessStatusCode)
            {
                await using var stream2 = await resp2.Content.ReadAsStreamAsync(cts2.Token);
                using var doc2 = await JsonDocument.ParseAsync(stream2, cancellationToken: cts2.Token);
                var root2 = doc2.RootElement;
                if (root2.TryGetProperty("result", out var result2) && result2.TryGetProperty("status", out var status2))
                {
                    if (status2.TryGetProperty("extruder", out var ex))
                    {
                        if (ex.TryGetProperty("temperature", out var t) && t.ValueKind is JsonValueKind.Number) { try { hotend = t.GetDouble(); } catch { } }
                        if (ex.TryGetProperty("target", out var tt) && tt.ValueKind is JsonValueKind.Number) { try { hotendT = tt.GetDouble(); } catch { } }
                    }
                    if (status2.TryGetProperty("heater_bed", out var hb))
                    {
                        if (hb.TryGetProperty("temperature", out var t) && t.ValueKind is JsonValueKind.Number) { try { bed = t.GetDouble(); } catch { } }
                        if (hb.TryGetProperty("target", out var tt) && tt.ValueKind is JsonValueKind.Number) { try { bedT = tt.GetDouble(); } catch { } }
                    }
                }
            }
        }
        catch { }

        // Only query camera when online
        string? cam = null;
        if (status.IsOnline)
        {
            cam = await GetCameraStreamUrlAsync(baseUrl, ct);
        }
        return new PrinterCompositeStatus(status.IsOnline, state, job?.Progress, job?.JobName, job?.ThumbnailUrl, cam, x, y, z, hotend, bed, hotendT, bedT);
    }

    public async Task<bool> SendHomeAsync(string baseUrl, CancellationToken ct = default)
        => await SendGcodeAsync(baseUrl, "G28", ct);

    public async Task<bool> HomeXYAsync(string baseUrl, CancellationToken ct = default)
        => await SendGcodeAsync(baseUrl, "G28 X Y", ct);

    public async Task<bool> HomeZAsync(string baseUrl, CancellationToken ct = default)
        => await SendGcodeAsync(baseUrl, "G28 Z", ct);

    public async Task<bool> SetTempsAsync(string baseUrl, double? hotend = null, double? bed = null, CancellationToken ct = default)
    {
        var cmds = new List<string>();
        if (hotend is not null) cmds.Add($"M104 S{hotend:0}");
        if (bed is not null) cmds.Add($"M140 S{bed:0}");
        return await SendGcodeAsync(baseUrl, cmds, ct);
    }

    public async Task<bool> MoveAsync(string baseUrl, double? x = null, double? y = null, double? z = null, double? f = null, CancellationToken ct = default)
    {
        var parts = new List<string>{"G91","G0"};
        if (x is not null) parts.Add($"X{x:0.###}");
        if (y is not null) parts.Add($"Y{y:0.###}");
        if (z is not null) parts.Add($"Z{z:0.###}");
        if (f is not null) parts.Add($"F{f:0.###}");
        var cmds = new []{ string.Join(' ', parts), "G90" };
        return await SendGcodeAsync(baseUrl, cmds, ct);
    }

    public async Task<bool> MoveToAsync(string baseUrl, double? x = null, double? y = null, double? z = null, double? f = null, CancellationToken ct = default)
    {
        var parts = new List<string>{"G90","G0"};
        if (x is not null) parts.Add($"X{x:0.###}");
        if (y is not null) parts.Add($"Y{y:0.###}");
        if (z is not null) parts.Add($"Z{z:0.###}");
        if (f is not null) parts.Add($"F{f:0.###}");
        return await SendGcodeAsync(baseUrl, string.Join(' ', parts), ct);
    }

    public async Task<bool> PauseAsync(string baseUrl, CancellationToken ct = default)
        => await SendGcodeAsync(baseUrl, "PAUSE", ct);

    public async Task<bool> ResumeAsync(string baseUrl, CancellationToken ct = default)
        => await SendGcodeAsync(baseUrl, "RESUME", ct);

    public async Task<bool> EmergencyStopAsync(string baseUrl, CancellationToken ct = default)
        => await SendGcodeAsync(baseUrl, "M112", ct);

    private async Task<bool> SendGcodeAsync(string baseUrl, string gcode, CancellationToken ct = default)
        => await SendGcodeAsync(baseUrl, new []{ gcode }, ct);

    private async Task<bool> SendGcodeAsync(string baseUrl, IEnumerable<string> gcodes, CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            var url = $"{NormalizeBaseUrl(baseUrl)}/printer/gcode/script";
            var resp = await http.PostAsJsonAsync(url, new { script = string.Join("\n", gcodes) }, cts.Token);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}
