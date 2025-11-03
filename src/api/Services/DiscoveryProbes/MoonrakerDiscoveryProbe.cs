using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Farm.Web.Api.Services.DiscoveryProbes;
using Farm.Web.Api.Services.Interfaces;
using Farm.Web.Shared;

namespace Farm.Web.Api.Services.DiscoveryProbes;

[DiscoveryProbe(Name)]
public class MoonrakerDiscoveryProbe : BaseDiscoveryProbe
{
    private const string Name = "Moonraker";
    private readonly IMoonrakerClient _moonrakerClient;

    public override string DisplayName => Name;
    // Moonraker backend always on 7125; frontend ports to probe: 80, 8080, 8808
    protected override int[] Ports => new[] { 7125 };
    protected override string EndpointPath => "/printer/info";
    protected override PrinterBackend Backend => PrinterBackend.Moonraker;
    protected override string PrinterName => "Moonraker Printer";

    public MoonrakerDiscoveryProbe(IMoonrakerClient moonrakerClient)
    {
        _moonrakerClient = moonrakerClient ?? throw new ArgumentNullException(nameof(moonrakerClient));
    }

    // Override to discover both backend (7125) and frontend ports (80, 8080, 8808), and actual camera URLs
    public override async Task<DiscoveredPrinterDto?> ProbeAsync(string ipAddress, int timeoutMs, CancellationToken cancellationToken)
    {
        using HttpClient client = new()
        { Timeout = TimeSpan.FromMilliseconds(timeoutMs) };

        // First, confirm Moonraker backend is running on port 7125
        string backendUrl = $"http://{ipAddress}:7125{EndpointPath}";
        try
        {
            HttpResponseMessage response = await client.GetAsync(backendUrl, cancellationToken);
            string content = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!await IsValidResponseAsync(response, content))
            {
                return null;
            }

            // Backend found on 7125; now discover frontend port
            int? frontendPort = await DiscoverFrontendPortAsync(ipAddress, client, timeoutMs, cancellationToken);

            // Query for actual camera URLs via Moonraker API
            (string? cameraStreamUrl, string? cameraSnapshotUrl) = await _moonrakerClient.GetConfiguredCameraUrlsAsync($"http://{ipAddress}", frontendPort, cancellationToken);

            // Get hostname
            IPHostEntry entry;
            string? hostName = null;
            try
            {
                entry = await Dns.GetHostEntryAsync(ipAddress, cancellationToken);
                hostName = entry.HostName;
            }
            catch (SocketException)
            {
                // no PTR record or lookup failed
            }

            return new DiscoveredPrinterDto
            {
                IpAddress = ipAddress,
                Port = 7125,
                FrontendPort = frontendPort,
                Backend = Backend,
                ServerUrl = $"http://{ipAddress}",
                Name = hostName ?? PrinterName,
                CameraStreamUrl = cameraStreamUrl,
                CameraSnapshotUrl = cameraSnapshotUrl
            };
        }
        catch { }

        return null;
    }

    // Attempt to discover which port the frontend is running on
    private async Task<int?> DiscoverFrontendPortAsync(string ipAddress, HttpClient client, int timeoutMs, CancellationToken cancellationToken)
    {
        // Try common frontend ports in order: 80, 8080, 8808
        int[] frontendPorts = new[] { 80, 8080, 8808 };

        foreach (int port in frontendPorts)
        {
            try
            {
                // Simple connectivity check to see if something is listening on this port
                string testUrl = $"http://{ipAddress}:{port}/";
                using CancellationTokenSource portTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                portTimeoutCts.CancelAfter(TimeSpan.FromMilliseconds(Math.Min(timeoutMs / 2, 2000)));
                HttpResponseMessage response = await client.GetAsync(testUrl, portTimeoutCts.Token);
                if (response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    // Port is open and responding; likely the frontend
                    return port;
                }
            }
            catch
            {
                // Port not responding, try next one
            }
        }

        // Default to 80 if no port responds
        return 80;
    }
}
