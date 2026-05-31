using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Farm.Web.Api.Services.HomeAssistant;

/// <summary>
/// Implementation of <see cref="IHomeAssistantClient"/> using the HA REST API.
/// Reference: https://developers.home-assistant.io/docs/api/rest/
/// </summary>
public sealed class HomeAssistantClient(
    IHttpClientFactory httpClientFactory,
    ILogger<HomeAssistantClient> logger) : IHomeAssistantClient
{
    /// <inheritdoc/>
    public async Task<HomeAssistantConnectionResult> TestConnectionAsync(
        string baseUrl, string token, CancellationToken ct)
    {
        try
        {
            using HttpClient client = CreateClient(baseUrl, token);

            // GET /api/ → {"message":"API running."}
            using HttpResponseMessage apiResponse = await client.GetAsync("api/", ct);
            if (!apiResponse.IsSuccessStatusCode)
            {
                return new HomeAssistantConnectionResult(
                    false, null, null,
                    $"HA API returned HTTP {(int)apiResponse.StatusCode}");
            }

            string? version = null;
            try
            {
                await using Stream stream = await apiResponse.Content.ReadAsStreamAsync(ct);
                using JsonDocument doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                JsonElement root = doc.RootElement;
                if (root.TryGetProperty("ha_version", out JsonElement ver))
                {
                    version = ver.GetString();
                }
            }
            catch (JsonException)
            {
                // version is optional — not a failure
            }

            // GET /api/states to get entity count
            int? entityCount = null;
            try
            {
                using HttpResponseMessage statesResponse = await client.GetAsync("api/states", ct);
                if (statesResponse.IsSuccessStatusCode)
                {
                    await using Stream statesStream = await statesResponse.Content.ReadAsStreamAsync(ct);
                    using JsonDocument statesDoc = await JsonDocument.ParseAsync(statesStream, cancellationToken: ct);
                    if (statesDoc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        entityCount = statesDoc.RootElement.GetArrayLength();
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "Could not fetch entity count from HA at {BaseUrl}", baseUrl);
            }

            return new HomeAssistantConnectionResult(true, version, entityCount, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "HomeAssistant TestConnection failed for {BaseUrl}", baseUrl);
            return new HomeAssistantConnectionResult(false, null, null, ex.Message);
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<HomeAssistantEntityInfo>> GetPowerEntitiesAsync(
        string baseUrl, string token, CancellationToken ct)
    {
        try
        {
            using HttpClient client = CreateClient(baseUrl, token);
            using HttpResponseMessage response = await client.GetAsync("api/states", ct);
            response.EnsureSuccessStatusCode();

            await using Stream stream = await response.Content.ReadAsStreamAsync(ct);
            using JsonDocument doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            List<HomeAssistantEntityInfo> results = [];
            foreach (JsonElement entity in doc.RootElement.EnumerateArray())
            {
                HomeAssistantEntityInfo? info = TryParseEntity(entity);
                if (info != null && IsPowerCapable(info))
                {
                    results.Add(info);
                }
            }

            return results;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "HomeAssistant GetPowerEntities failed for {BaseUrl}", baseUrl);
            return [];
        }
    }

    /// <inheritdoc/>
    public async Task<HomeAssistantEntityState?> GetStateAsync(
        string baseUrl, string token, string entityId, CancellationToken ct)
    {
        try
        {
            using HttpClient client = CreateClient(baseUrl, token);
            using HttpResponseMessage response = await client.GetAsync($"api/states/{entityId}", ct);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using Stream stream = await response.Content.ReadAsStreamAsync(ct);
            using JsonDocument doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            JsonElement root = doc.RootElement;

            string state = root.TryGetProperty("state", out JsonElement stateEl)
                ? stateEl.GetString() ?? string.Empty
                : string.Empty;

            Dictionary<string, object?> attrs = [];
            if (root.TryGetProperty("attributes", out JsonElement attrsEl) &&
                attrsEl.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty prop in attrsEl.EnumerateObject())
                {
                    attrs[prop.Name] = ExtractValue(prop.Value);
                }
            }

            return new HomeAssistantEntityState(entityId, state, attrs);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "HomeAssistant GetState failed for {EntityId} at {BaseUrl}", entityId, baseUrl);
            return null;
        }
    }

    private HttpClient CreateClient(string baseUrl, string token)
    {
        HttpClient client = httpClientFactory.CreateClient("HomeAssistant");
        client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static HomeAssistantEntityInfo? TryParseEntity(JsonElement entity)
    {
        if (!entity.TryGetProperty("entity_id", out JsonElement idEl))
        {
            return null;
        }

        string entityId = idEl.GetString() ?? string.Empty;
        string domain = entityId.Split('.')[0];
        string state = entity.TryGetProperty("state", out JsonElement stateEl)
            ? stateEl.GetString() ?? string.Empty
            : string.Empty;

        string friendlyName = entityId;
        string? deviceClass = null;

        if (entity.TryGetProperty("attributes", out JsonElement attrs))
        {
            if (attrs.TryGetProperty("friendly_name", out JsonElement fn))
            {
                friendlyName = fn.GetString() ?? entityId;
            }

            if (attrs.TryGetProperty("device_class", out JsonElement dc))
            {
                deviceClass = dc.GetString();
            }
        }

        return new HomeAssistantEntityInfo(entityId, friendlyName, domain, deviceClass, state);
    }

    /// <summary>
    /// Returns true for switch domains or power/energy sensor device classes.
    /// </summary>
    private static bool IsPowerCapable(HomeAssistantEntityInfo info)
    {
        if (info.Domain == "switch")
        {
            return true;
        }

        if (info.Domain == "sensor" &&
            info.DeviceClass is "power" or "energy" or "current" or "voltage")
        {
            return true;
        }

        return false;
    }

    private static object? ExtractValue(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.Number when el.TryGetDouble(out double d) => d,
        JsonValueKind.String => el.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => el.ToString(),
    };
}
