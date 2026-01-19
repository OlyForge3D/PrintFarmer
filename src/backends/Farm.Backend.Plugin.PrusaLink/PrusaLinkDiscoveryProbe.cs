using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Discovery;

namespace Farm.Backend.Plugin.PrusaLink;

/// <summary>
/// Discovery probe for PrusaLink-based 3D printers (Prusa MK series).
/// Probes ports 80 and 8080, validates response contains PrusaLink-specific fields.
/// </summary>
public class PrusaLinkDiscoveryProbe : INetworkDiscoveryProbe
{
    public string DisplayName => "PrusaLink";

    public PrinterBackend Backend => PrinterBackend.PrusaLink;

    /// <summary>
    /// Validates PrusaLink response with confidence scoring.
    /// Score 100: Has multiple Prusa-specific fields (2-3 fields)
    /// Score 85: Has some Prusa-specific fields (1 field)
    /// </summary>
    /// <param name="response">The HTTP response message to validate.</param>
    /// <param name="content">The response content as a string.</param>
    protected static Task<(bool IsValid, int ConfidenceScore, string Reason)> ValidateResponseAsync(
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

            // Check for Prusa-specific fields
            bool hasPrinterModel = root.TryGetProperty("printer_model", out _);
            bool hasFriendlyName = root.TryGetProperty("friendly_name", out _);
            bool hasPrusaField = root.TryGetProperty("prusa", out _);

            int fieldCount = (hasPrinterModel ? 1 : 0) + (hasFriendlyName ? 1 : 0) + (hasPrusaField ? 1 : 0);

            if (fieldCount == 0)
            {
                return Task.FromResult((false, 0, "No Prusa fields found"));
            }

            // Score based on how many Prusa fields are present
            int confidence = fieldCount >= 2 ? 100 : 85;
            return Task.FromResult((true, confidence, $"PrusaLink detected ({fieldCount}/3 fields)"));
        }
        catch
        {
            // Not valid JSON or parsing error - not PrusaLink
            return Task.FromResult((false, 0, "Invalid JSON"));
        }
    }

    public async Task<ProbeResult?> ProbeAsync(string ipAddress, int timeoutMs, CancellationToken cancellationToken)
    {
        using HttpClient client = new()
        { Timeout = TimeSpan.FromMilliseconds(timeoutMs) };

        // Try both port 80 and 8080
        foreach (int port in new[] { 80, 8080 })
        {
            string url = $"http://{ipAddress}:{port}/api/v1/info";
            try
            {
                HttpResponseMessage response = await client.GetAsync(url, cancellationToken);
                string content = await response.Content.ReadAsStringAsync(cancellationToken);

                (bool isValid, int confidence, string? reason) = await ValidateResponseAsync(response, content);
                if (!isValid)
                {
                    continue;
                }

                DiscoveredPrinterDto dto = new DiscoveredPrinterDto
                {
                    IpAddress = ipAddress,
                    BackendPort = port,
                    Backend = Backend,
                    ServerUrl = $"http://{ipAddress}",
                    Name = "PrusaLink Printer"
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
