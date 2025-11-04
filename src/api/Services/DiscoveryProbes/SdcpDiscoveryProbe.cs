using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Farm.Web.Api.Services.DiscoveryProbes;
using Farm.Web.Shared;
using Microsoft.Extensions.Logging;

namespace Farm.Web.Api.Services.DiscoveryProbes;

/// <summary>
/// SDCP (Snapmaker Discovery Protocol) discovery probe using UDP broadcast.
/// Sends 'M99999' discovery message to port 3000, receives JSON response with printer details.
/// </summary>
[DiscoveryProbe(Name)]
public class SdcpDiscoveryProbe : INetworkDiscoveryProbe
{
    private const string Name = "SDCP";
    private const string SDCP_DISCOVERY_MESSAGE = "M99999";
    private const int SDCP_DISCOVERY_PORT = 3000;

    private readonly ILogger<SdcpDiscoveryProbe> _logger;

    public SdcpDiscoveryProbe(ILogger<SdcpDiscoveryProbe> logger)
    {
        _logger = logger;
    }

    public string DisplayName => Name;
    public PrinterBackend Backend => PrinterBackend.SDCP;

    public async Task<DiscoveredPrinterDto?> ProbeAsync(string ipAddress, int timeoutMs, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new UdpClient();
            client.Client.SendTimeout = timeoutMs;
            client.Client.ReceiveTimeout = timeoutMs;

            byte[] discoveryBytes = Encoding.ASCII.GetBytes(SDCP_DISCOVERY_MESSAGE);

            // Send discovery broadcast to the target IP
            await client.SendAsync(discoveryBytes, discoveryBytes.Length, ipAddress, SDCP_DISCOVERY_PORT);

            // Wait for response with timeout
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeoutMs);

            var result = await client.ReceiveAsync(cts.Token);
            string responseText = Encoding.UTF8.GetString(result.Buffer);

            _logger.LogDebug("SDCP response from {IpAddress}: {Response}", ipAddress, responseText);

            // Parse JSON response
            using var doc = JsonDocument.Parse(responseText);
            var root = doc.RootElement;

            // Expected structure: { "Id": "...", "Data": { "Attributes": { "MainboardIP": "...", ... } } }
            if (root.TryGetProperty("Data", out var dataElement) &&
                dataElement.TryGetProperty("Attributes", out var attributesElement))
            {
                var mainboardIp = attributesElement.GetPropertyOrNull("MainboardIP")?.GetString();
                var name = attributesElement.GetPropertyOrNull("Name")?.GetString()
                    ?? attributesElement.GetPropertyOrNull("MachineName")?.GetString()
                    ?? "SDCP Printer";
                var protocolVersion = attributesElement.GetPropertyOrNull("ProtocolVersion")?.GetString();
                var firmwareVersion = attributesElement.GetPropertyOrNull("FirmwareVersion")?.GetString();
                var brandName = attributesElement.GetPropertyOrNull("BrandName")?.GetString();

                _logger.LogInformation(
                    "SDCP printer discovered at {IpAddress}: {Name} (Brand: {Brand}, Protocol: {Protocol}, Firmware: {Firmware})",
                    mainboardIp ?? ipAddress, name, brandName, protocolVersion, firmwareVersion);

                return new DiscoveredPrinterDto
                {
                    IpAddress = mainboardIp ?? ipAddress,
                    Name = name,
                    Port = 80, // SDCP typically uses HTTP for subsequent communication
                    Backend = Backend,
                    Manufacturer = brandName,
                    Version = protocolVersion,
                    Firmware = firmwareVersion,
                    ServerUrl = $"http://{mainboardIp ?? ipAddress}:80",
                    IsReachable = true,
                    DiscoveredAt = System.DateTime.UtcNow
                };
            }

            _logger.LogDebug("SDCP response from {IpAddress} missing expected Data.Attributes structure", ipAddress);
            return null;
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("SDCP probe for {IpAddress} timed out after {TimeoutMs}ms", ipAddress, timeoutMs);
            return null;
        }
        catch (SocketException ex)
        {
            _logger.LogDebug("SDCP probe for {IpAddress} socket error: {Error}", ipAddress, ex.Message);
            return null;
        }
        catch (JsonException ex)
        {
            _logger.LogDebug("SDCP probe for {IpAddress} received invalid JSON: {Error}", ipAddress, ex.Message);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug("SDCP probe for {IpAddress} unexpected error: {Error}", ipAddress, ex.Message);
            return null;
        }
    }
}

/// <summary>
/// Extension method for safer JSON property access.
/// </summary>
internal static class JsonElementExtensions
{
    public static JsonElement? GetPropertyOrNull(this JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var property))
        {
            return property;
        }
        return null;
    }
}
