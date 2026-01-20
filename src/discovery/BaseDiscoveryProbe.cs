using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Discovery;

/// <summary>
/// Base class for HTTP-based discovery probes.
/// Handles common probe logic: HTTP requests, DNS resolution, response validation with scoring.
/// </summary>
public abstract class BaseDiscoveryProbe : INetworkDiscoveryProbe
{
    public abstract string DisplayName { get; }

    protected abstract int[] Ports { get; }

    protected abstract string EndpointPath { get; }

    protected abstract PrinterBackend Backend { get; }

    protected abstract string PrinterName { get; }

    // Expose backend via interface
    PrinterBackend INetworkDiscoveryProbe.Backend => Backend;

    /// <summary>
    /// Override to provide backend-specific validation with confidence scoring.
    /// Returns (isValid, confidenceScore, reason).
    /// </summary>
    /// <param name="response">The HTTP response message to validate.</param>
    /// <param name="content">The response content as a string.</param>
    protected virtual Task<(bool IsValid, int ConfidenceScore, string Reason)> ValidateResponseAsync(
        HttpResponseMessage response, string content)
    {
        // Default: check success status
        return Task.FromResult(response.IsSuccessStatusCode
            ? (true, 100, "Response valid")
            : (false, 0, "Response invalid"));
    }

    public virtual async Task<ProbeResult?> ProbeAsync(string ipAddress, int timeoutMs, CancellationToken cancellationToken)
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

                (bool isValid, int confidence, string? reason) = await ValidateResponseAsync(response, content);
                if (!isValid)
                {
                    continue;
                }

                // Attempt reverse DNS lookup for hostname
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

                DiscoveredPrinterDto dto = new DiscoveredPrinterDto
                {
                    IpAddress = ipAddress,
                    BackendPort = port,
                    Backend = Backend,
                    ServerUrl = $"http://{ipAddress}",
                    Name = hostName ?? PrinterName
                };

                return new ProbeResult(dto, confidence, reason);
            }
            catch
            {
            }
        }

        return null;
    }
}
