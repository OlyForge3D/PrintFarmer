using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Discovery;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;

namespace Farm.Backend.Plugin.Moonraker;

/// <summary>
/// Advanced discovery probe for Moonraker-based 3D printers (Klipper firmware).
/// Probes backend port 7125 plus Snapmaker U1-style port 80, discovers frontend ports (80, 8080, 8808), and extracts actual camera URLs.
/// Features:
/// - Validates Klipper-specific response fields with confidence scoring
/// - Discovers frontend port for web interface
/// - Extracts hostname from response or via reverse DNS
/// </summary>
public class MoonrakerDiscoveryProbe : INetworkDiscoveryProbe
{
    // Stock Moonraker commonly uses backend 7125; Snapmaker U1 exposes Moonraker-compatible endpoints on port 80.
    private static readonly int[] FrontendPorts = new[] { 80, 8080, 8808 };

    public string DisplayName => "Moonraker";

    public PrinterBackend Backend => PrinterBackend.Moonraker;

    /// <summary>
    /// Validates Moonraker response with confidence scoring.
    /// Note: Moonraker /printer/info response has fields nested under "result" key.
    /// Score 100: All Klipper fields present
    /// Score 90: Most fields present (2 out of 3)
    /// Score 75: Some Klipper fields present (1 out of 3)
    /// </summary>
    /// <param name="response">The HTTP response message to validate.</param>
    /// <param name="content">The response content as a string.</param>
    protected static Task<(bool IsValid, int ConfidenceScore, string Reason)> ValidateResponseAsync(
        HttpResponseMessage response, string content)
    {
        return MoonrakerOnboardingResolver.ValidatePrinterInfoResponseAsync(response, content);
    }

    protected static Task<(bool IsValid, int ConfidenceScore, string Reason)> ValidateMachineSystemInfoResponseAsync(
        HttpResponseMessage response, string content) =>
        MoonrakerOnboardingResolver.ValidateMachineSystemInfoResponseAsync(response, content);

    protected virtual HttpClient CreateHttpClient(int timeoutMs) => new()
    { Timeout = TimeSpan.FromMilliseconds(timeoutMs) };

    /// <summary>
    /// Advanced probe that:
    /// 1. Confirms Moonraker backend is running on port 7125
    /// 2. Discovers frontend port (80, 8080, or 8808)
    /// 3. Attempts to extract actual camera URLs via API
    /// 4. Extracts hostname from response or via reverse DNS
    /// </summary>
    /// <param name="ipAddress">The IP address to probe.</param>
    /// <param name="timeoutMs">Timeout in milliseconds for probe operations.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    public async Task<ProbeResult?> ProbeAsync(string ipAddress, int timeoutMs, CancellationToken cancellationToken)
    {
        using HttpClient client = CreateHttpClient(timeoutMs);

        foreach (MoonrakerEndpointCandidate endpoint in MoonrakerOnboardingResolver.GetEndpointCandidates(preferredBackendPort: null))
        {
            string backendUrl = $"http://{ipAddress}:{endpoint.BackendPort}{endpoint.EndpointPath}";
            try
            {
                HttpResponseMessage response = await client.GetAsync(backendUrl, cancellationToken);
                string content = await response.Content.ReadAsStringAsync(cancellationToken);

                (bool isValid, int confidence, string? reason) = endpoint.EndpointPath == MoonrakerOnboardingResolver.MachineSystemInfoPath
                    ? await ValidateMachineSystemInfoResponseAsync(response, content)
                    : await ValidateResponseAsync(response, content);
                if (!isValid)
                {
                    continue;
                }

                int? frontendPort = endpoint.BackendPort == MoonrakerOnboardingResolver.SnapmakerU1MoonrakerPort
                    ? MoonrakerOnboardingResolver.SnapmakerU1MoonrakerPort
                    : await DiscoverFrontendPortAsync(ipAddress, client, timeoutMs, cancellationToken);

                SnapmakerU1Metadata? u1Metadata = MoonrakerOnboardingResolver.ExtractSnapmakerU1Metadata(content);
                string? hostName = u1Metadata?.DeviceName ?? ExtractHostnameFromResponse(content);
                if (string.IsNullOrEmpty(hostName))
                {
                    hostName = await TryResolveHostNameAsync(ipAddress, cancellationToken);
                }

                // Attempt to get camera URLs (basic implementation; can be extended with IMoonrakerClient in API layer)
                string? cameraStreamUrl = null;
                string? cameraSnapshotUrl = null;
                if (frontendPort.HasValue)
                {
                    (cameraStreamUrl, cameraSnapshotUrl) =
                        await DiscoverCameraUrlsAsync(ipAddress, endpoint.BackendPort, frontendPort.Value, client, timeoutMs, cancellationToken);
                }

                DiscoveredPrinterDto dto = new DiscoveredPrinterDto
                {
                    IpAddress = ipAddress,
                    BackendPort = endpoint.BackendPort,
                    FrontendPort = frontendPort,
                    Backend = Backend,
                    ServerUrl = $"http://{ipAddress}",
                    Name = hostName ?? "Moonraker Printer",
                    Manufacturer = u1Metadata?.Manufacturer,
                    Model = u1Metadata?.Model,
                    CameraStreamUrl = cameraStreamUrl,
                    CameraSnapshotUrl = cameraSnapshotUrl
                };

                return new ProbeResult(dto, confidence, u1Metadata is not null ? "Snapmaker U1 Moonraker detected via /machine/system_info" : reason);
            }
            catch
            {
            }
        }

        return null;
    }

    protected virtual async Task<string?> TryResolveHostNameAsync(string ipAddress, CancellationToken cancellationToken)
    {
        try
        {
            IPHostEntry entry = await Dns.GetHostEntryAsync(ipAddress, cancellationToken);
            return entry.HostName;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Attempts to discover which port the frontend is running on by probing common ports.
    /// </summary>
    private static async Task<int?> DiscoverFrontendPortAsync(string ipAddress, HttpClient client, int timeoutMs,
        CancellationToken cancellationToken)
    {
        // Try common frontend ports in order: 80, 8080, 8808
        foreach (int port in FrontendPorts)
        {
            try
            {
                // Simple connectivity check to see if something is listening on this port
                string testUrl = $"http://{ipAddress}:{port}/";
                using CancellationTokenSource portTimeoutCts =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

                // Use shorter timeout for port discovery (don't waste time on unresponsive ports)
                portTimeoutCts.CancelAfter(TimeSpan.FromMilliseconds(Math.Min(timeoutMs / 2, 2000)));

                HttpResponseMessage response = await client.GetAsync(testUrl, portTimeoutCts.Token);
                if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound)
                {
                    // Port is open and responding; likely the frontend
                    return port;
                }
            }
            catch
            {
            }
        }

        // Default to 80 if no port responds
        return 80;
    }

    /// <summary>
    /// Extracts hostname from Moonraker's /printer/info response.
    /// </summary>
    private static string? ExtractHostnameFromResponse(string content)
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
        catch
        {
        }

        return null;
    }

    /// <summary>
    /// Attempts to discover camera URLs from Moonraker's /server/webcams/list API.
    /// Uses basic HTTP calls to query the API endpoint.
    /// </summary>
    /// <param name="ipAddress">The IP address of the Moonraker instance.</param>
    /// <param name="backendPort">The backend API port to use for camera discovery.</param>
    /// <param name="frontendPort">The frontend port to use for camera URLs.</param>
    /// <param name="client">The HTTP client to use for requests.</param>
    /// <param name="timeoutMs">Timeout in milliseconds for the request.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    private static async Task<(string? StreamUrl, string? SnapshotUrl)> DiscoverCameraUrlsAsync(
        string ipAddress, int backendPort, int frontendPort, HttpClient client, int timeoutMs, CancellationToken cancellationToken)
    {
        try
        {
            // Query Moonraker's /server/webcams/list API for configured cameras
            string webcamsApiUrl = $"http://{ipAddress}:{backendPort}/server/webcams/list";
            using CancellationTokenSource cameraTimeoutCts =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cameraTimeoutCts.CancelAfter(TimeSpan.FromMilliseconds(Math.Min(timeoutMs / 3, 3000)));

            HttpResponseMessage response = await client.GetAsync(webcamsApiUrl, cameraTimeoutCts.Token);
            if (response.IsSuccessStatusCode)
            {
                string content = await response.Content.ReadAsStringAsync(cameraTimeoutCts.Token);
                using JsonDocument doc = JsonDocument.Parse(content);

                if (doc.RootElement.TryGetProperty("result", out JsonElement resultElem) &&
                    resultElem.TryGetProperty("webcams", out JsonElement webcamsElem) &&
                    webcamsElem.ValueKind == JsonValueKind.Array && webcamsElem.GetArrayLength() > 0)
                {
                    // Get the first configured webcam
                    JsonElement firstWebcam = webcamsElem[0];

                    // Moonraker webcams typically have stream_url and snapshot_url properties
                    string? streamUrl = null;
                    string? snapshotUrl = null;

                    if (firstWebcam.TryGetProperty("stream_url", out JsonElement streamElem) &&
                        streamElem.ValueKind == JsonValueKind.String)
                    {
                        streamUrl = streamElem.GetString();
                    }

                    if (firstWebcam.TryGetProperty("snapshot_url", out JsonElement snapshotElem) &&
                        snapshotElem.ValueKind == JsonValueKind.String)
                    {
                        snapshotUrl = snapshotElem.GetString();
                    }

                    // Make URLs absolute if they're relative
                    if (!string.IsNullOrEmpty(streamUrl) && !streamUrl.StartsWith("http"))
                    {
                        streamUrl = $"http://{ipAddress}:{frontendPort}{streamUrl}";
                    }

                    if (!string.IsNullOrEmpty(snapshotUrl) && !snapshotUrl.StartsWith("http"))
                    {
                        snapshotUrl = $"http://{ipAddress}:{frontendPort}{snapshotUrl}";
                    }

                    if (!string.IsNullOrEmpty(streamUrl) || !string.IsNullOrEmpty(snapshotUrl))
                    {
                        return (streamUrl, snapshotUrl);
                    }
                }
            }
        }
        catch
        {
        }

        return (null, null);
    }
}
