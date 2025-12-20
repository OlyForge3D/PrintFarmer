using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Printers;

namespace Farm.Infrastructure.Discovery;

/// <summary>
/// Discovery probe for OctoPrint-based 3D printers.
/// Uses IOctoPrintClient for all API interactions to ensure consistency with backend client implementation.
/// Note: When OctoPrint runs with Moonraker compatibility mode, the server element may contain
/// both OctoPrint and Moonraker. In this case, confidence is 0 to allow the Moonraker probe
/// to take precedence (more accurate for Klipper-based systems).
/// </summary>
public class OctoPrintDiscoveryProbe : BaseDiscoveryProbe
{
    private readonly IOctoPrintClient _octoPrintClient;

    public OctoPrintDiscoveryProbe(IOctoPrintClient octoPrintClient)
    {
        _octoPrintClient = octoPrintClient;
    }
    public override string DisplayName => "OctoPrint";
    protected override int[] Ports => new[] { 80, 5000 }; // Probe both, but prefer 80 as default
    protected override string EndpointPath => "/api/version";
    protected override PrinterBackend Backend => PrinterBackend.OctoPrint;
    protected override string PrinterName => "OctoPrint Printer";

    /// <summary>
    /// Validates OctoPrint response with confidence scoring.
    /// Score 100: Has "api" AND "server" with "OctoPrint" in it (and NO Moonraker)
    /// Score 75: Has "api" field (specific to OctoPrint endpoint, and NO Moonraker)
    /// Score 0: Moonraker detected in server element (prefer Moonraker probe)
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

            // MUST have an "api" property - this is specific to OctoPrint /api/version
            // Moonraker and other endpoints won't have this
            if (!root.TryGetProperty("api", out _))
            {
                return Task.FromResult((false, 0, "Missing 'api' field"));
            }

            // Check for Moonraker in text field (compatibility mode)
            // If Moonraker is detected, return confidence 0 to let Moonraker probe take precedence
            if (root.TryGetProperty("text", out JsonElement textElem))
            {
                if (textElem.ValueKind == JsonValueKind.String)
                {
                    string? textStr = textElem.GetString();
                    if (textStr?.Contains("Moonraker", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        return Task.FromResult((false, 0, "Moonraker detected in text field - let Moonraker probe handle"));
                    }

                    // Check for explicit OctoPrint string for higher confidence
                    if (textStr?.Contains("OctoPrint", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        return Task.FromResult((true, 100, "OctoPrint detected (text field confirms)"));
                    }
                }
            }

            // If we have "api" but no explicit "OctoPrint" string, still accept with lower confidence
            // since "api" field is specific to OctoPrint
            return Task.FromResult((true, 75, "OctoPrint detected (api field present)"));
        }
        catch
        {
            // Not valid JSON - reject
            return Task.FromResult((false, 0, "Invalid JSON"));
        }
    }
}
