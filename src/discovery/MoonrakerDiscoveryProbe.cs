using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Farm.Web.Shared;

namespace Farm.Shared.Discovery;

/// <summary>
/// Advanced discovery probe for Moonraker-based 3D printers (Klipper firmware).
/// Probes backend port 7125, discovers frontend ports (80, 8080, 8808), and extracts actual camera URLs.
/// Features:
/// - Validates Klipper-specific response fields with confidence scoring
/// - Discovers frontend port for web interface
/// - Attempts to extract actual camera URLs from Moonraker API
/// - Extracts hostname from response or via reverse DNS
/// </summary>
public class MoonrakerDiscoveryProbe : BaseDiscoveryProbe
{
    // Moonraker backend always on 7125; frontend ports to probe: 80, 8080, 8808
    private static readonly int[] FrontendPorts = new[] { 80, 8080, 8808 };

    public override string DisplayName => "Moonraker";
    protected override int[] Ports => new[] { 7125 };
    protected override string EndpointPath => "/printer/info";
    protected override PrinterBackend Backend => PrinterBackend.Moonraker;
    protected override string PrinterName => "Moonraker Printer";

    /// <summary>
    /// Validates Moonraker response with confidence scoring.
    /// Note: Moonraker /printer/info response has fields nested under "result" key.
    /// Score 100: All Klipper fields present
    /// Score 90: Most fields present (2 out of 3)
    /// Score 75: Some Klipper fields present (1 out of 3)
    /// </summary>
    protected override Task<(bool IsValid, int ConfidenceScore, string Reason)> ValidateResponseAsync(
        HttpResponseMessage response, string content)
    {
        if (!response.IsSuccessStatusCode)
        {
            return Task.FromResult((false, 0, "HTTP error"));
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            return Task.FromResult((false, 0, "Empty response"));
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(content);
            JsonElement root = doc.RootElement;

            // Moonraker wraps response in a "result" property
            if (!root.TryGetProperty("result", out JsonElement resultElem))
            {
                return Task.FromResult((false, 0, "Missing 'result' wrapper"));
            }

            // Check for Klipper-specific fields in the result
            bool hasStateMessage = resultElem.TryGetProperty("state_message", out _);
            bool hasKlipperPath = resultElem.TryGetProperty("klipper_path", out _);
            bool hasHostname = resultElem.TryGetProperty("hostname", out _);

            int fieldCount = (hasStateMessage ? 1 : 0) + (hasKlipperPath ? 1 : 0) + (hasHostname ? 1 : 0);

            if (fieldCount == 0)
            {
                return Task.FromResult((false, 0, "No Klipper fields found"));
            }

            // Score based on how many Klipper fields are present
            int confidence = fieldCount == 3 ? 100 : fieldCount == 2 ? 90 : 75;
            return Task.FromResult((true, confidence, $"Moonraker detected ({fieldCount}/3 fields)"));
        }
        catch
        {
            // Not valid JSON or parsing error - not Moonraker
            return Task.FromResult((false, 0, "Invalid JSON"));
        }
    }

    /// <summary>
    /// Advanced probe that:
    /// 1. Confirms Moonraker backend is running on port 7125
    /// 2. Discovers frontend port (80, 8080, or 8808)
    /// 3. Attempts to extract actual camera URLs via API
    /// 4. Extracts hostname from response or via reverse DNS
    /// </summary>
    public override async Task<ProbeResult?> ProbeAsync(string ipAddress, int timeoutMs, CancellationToken cancellationToken)
    {
        using HttpClient client = new()
        { Timeout = TimeSpan.FromMilliseconds(timeoutMs) };

        // First, confirm Moonraker backend is running on port 7125
        string backendUrl = $"http://{ipAddress}:7125{EndpointPath}";
        try
        {
            HttpResponseMessage response = await client.GetAsync(backendUrl, cancellationToken);
            string content = await response.Content.ReadAsStringAsync(cancellationToken);

            var (isValid, confidence, reason) = await ValidateResponseAsync(response, content);
            if (!isValid)
            {
                return null;
            }

            // Backend found on 7125; now discover frontend port
            int? frontendPort = await DiscoverFrontendPortAsync(ipAddress, client, timeoutMs, cancellationToken);

            // Extract hostname from response or via reverse DNS
            string? hostName = ExtractHostnameFromResponse(content);
            if (string.IsNullOrEmpty(hostName))
            {
                try
                {
                    IPHostEntry entry = await Dns.GetHostEntryAsync(ipAddress, cancellationToken);
                    hostName = entry.HostName;
                }
                catch { }
            }

            // Attempt to get camera URLs (basic implementation; can be extended with IMoonrakerClient in API layer)
            string? cameraStreamUrl = null;
            string? cameraSnapshotUrl = null;
            if (frontendPort.HasValue)
            {
                // Try common camera endpoint paths
                (cameraStreamUrl, cameraSnapshotUrl) = await DiscoverCameraUrlsAsync(ipAddress, frontendPort.Value, client, timeoutMs, cancellationToken);
            }

            var dto = new DiscoveredPrinterDto
            {
                IpAddress = ipAddress,
                BackendPort = 7125,
                FrontendPort = frontendPort,
                Backend = Backend,
                ServerUrl = $"http://{ipAddress}",
                Name = hostName ?? PrinterName,
                CameraStreamUrl = cameraStreamUrl,
                CameraSnapshotUrl = cameraSnapshotUrl
            };

            return new ProbeResult(dto, confidence, reason);
        }
        catch { }

        return null;
    }

    /// <summary>
    /// Attempts to discover which port the frontend is running on by probing common ports.
    /// </summary>
    private async Task<int?> DiscoverFrontendPortAsync(string ipAddress, HttpClient client, int timeoutMs, CancellationToken cancellationToken)
    {
        // Try common frontend ports in order: 80, 8080, 8808
        foreach (int port in FrontendPorts)
        {
            try
            {
                // Simple connectivity check to see if something is listening on this port
                string testUrl = $"http://{ipAddress}:{port}/";
                using CancellationTokenSource portTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                // Use shorter timeout for port discovery (don't waste time on unresponsive ports)
                portTimeoutCts.CancelAfter(TimeSpan.FromMilliseconds(Math.Min(timeoutMs / 2, 2000)));

                HttpResponseMessage response = await client.GetAsync(testUrl, portTimeoutCts.Token);
                if (response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    // Port is open and responding; likely the frontend
                    return port;
                }
            }
            catch { }
        }

        // Default to 80 if no port responds
        return 80;
    }

    /// <summary>
    /// Extracts hostname from Moonraker's /printer/info response.
    /// </summary>
    private string? ExtractHostnameFromResponse(string content)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(content);
            if (doc.RootElement.TryGetProperty("result", out JsonElement resultElem) &&
                resultElem.TryGetProperty("hostname", out JsonElement hostnameElem) &&
                hostnameElem.ValueKind == JsonValueKind.String)
            {
                return hostnameElem.GetString();
            }
        }
        catch { }

        return null;
    }

    /// <summary>
    /// Attempts to discover camera URLs from common endpoints.
    /// This is a basic implementation; a more advanced version with IMoonrakerClient would be more reliable.
    /// </summary>
    private async Task<(string? StreamUrl, string? SnapshotUrl)> DiscoverCameraUrlsAsync(
        string ipAddress, int frontendPort, HttpClient client, int timeoutMs, CancellationToken cancellationToken)
    {
        try
        {
            // Try common camera endpoint paths
            string[] cameraPaths = new[]
            {
                "/webcam/?action=stream",  // Common MJPEG stream
                "/api/webcams",            // Moonraker webcams API endpoint
                "/webcam/stream"           // Alternative stream endpoint
            };

            foreach (string path in cameraPaths)
            {
                try
                {
                    string cameraUrl = $"http://{ipAddress}:{frontendPort}{path}";
                    using CancellationTokenSource cameraTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    cameraTimeoutCts.CancelAfter(TimeSpan.FromMilliseconds(Math.Min(timeoutMs / 3, 1000)));

                    HttpResponseMessage response = await client.GetAsync(cameraUrl, cameraTimeoutCts.Token);
                    if (response.IsSuccessStatusCode)
                    {
                        // Found a responsive camera endpoint
                        return (cameraUrl, $"http://{ipAddress}:{frontendPort}/webcam/?action=snapshot");
                    }
                }
                catch { }
            }
        }
        catch { }

        return (null, null);
    }
}
