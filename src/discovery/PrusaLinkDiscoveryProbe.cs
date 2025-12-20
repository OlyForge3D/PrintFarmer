using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Printers;

namespace Farm.Infrastructure.Discovery;

/// <summary>
/// Discovery probe for PrusaLink-based 3D printers (Prusa MK series).
/// Probes ports 80 and 8080, validates response contains PrusaLink-specific fields.
/// Uses IPrusaLinkClient for all API interactions to ensure consistency with backend client implementation.
/// </summary>
public class PrusaLinkDiscoveryProbe : BaseDiscoveryProbe
{
    private readonly IPrusaLinkClient _prusaLinkClient;

    public PrusaLinkDiscoveryProbe(IPrusaLinkClient prusaLinkClient)
    {
        _prusaLinkClient = prusaLinkClient;
    }
    public override string DisplayName => "PrusaLink";
    protected override int[] Ports => new[] { 80, 8080 };
    protected override string EndpointPath => "/api/v1/info";
    protected override PrinterBackend Backend => PrinterBackend.PrusaLink;
    protected override string PrinterName => "PrusaLink Printer";

    /// <summary>
    /// Validates PrusaLink response with confidence scoring.
    /// Score 100: Has multiple Prusa-specific fields (2-3 fields)
    /// Score 85: Has some Prusa-specific fields (1 field)
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
}
