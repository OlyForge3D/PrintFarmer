using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Farm.Web.Api.Services.SmartPlug;

/// <summary>
/// Smart plug provider for Shelly Gen 1 and Gen 2 devices.
/// Gen 1: GET /meter/0  (Shelly Plug S, Shelly EM, etc.)
/// Gen 2: GET /rpc/Switch.GetStatus?id=0  (Shelly Plus Plug S, Shelly Pro 4PM, etc.)
/// Auto-detects generation based on which endpoint responds.
/// </summary>
public sealed class ShellySmartPlugProvider(
    IHttpClientFactory httpClientFactory,
    ILogger<ShellySmartPlugProvider> logger) : ISmartPlugProvider
{
    public string ProviderType => "Shelly";

    /// <inheritdoc/>
    public async Task<PowerReading?> GetCurrentReadingAsync(string deviceAddress, CancellationToken ct)
    {
        // Try Gen 2 first, fall back to Gen 1.
        PowerReading? reading = await TryGen2ReadingAsync(deviceAddress, ct);
        return reading ?? await TryGen1ReadingAsync(deviceAddress, ct);
    }

    /// <inheritdoc/>
    public async Task<bool> TestConnectionAsync(string deviceAddress, CancellationToken ct)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("SmartPlug");

            // Shelly info endpoint exists on both Gen 1 (/shelly) and Gen 2 (/shelly)
            string url = $"http://{deviceAddress}/shelly";
            using HttpResponseMessage response = await client.GetAsync(url, ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Shelly TestConnection failed for {DeviceAddress}", deviceAddress);
            return false;
        }
    }

    private async Task<PowerReading?> TryGen2ReadingAsync(string deviceAddress, CancellationToken ct)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("SmartPlug");
            string url = $"http://{deviceAddress}/rpc/Switch.GetStatus?id=0";
            using HttpResponseMessage response = await client.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using System.IO.Stream stream = await response.Content.ReadAsStreamAsync(ct);
            return ParseGen2Response(stream);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Shelly Gen2 endpoint unavailable for {DeviceAddress}", deviceAddress);
            return null;
        }
    }

    private async Task<PowerReading?> TryGen1ReadingAsync(string deviceAddress, CancellationToken ct)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("SmartPlug");
            string url = $"http://{deviceAddress}/meter/0";
            using HttpResponseMessage response = await client.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using System.IO.Stream stream = await response.Content.ReadAsStreamAsync(ct);
            return ParseGen1Response(stream);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Shelly GetCurrentReading failed for {DeviceAddress}", deviceAddress);
            return null;
        }
    }

    private PowerReading? ParseGen2Response(System.IO.Stream stream)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(stream);
            JsonElement root = doc.RootElement;

            // {"apower":23.5,"voltage":230.1,"current":0.103,"aenergy":{"total":12.345}}
            double watts = root.TryGetProperty("apower", out JsonElement ap) ? ap.GetDouble() : 0;
            double? volts = root.TryGetProperty("voltage", out JsonElement v) ? v.GetDouble() : null;
            double? amps = root.TryGetProperty("current", out JsonElement c) ? c.GetDouble() : null;
            double? kwh = null;
            if (root.TryGetProperty("aenergy", out JsonElement ae) &&
                ae.TryGetProperty("total", out JsonElement tot))
            {
                kwh = tot.GetDouble() / 1000.0;
            }

            return new PowerReading(watts, kwh, volts, amps);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to parse Shelly Gen 2 response");
            return null;
        }
    }

    private PowerReading? ParseGen1Response(System.IO.Stream stream)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(stream);
            JsonElement root = doc.RootElement;

            // {"power":23.5,"overpower":0.0,"is_valid":true,"timestamp":0,"counters":[...],"total":12345}
            double watts = root.TryGetProperty("power", out JsonElement pw) ? pw.GetDouble() : 0;

            // Gen 1 /meter/0 does not expose voltage or current; total is in Wh.
            double? kwh = root.TryGetProperty("total", out JsonElement tot)
                ? tot.GetDouble() / 1000.0
                : null;

            return new PowerReading(watts, kwh);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to parse Shelly Gen 1 response");
            return null;
        }
    }
}
