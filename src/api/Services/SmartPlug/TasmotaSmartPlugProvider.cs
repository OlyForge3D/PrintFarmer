using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Farm.Web.Api.Services.SmartPlug;

/// <summary>
/// Smart plug provider for Tasmota firmware devices.
/// Queries the HTTP API endpoint: GET /cm?cmnd=Status%208 (Status 8 = power readings).
/// Works with any Tasmota device that has energy monitoring (e.g., Sonoff POW, NOUS A1T, etc.).
/// </summary>
public sealed class TasmotaSmartPlugProvider(
    IHttpClientFactory httpClientFactory,
    ILogger<TasmotaSmartPlugProvider> logger) : ISmartPlugProvider
{
    public string ProviderType => "Tasmota";

    /// <inheritdoc/>
    public async Task<PowerReading?> GetCurrentReadingAsync(string deviceAddress, CancellationToken ct)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("SmartPlug");

            // Status 8 returns StatusSNS with energy sensor data
            string url = $"http://{deviceAddress}/cm?cmnd=Status%208";
            using HttpResponseMessage response = await client.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();

            await using System.IO.Stream stream = await response.Content.ReadAsStreamAsync(ct);
            return ParseStatus8Response(stream);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Tasmota GetCurrentReading failed for {DeviceAddress}", deviceAddress);
            return null;
        }
    }

    /// <inheritdoc/>
    public async Task<bool> TestConnectionAsync(string deviceAddress, CancellationToken ct)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("SmartPlug");

            // Status 0 returns all status info — lightweight connectivity check
            string url = $"http://{deviceAddress}/cm?cmnd=Status%200";
            using HttpResponseMessage response = await client.GetAsync(url, ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Tasmota TestConnection failed for {DeviceAddress}", deviceAddress);
            return false;
        }
    }

    private PowerReading? ParseStatus8Response(System.IO.Stream stream)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(stream);
            JsonElement root = doc.RootElement;

            // Navigate: {"StatusSNS":{"ENERGY":{"Power":...,"Today":...,"Voltage":...,"Current":...}}}
            if (!root.TryGetProperty("StatusSNS", out JsonElement sns))
            {
                return null;
            }

            if (!sns.TryGetProperty("ENERGY", out JsonElement energy))
            {
                return null;
            }

            double watts = energy.TryGetProperty("Power", out JsonElement pw) ? pw.GetDouble() : 0;
            double? kwh = energy.TryGetProperty("Today", out JsonElement today) ? today.GetDouble() : null;
            double? volts = energy.TryGetProperty("Voltage", out JsonElement v) ? v.GetDouble() : null;
            double? amps = energy.TryGetProperty("Current", out JsonElement c) ? c.GetDouble() : null;

            return new PowerReading(watts, kwh, volts, amps);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to parse Tasmota Status 8 response");
            return null;
        }
    }
}
