using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Net;

namespace Farm.Web.Server.Services;

public class PrusaLinkClient(HttpClient http) : PrinterClientBase
{
    private static string NormalizeBaseUrl(string url) => NormalizeBaseUrl(url, 80);

    private static void AddApiKey(HttpRequestMessage req, string? apiKey)
    {
        if (!string.IsNullOrWhiteSpace(apiKey))
            req.Headers.Add("X-Api-Key", apiKey);
    }

    public async Task<PrusaCompositeStatus> GetCompositeStatusAsync(string baseUrl, string? apiKey, CancellationToken ct = default)
    {
        var status = await GetStatusAsync(baseUrl, apiKey, ct);
        var job = await GetJobAsync(baseUrl, apiKey, ct);
        // PrusaLink does not expose position/temps in the same way; stub for now
        return new PrusaCompositeStatus(
            status.IsOnline,
            status.State,
            job?.Progress,
            job?.JobName,
            job?.ThumbnailUrl,
            job?.CameraStreamUrl,
            job?.CameraSnapshotUrl
        );
    }

    public async Task<PrusaStatus> GetStatusAsync(string baseUrl, string? apiKey, CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            var url = $"{NormalizeBaseUrl(baseUrl)}/api/v1/status";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            AddApiKey(req, apiKey);
            using var resp = await http.SendAsync(req, cts.Token);
            if (!resp.IsSuccessStatusCode) return new PrusaStatus(false, null);
            var doc = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cts.Token);
            string? state = null;
            if (doc.TryGetProperty("printer", out var printer) && printer.TryGetProperty("state", out var st) && st.ValueKind == JsonValueKind.String)
                state = st.GetString();
            return new PrusaStatus(true, state);
        }
        catch { return new PrusaStatus(false, null); }
    }

    public async Task<PrusaJob?> GetJobAsync(string baseUrl, string? apiKey, CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            var url = $"{NormalizeBaseUrl(baseUrl)}/api/v1/job";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            AddApiKey(req, apiKey);
            using var resp = await http.SendAsync(req, cts.Token);
            if (!resp.IsSuccessStatusCode) return null;
            var doc = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cts.Token);
            string? jobName = null;
            double? progress = null;
            string? thumb = null;
            string? stream = null;
            string? snap = null;
            if (doc.TryGetProperty("job", out var job))
            {
                if (job.TryGetProperty("filename", out var fn) && fn.ValueKind == JsonValueKind.String)
                    jobName = fn.GetString();
                if (job.TryGetProperty("progress", out var prog) && prog.ValueKind == JsonValueKind.Number)
                    progress = prog.GetDouble();
                if (job.TryGetProperty("thumbnail", out var th) && th.ValueKind == JsonValueKind.String)
                    thumb = NormalizeCameraUrl(th.GetString(), NormalizeBaseUrl(baseUrl));
            }
            // Camera URLs
            if (doc.TryGetProperty("webcam", out var cam))
            {
                if (cam.TryGetProperty("stream", out var s) && s.ValueKind == JsonValueKind.String)
                    stream = NormalizeCameraUrl(s.GetString(), NormalizeBaseUrl(baseUrl));
                if (cam.TryGetProperty("snapshot", out var sn) && sn.ValueKind == JsonValueKind.String)
                    snap = NormalizeCameraUrl(sn.GetString(), NormalizeBaseUrl(baseUrl));
            }
            return new PrusaJob(null, progress, jobName, thumb, stream, snap);
        }
        catch { return null; }
    }

    // Add more methods for movement, temps, etc. as needed
}

public record PrusaStatus(bool IsOnline, string? State);
public record PrusaJob(string? PrintState, double? Progress, string? JobName, string? ThumbnailUrl, string? CameraStreamUrl, string? CameraSnapshotUrl);
public record PrusaCompositeStatus(
    bool IsOnline,
    string? State,
    double? Progress,
    string? JobName,
    string? ThumbnailUrl,
    string? CameraStreamUrl,
    string? CameraSnapshotUrl
);
