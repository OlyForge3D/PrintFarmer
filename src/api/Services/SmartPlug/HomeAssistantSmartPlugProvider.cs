using System.Text.Json;
using Farm.Infrastructure.Services.Security;
using Farm.Infrastructure.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Farm.Web.Api.Services.SmartPlug;

/// <summary>
/// Smart plug provider that reads power state from Home Assistant entities.
/// Token resolution order:
/// 1. <c>HomeAssistant:Token</c> configuration key (env: <c>PFARM__HomeAssistant__Token</c>)
/// 2. Persisted <see cref="HomeAssistantSettings.EncryptedToken"/> (decrypted via <see cref="ISensitiveDataProtector"/>)
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
    IServiceScopeFactory scopeFactory,
    ISensitiveDataProtector dataProtector,
    ILogger<HomeAssistantSmartPlugProvider> logger) : ISmartPlugProvider
{
    public string ProviderType => "HomeAssistant";

    /// <inheritdoc/>
    public async Task<PowerReading?> GetCurrentReadingAsync(string deviceAddress, CancellationToken ct)
    {
        (string? parsedBaseUrl, string entityId) = ParseDeviceAddress(deviceAddress);
        (string? configuredBaseUrl, string? token) = ResolveConnectionParams();

        if (string.IsNullOrWhiteSpace(token))
        {
            logger.LogWarning(
                "HomeAssistant token not configured or integration disabled. Skipping reading for {EntityId}",
                entityId);
            return null;
        }

        // Blocker 5: prefer the address-embedded base URL; fall back to the configured setting.
        // Never fall back to a hardcoded host.
        string baseUrl = parsedBaseUrl ?? configuredBaseUrl ?? string.Empty;
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            logger.LogWarning(
                "HomeAssistant base URL not configured. Skipping reading for {EntityId}",
                entityId);
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
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Blocker 4: surface specific failure reasons so admins can act.
            string reason = ex switch
            {
                TaskCanceledException => "timeout (HA may be offline)",
                HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized }
                    or HttpRequestException { StatusCode: System.Net.HttpStatusCode.Forbidden }
                    => "bad token (401/403 Unauthorized)",
                HttpRequestException { StatusCode: System.Net.HttpStatusCode.NotFound }
                    => $"entity not found — {entityId} returned 404",
                _ => ex.Message
            };
            logger.LogWarning(
                "HomeAssistant GetCurrentReading failed for {EntityId} at {BaseUrl}: {Reason}",
                entityId, baseUrl, reason);
            return null;
        }
    }

    /// <inheritdoc/>
    public async Task<bool> TestConnectionAsync(string deviceAddress, CancellationToken ct)
    {
        (string? parsedBaseUrl, string entityId) = ParseDeviceAddress(deviceAddress);
        (string? configuredBaseUrl, string? token) = ResolveConnectionParams();

        if (string.IsNullOrWhiteSpace(token))
        {
            logger.LogWarning("HomeAssistant token not configured. Cannot test connection for {EntityId}", entityId);
            return false;
        }

        string baseUrl = parsedBaseUrl ?? configuredBaseUrl ?? string.Empty;
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            logger.LogWarning("HomeAssistant base URL not configured. Cannot test connection for {EntityId}", entityId);
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
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "HomeAssistant TestConnection failed for {BaseUrl}", baseUrl);
            return false;
        }
    }

    /// <summary>
    /// Parses deviceAddress formatted as "{baseUrl}|{entityId}".
    /// Returns <c>null</c> for baseUrl when no pipe is present; the caller must then
    /// resolve the base URL from <see cref="HomeAssistantSettings.BaseUrl"/>.
    /// Blocker 5: the hardcoded "homeassistant.local" fallback has been removed —
    /// explicit configuration is required.
    /// </summary>
    private static (string? BaseUrl, string EntityId) ParseDeviceAddress(string deviceAddress)
    {
        int sep = deviceAddress.IndexOf('|', StringComparison.Ordinal);
        if (sep < 0)
        {
            // No base URL in address; caller resolves it from settings.
            return (null, deviceAddress);
        }

        return (deviceAddress[..sep], deviceAddress[(sep + 1)..]);
    }

    /// <summary>
    /// Resolves the HA base URL and long-lived access token.
    /// <para>
    /// <c>Enabled=false</c> is a hard off-switch and takes priority over every token source,
    /// including the <c>PFARM__HomeAssistant__Token</c> env-var override. This ensures that
    /// disabling the integration via the admin UI stops all background polling immediately,
    /// regardless of deployment configuration.
    /// </para>
    /// Token priority (when enabled): raw configuration key (<c>HomeAssistant:Token</c>) →
    /// persisted encrypted token.
    /// </summary>
    private (string? ConfiguredBaseUrl, string? Token) ResolveConnectionParams()
    {
        // ISettingsService is scoped; create a short-lived scope from the singleton provider.
        using IServiceScope scope = scopeFactory.CreateScope();
        HomeAssistantSettings settings = scope.ServiceProvider
            .GetRequiredService<ISettingsService>()
            .Get<HomeAssistantSettings>();

        // Enabled=false is checked first — before any token source — so that the admin
        // toggle reliably stops polling even when PFARM__HomeAssistant__Token is set.
        if (!settings.Enabled)
        {
            logger.LogDebug("HomeAssistant integration is disabled — skipping token resolution");
            return (settings.BaseUrl, null);
        }

        // Config-level token allows access in dev/admin scenarios without persisted settings.
        string? configToken = configuration["HomeAssistant:Token"];
        if (!string.IsNullOrWhiteSpace(configToken))
        {
            return (settings.BaseUrl, configToken);
        }

        string? token = !string.IsNullOrWhiteSpace(settings.EncryptedToken)
            ? dataProtector.Unprotect(settings.EncryptedToken)
            : null;

        return (settings.BaseUrl, token);
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
                // Unit-aware conversion: HA may report in kW; the rest of the system stores watts.
                // Only kW needs conversion — W entities are already in the correct unit.
                if (attrs.TryGetProperty("unit_of_measurement", out JsonElement uomEl)
                    && (uomEl.GetString() ?? string.Empty) == "kW")
                {
                    watts *= 1000.0;
                }

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
