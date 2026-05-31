using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Farm.Web.Api.Services.SmartPlug;

/// <summary>
/// Smart plug provider that reads power state from Home Assistant entities.
/// Requires a long-lived access token stored in configuration key
/// <c>HomeAssistant:Token</c> (env: <c>PFARM__HomeAssistant__Token</c>).
///
/// <para>
/// The device address passed to <see cref="ISmartPlugProvider.GetCurrentReadingAsync"/> must be
/// formatted as <c>{ha_base_url}|{entity_id}</c>, e.g.
/// <c>http://homeassistant.local:8123|sensor.plug_power</c>.
/// </para>
/// </summary>
public sealed class HomeAssistantSmartPlugProvider(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ILogger<HomeAssistantSmartPlugProvider> logger) : ISmartPlugProvider
{
    public string ProviderType => "HomeAssistant";

    /// <inheritdoc/>
    public async Task<PowerReading?> GetCurrentReadingAsync(string deviceAddress, CancellationToken ct)
    {
        (string baseUrl, string entityId) = ParseDeviceAddress(deviceAddress);
        string? token = configuration["HomeAssistant:Token"];

        if (string.IsNullOrWhiteSpace(token))
        {
            logger.LogWarning("HomeAssistant token not configured (PFARM__HomeAssistant__Token). Skipping reading for {EntityId}", entityId);
            return null;
        }

        try
        {
            HttpClient client = httpClientFactory.CreateClient("SmartPlug");
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            string url = $"{baseUrl.TrimEnd('/')}/api/states/{entityId}";
            using HttpResponseMessage response = await client.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();

            await using System.IO.Stream stream = await response.Content.ReadAsStreamAsync(ct);
            return ParseStateResponse(stream, entityId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "HomeAssistant GetCurrentReading failed for entity {EntityId} at {BaseUrl}", entityId, baseUrl);
            return null;
        }
    }

    /// <inheritdoc/>
    public async Task<bool> TestConnectionAsync(string deviceAddress, CancellationToken ct)
    {
        (string baseUrl, string entityId) = ParseDeviceAddress(deviceAddress);
        string? token = configuration["HomeAssistant:Token"];

        if (string.IsNullOrWhiteSpace(token))
        {
            logger.LogWarning("HomeAssistant token not configured. Cannot test connection for {EntityId}", entityId);
            return false;
        }

        try
        {
            HttpClient client = httpClientFactory.CreateClient("SmartPlug");
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            string url = $"{baseUrl.TrimEnd('/')}/api/";
            using HttpResponseMessage response = await client.GetAsync(url, ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "HomeAssistant TestConnection failed for {BaseUrl}", baseUrl);
            return false;
        }
    }

    /// <summary>
    /// Parses deviceAddress formatted as "{baseUrl}|{entityId}".
    /// Falls back to treating the whole string as an entity ID with no base URL when no pipe is present.
    /// </summary>
    private static (string BaseUrl, string EntityId) ParseDeviceAddress(string deviceAddress)
    {
        int sep = deviceAddress.IndexOf('|', StringComparison.Ordinal);
        if (sep < 0)
        {
            // Legacy / simple: treat whole string as entity; caller must have base URL in config.
            return ("http://homeassistant.local:8123", deviceAddress);
        }

        return (deviceAddress[..sep], deviceAddress[(sep + 1)..]);
    }

    private PowerReading? ParseStateResponse(System.IO.Stream stream, string entityId)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(stream);
            JsonElement root = doc.RootElement;

            // HA state object: {"entity_id":"...", "state":"23.5", "attributes":{...}}
            if (!root.TryGetProperty("state", out JsonElement stateEl))
            {
                return null;
            }

            string stateStr = stateEl.GetString() ?? string.Empty;

            if (!double.TryParse(stateStr, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out double watts))
            {
                logger.LogWarning("HomeAssistant entity {EntityId} state '{State}' is not a numeric watt value", entityId, stateStr);
                return null;
            }

            // Extract optional attributes if the entity exposes them
            double? kwh = null;
            double? volts = null;
            double? amps = null;

            if (root.TryGetProperty("attributes", out JsonElement attrs))
            {
                if (attrs.TryGetProperty("total_increasing", out JsonElement ti))
                {
                    kwh = ti.GetDouble();
                }
                else if (attrs.TryGetProperty("energy", out JsonElement en))
                {
                    kwh = en.GetDouble();
                }

                if (attrs.TryGetProperty("voltage", out JsonElement v))
                {
                    volts = v.GetDouble();
                }

                if (attrs.TryGetProperty("current", out JsonElement c))
                {
                    amps = c.GetDouble();
                }
            }

            return new PowerReading(watts, kwh, volts, amps);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to parse HomeAssistant state response for entity {EntityId}", entityId);
            return null;
        }
    }
}
