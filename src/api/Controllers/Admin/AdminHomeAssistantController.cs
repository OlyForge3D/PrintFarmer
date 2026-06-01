using System.Text.Json;
using System.Text.Json.Serialization;
using Farm.Infrastructure.Services.Security;
using Farm.Infrastructure.Settings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Controllers.Admin;

/// <summary>
/// Admin endpoints for the optional Home Assistant integration:
/// settings persistence, connection test, and entity discovery.
/// </summary>
[ApiController]
[Route("api/admin/integrations/home-assistant")]
[Authorize(Roles = "farm_admin")]
[Tags("Admin - Home Assistant Integration")]
public class AdminHomeAssistantController(
    ISettingsService settingsService,
    ISensitiveDataProtector dataProtector,
    IHttpClientFactory httpClientFactory,
    ILogger<AdminHomeAssistantController> logger) : ControllerBase
{
    private const string TokenMaskPrefix = "***";

    // ──────────────────────────────────────────────────────────────────────────
    // Settings

    /// <summary>
    /// Returns current Home Assistant integration settings.
    /// The token is masked: only the last 4 characters are visible.
    /// </summary>
    [HttpGet("settings")]
    [ProducesResponseType(typeof(HomeAssistantSettingsDto), StatusCodes.Status200OK)]
    public ActionResult<HomeAssistantSettingsDto> GetSettings()
    {
        HomeAssistantSettings settings = settingsService.Get<HomeAssistantSettings>();
        return Ok(MapToDto(settings));
    }

    /// <summary>
    /// Persists Home Assistant integration settings.
    /// If <see cref="UpdateHomeAssistantSettingsRequest.Token"/> is a non-masked value, it is
    /// encrypted before storage. Passing the masked placeholder leaves the existing token unchanged.
    /// </summary>
    [HttpPut("settings")]
    [ProducesResponseType(typeof(HomeAssistantSettingsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<HomeAssistantSettingsDto> UpdateSettings(
        [FromBody] UpdateHomeAssistantSettingsRequest request)
    {
        HomeAssistantSettings settings = settingsService.Get<HomeAssistantSettings>();

        settings.Enabled = request.Enabled;
        settings.BaseUrl = request.BaseUrl?.Trim() ?? string.Empty;

        // Only overwrite the stored token when the caller provides a real (non-masked) value.
        if (!string.IsNullOrWhiteSpace(request.Token) && !request.Token.StartsWith(TokenMaskPrefix, StringComparison.Ordinal))
        {
            settings.EncryptedToken = dataProtector.Protect(request.Token) ?? string.Empty;
        }

        try
        {
            settings.Validate();
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }

        settingsService.Save(settings);
        return Ok(MapToDto(settings));
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Connection test

    /// <summary>
    /// Tests connectivity to the configured Home Assistant instance.
    /// Returns the HA version and the count of power-capable entities discovered.
    /// Always returns 200; inspect <c>success</c> to determine outcome.
    /// </summary>
    [HttpPost("test")]
    [ProducesResponseType(typeof(HomeAssistantConnectionTestResult), StatusCodes.Status200OK)]
    public async Task<ActionResult<HomeAssistantConnectionTestResult>> TestConnectionAsync(
        CancellationToken ct)
    {
        // Blocker 2: honor the Enabled toggle before attempting any outbound HA request.
        HomeAssistantSettings current = settingsService.Get<HomeAssistantSettings>();
        if (!current.Enabled)
        {
            return Ok(new HomeAssistantConnectionTestResult
            {
                Success = false,
                Message = "Home Assistant integration is disabled."
            });
        }

        (string baseUrl, string? token) = ResolveConnectionDetails();

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return Ok(new HomeAssistantConnectionTestResult
            {
                Success = false,
                Message = "Home Assistant base URL is not configured."
            });
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            return Ok(new HomeAssistantConnectionTestResult
            {
                Success = false,
                Message = "Home Assistant token is not configured."
            });
        }

        try
        {
            using HttpClient client = CreateAuthorizedClient(token);
            string version = await FetchHaVersionAsync(client, baseUrl, ct);
            int entityCount = await CountPowerEntitiesAsync(client, baseUrl, ct);

            return Ok(new HomeAssistantConnectionTestResult
            {
                Success = true,
                Version = version,
                PowerEntityCount = entityCount,
                Message = "Connected"
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Blocker 4: map specific HA failure modes to actionable admin messages.
            string message = ex switch
            {
                TaskCanceledException => "Connection timed out — Home Assistant may be offline.",
                HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized }
                    or HttpRequestException { StatusCode: System.Net.HttpStatusCode.Forbidden }
                    => "Authentication failed — check your long-lived access token.",
                HttpRequestException { StatusCode: System.Net.HttpStatusCode.NotFound }
                    => "HA API endpoint not found — check the base URL.",
                _ => ex.Message
            };
            logger.LogDebug(ex, "Home Assistant connection test failed for {BaseUrl}", baseUrl);
            return Ok(new HomeAssistantConnectionTestResult
            {
                Success = false,
                Message = message
            });
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Entity discovery

    /// <summary>
    /// Lists Home Assistant entities that expose power (W) or energy (kWh) measurements.
    /// Matches <c>sensor.*</c> and <c>switch.*</c> entities whose device class or
    /// unit of measurement indicates power or energy.
    /// </summary>
    [HttpGet("entities")]
    [ProducesResponseType(typeof(IEnumerable<HomeAssistantEntityDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IEnumerable<HomeAssistantEntityDto>>> DiscoverEntitiesAsync(
        CancellationToken ct)
    {
        // Blocker 2: honor the Enabled toggle.
        HomeAssistantSettings current = settingsService.Get<HomeAssistantSettings>();
        if (!current.Enabled)
        {
            return BadRequest(new { error = "Home Assistant integration is disabled." });
        }

        (string baseUrl, string? token) = ResolveConnectionDetails();

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return BadRequest(new { error = "Home Assistant base URL is not configured." });
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            return BadRequest(new { error = "Home Assistant token is not configured." });
        }

        try
        {
            using HttpClient client = CreateAuthorizedClient(token);
            List<HomeAssistantEntityDto> entities = await FetchPowerEntitiesAsync(client, baseUrl, ct);
            return Ok(entities);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Blocker 4: map specific HA failure modes to actionable admin messages.
            string message = ex switch
            {
                TaskCanceledException => "Connection timed out — Home Assistant may be offline.",
                HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized }
                    or HttpRequestException { StatusCode: System.Net.HttpStatusCode.Forbidden }
                    => "Authentication failed — check your long-lived access token.",
                HttpRequestException { StatusCode: System.Net.HttpStatusCode.NotFound }
                    => "HA API endpoint not found — check the base URL.",
                _ => $"Discovery failed: {ex.Message}"
            };
            logger.LogWarning(ex, "Home Assistant entity discovery failed for {BaseUrl}", baseUrl);
            return BadRequest(new { error = message });
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Helpers
    private (string BaseUrl, string? Token) ResolveConnectionDetails()
    {
        HomeAssistantSettings settings = settingsService.Get<HomeAssistantSettings>();
        string baseUrl = settings.BaseUrl?.Trim() ?? string.Empty;
        string? token = string.IsNullOrWhiteSpace(settings.EncryptedToken)
            ? null
            : dataProtector.Unprotect(settings.EncryptedToken);
        return (baseUrl, token);
    }

    private HttpClient CreateAuthorizedClient(string token)
    {
        // Use a named client so the factory's timeout/handler settings apply.
        HttpClient client = httpClientFactory.CreateClient("SmartPlug");
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static async Task<string> FetchHaVersionAsync(HttpClient client, string baseUrl, CancellationToken ct)
    {
        string url = $"{baseUrl.TrimEnd('/')}/api/";
        using HttpResponseMessage response = await client.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();

        await using System.IO.Stream stream = await response.Content.ReadAsStreamAsync(ct);
        using JsonDocument doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        if (doc.RootElement.TryGetProperty("version", out JsonElement ver))
        {
            return ver.GetString() ?? string.Empty;
        }

        return string.Empty;
    }

    private static async Task<int> CountPowerEntitiesAsync(HttpClient client, string baseUrl, CancellationToken ct)
    {
        List<HomeAssistantEntityDto> entities = await FetchPowerEntitiesAsync(client, baseUrl, ct);
        return entities.Count;
    }

    private static async Task<List<HomeAssistantEntityDto>> FetchPowerEntitiesAsync(
        HttpClient client, string baseUrl, CancellationToken ct)
    {
        string url = $"{baseUrl.TrimEnd('/')}/api/states";
        using HttpResponseMessage response = await client.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();

        await using System.IO.Stream stream = await response.Content.ReadAsStreamAsync(ct);
        using JsonDocument doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        List<HomeAssistantEntityDto> result = [];

        foreach (JsonElement entity in doc.RootElement.EnumerateArray())
        {
            if (!entity.TryGetProperty("entity_id", out JsonElement entityIdEl))
            {
                continue;
            }

            string entityId = entityIdEl.GetString() ?? string.Empty;
            if (!entityId.StartsWith("sensor.", StringComparison.OrdinalIgnoreCase) &&
                !entityId.StartsWith("switch.", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!entity.TryGetProperty("attributes", out JsonElement attrs))
            {
                continue;
            }

            // Include entities that expose power-related units or device classes.
            bool isPowerEntity = IsPowerCapableEntity(attrs);
            if (!isPowerEntity)
            {
                continue;
            }

            string friendlyName = attrs.TryGetProperty("friendly_name", out JsonElement fn)
                ? fn.GetString() ?? entityId
                : entityId;

            string currentState = entity.TryGetProperty("state", out JsonElement stateEl)
                ? stateEl.GetString() ?? string.Empty
                : string.Empty;

            string unitOfMeasurement = attrs.TryGetProperty("unit_of_measurement", out JsonElement uom)
                ? uom.GetString() ?? string.Empty
                : string.Empty;

            result.Add(new HomeAssistantEntityDto
            {
                EntityId = entityId,
                FriendlyName = friendlyName,
                CurrentState = currentState,
                UnitOfMeasurement = unitOfMeasurement
            });
        }

        return result;
    }

    /// <summary>
    /// Returns true only for entities that expose instantaneous power (W or kW).
    /// Blocker 3 fix: restricting to device_class=power and W/kW units prevents the entity
    /// picker from offering kWh, voltage, or current sensors — values the provider would
    /// misinterpret as watts, corrupting power readings.
    /// </summary>
    private static bool IsPowerCapableEntity(JsonElement attrs)
    {
        // Prefer explicit device_class=power (instantaneous watt sensors).
        if (attrs.TryGetProperty("device_class", out JsonElement dc))
        {
            if ((dc.GetString() ?? string.Empty) == "power")
            {
                return true;
            }
        }

        // Fall back to unit_of_measurement for switch entities that expose watts
        // without a device_class, e.g. older Shelly/Sonoff switch.* entities.
        if (attrs.TryGetProperty("unit_of_measurement", out JsonElement uom))
        {
            string unit = uom.GetString() ?? string.Empty;
            if (unit is "W" or "kW")
            {
                return true;
            }
        }

        return false;
    }

    private HomeAssistantSettingsDto MapToDto(HomeAssistantSettings settings) => new()
    {
        Enabled = settings.Enabled,
        BaseUrl = settings.BaseUrl,
        TokenMasked = MaskToken(settings.EncryptedToken, dataProtector)
    };

    private static string MaskToken(string encryptedToken, ISensitiveDataProtector protector)
    {
        if (string.IsNullOrWhiteSpace(encryptedToken))
        {
            return string.Empty;
        }

        string? plain = protector.Unprotect(encryptedToken);
        if (string.IsNullOrWhiteSpace(plain) || plain.Length <= 4)
        {
            return TokenMaskPrefix;
        }

        return $"{TokenMaskPrefix}{plain[^4..]}";
    }
}

// ──────────────────────────────────────────────────────────────────────────────
// DTOs

/// <summary>Returned by GET /settings. Token is always masked.</summary>
public sealed class HomeAssistantSettingsDto
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("baseUrl")]
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Last 4 chars of the token prefixed with "***", or empty if none stored.</summary>
    [JsonPropertyName("tokenMasked")]
    public string TokenMasked { get; set; } = string.Empty;
}

/// <summary>Used by PUT /settings.</summary>
public sealed class UpdateHomeAssistantSettingsRequest
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("baseUrl")]
    public string? BaseUrl { get; set; }

    /// <summary>
    /// Plain-text long-lived access token. Leave as the masked value (starts with "***")
    /// to keep the existing stored token unchanged.
    /// </summary>
    [JsonPropertyName("token")]
    public string? Token { get; set; }
}

/// <summary>Returned by POST /test.</summary>
public sealed class HomeAssistantConnectionTestResult
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("powerEntityCount")]
    public int? PowerEntityCount { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }
}

/// <summary>A single HA entity returned by GET /entities.</summary>
public sealed class HomeAssistantEntityDto
{
    [JsonPropertyName("entityId")]
    public string EntityId { get; set; } = string.Empty;

    [JsonPropertyName("friendlyName")]
    public string FriendlyName { get; set; } = string.Empty;

    [JsonPropertyName("currentState")]
    public string CurrentState { get; set; } = string.Empty;

    [JsonPropertyName("unitOfMeasurement")]
    public string UnitOfMeasurement { get; set; } = string.Empty;
}
