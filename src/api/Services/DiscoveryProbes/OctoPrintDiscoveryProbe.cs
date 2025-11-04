using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Farm.Web.Api.Services.DiscoveryProbes;
using Farm.Web.Shared;

namespace Farm.Web.Api.Services.DiscoveryProbes;

[DiscoveryProbe(Name)]
public class OctoPrintDiscoveryProbe : BaseDiscoveryProbe
{
    private const string Name = "OctoPrint";
    public override string DisplayName => Name;
    protected override int[] Ports => new[] { 80, 5000 };
    protected override string EndpointPath => "/api/version";
    protected override PrinterBackend Backend => PrinterBackend.OctoPrint;
    protected override string PrinterName => "OctoPrint Printer";

    // Stronger validation to avoid false-positives from generic HTTP success responses.
    // OctoPrint's /api/version typically returns JSON with keys like "api" and "server",
    // and often contains the string "OctoPrint" in the server value. We validate that here.
    protected override Task<bool> IsValidResponseAsync(HttpResponseMessage response, string content)
    {
        if (!response.IsSuccessStatusCode)
        {
            return Task.FromResult(false);
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            return Task.FromResult(false);
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(content);
            JsonElement root = doc.RootElement;

            // If there is an "api" property that's a string/number, it's a good indicator
            if (root.TryGetProperty("api", out _))
            {
                return Task.FromResult(true);
            }

            // If there's a "server" property containing "OctoPrint", accept it
            if (root.TryGetProperty("server", out JsonElement serverElem))
            {
                if (serverElem.ValueKind == JsonValueKind.String &&
                    serverElem.GetString()?.Contains("OctoPrint", StringComparison.OrdinalIgnoreCase) == true)
                {
                    return Task.FromResult(true);
                }
            }
        }
        catch
        {
            // Not valid JSON - fall back to simple substring check below
        }

        // Last resort: check for the literal string in the response body
        if (content.Contains("OctoPrint", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(true);
        }

        return Task.FromResult(false);
    }
}
