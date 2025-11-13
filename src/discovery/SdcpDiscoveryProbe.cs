using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Farm.Web.Shared;

namespace Farm.Shared.Discovery;

/// <summary>
/// Discovery probe for SDCP (Snapmaker Discovery Protocol) printers using UDP broadcast.
/// Sends 'M99999' discovery message to port 3000, receives JSON response with printer details.
/// </summary>
public class SdcpDiscoveryProbe : INetworkDiscoveryProbe
{
    private const string SDCP_DISCOVERY_MESSAGE = "M99999";
    private const int SDCP_DISCOVERY_PORT = 3000;

    public string DisplayName => "SDCP";
    public PrinterBackend Backend => PrinterBackend.SDCP;

    public virtual async Task<ProbeResult?> ProbeAsync(string ipAddress, int timeoutMs, CancellationToken cancellationToken)
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

                var dto = new DiscoveredPrinterDto
                {
                    IpAddress = mainboardIp ?? ipAddress,
                    Name = name,
                    BackendPort = 80, // SDCP typically uses HTTP for subsequent communication
                    Backend = Backend,
                    Manufacturer = brandName,
                    ServerUrl = $"http://{mainboardIp ?? ipAddress}:80",
                    IsReachable = true,
                    DiscoveredAt = System.DateTime.UtcNow
                };

                // Score based on what fields we have
                int fieldCount = (mainboardIp != null ? 1 : 0) + (brandName != null ? 1 : 0);
                int confidence = fieldCount >= 2 ? 100 : 85;

                return new ProbeResult(dto, confidence, $"SDCP detected ({fieldCount}/3 fields)");
            }

            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (SocketException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch
        {
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
