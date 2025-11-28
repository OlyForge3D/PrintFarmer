using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;

namespace Farm.Infrastructure.Discovery;

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
            using UdpClient client = new UdpClient();
            client.Client.SendTimeout = timeoutMs;
            client.Client.ReceiveTimeout = timeoutMs;

            byte[] discoveryBytes = Encoding.ASCII.GetBytes(SDCP_DISCOVERY_MESSAGE);

            // Send discovery broadcast to the target IP
            _ = await client.SendAsync(discoveryBytes, discoveryBytes.Length, ipAddress, SDCP_DISCOVERY_PORT);

            // Wait for response with timeout
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeoutMs);

            UdpReceiveResult result = await client.ReceiveAsync(cts.Token);
            string responseText = Encoding.UTF8.GetString(result.Buffer);

            // Parse JSON response
            using JsonDocument doc = JsonDocument.Parse(responseText);
            JsonElement root = doc.RootElement;

            // Expected structure: { "Id": "...", "Data": { "Attributes": { "MainboardIP": "...", ... } } }
            if (root.TryGetProperty("Data", out JsonElement dataElement) &&
                dataElement.TryGetProperty("Attributes", out JsonElement attributesElement))
            {
                string? mainboardIp = attributesElement.GetPropertyOrNull("MainboardIP")?.GetString();
                string name = attributesElement.GetPropertyOrNull("Name")?.GetString()
                    ?? attributesElement.GetPropertyOrNull("MachineName")?.GetString()
                    ?? "SDCP Printer";
                string? protocolVersion = attributesElement.GetPropertyOrNull("ProtocolVersion")?.GetString();
                string? firmwareVersion = attributesElement.GetPropertyOrNull("FirmwareVersion")?.GetString();
                string? brandName = attributesElement.GetPropertyOrNull("BrandName")?.GetString();

                DiscoveredPrinterDto dto = new DiscoveredPrinterDto
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
        if (element.TryGetProperty(propertyName, out JsonElement property))
        {
            return property;
        }
        return null;
    }
}
