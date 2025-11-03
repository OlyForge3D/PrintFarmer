using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Farm.Web.Shared;

namespace Farm.Web.Api.Services.DiscoveryProbes;

public abstract class BaseDiscoveryProbe : INetworkDiscoveryProbe
{
    public abstract string DisplayName { get; }
    protected abstract int[] Ports { get; }
    protected abstract string EndpointPath { get; }
    protected abstract PrinterBackend Backend { get; }
    protected abstract string PrinterName { get; }

    // Optionally override for custom validation/parse
    protected virtual Task<bool> IsValidResponseAsync(HttpResponseMessage response, string content)
        => Task.FromResult(response.IsSuccessStatusCode);

    public virtual async Task<DiscoveredPrinterDto?> ProbeAsync(string ipAddress, int timeoutMs, CancellationToken cancellationToken)
    {
        using HttpClient client = new()
        { Timeout = TimeSpan.FromMilliseconds(timeoutMs) };
        foreach (int port in Ports)
        {
            string url = $"http://{ipAddress}:{port}{EndpointPath}";
            try
            {
                HttpResponseMessage response = await client.GetAsync(url, cancellationToken);
                string content = await response.Content.ReadAsStringAsync(cancellationToken);
                if (!await IsValidResponseAsync(response, content))
                {
                    continue;
                }

                // after you detect the IP:
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
                    Port = port,
                    Backend = Backend,
                    ServerUrl = $"http://{ipAddress}",
                    Name = hostName ?? PrinterName
                };
            }
            catch { }
        }
        return null;
    }
}
