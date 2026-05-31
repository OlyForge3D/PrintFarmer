namespace Farm.Web.Api.Services.HomeAssistant;

/// <summary>
/// Thin client for the Home Assistant REST API.
/// All methods take explicit baseUrl and token to remain stateless and testable.
/// </summary>
public interface IHomeAssistantClient
{
    /// <summary>
    /// Tests connectivity and token validity.
    /// Returns HA version and entity count on success.
    /// </summary>
    Task<HomeAssistantConnectionResult> TestConnectionAsync(string baseUrl, string token, CancellationToken ct);

    /// <summary>
    /// Returns all switch and power/energy sensor entities from HA,
    /// suitable for use as smart plugs in PrintFarmer.
    /// </summary>
    Task<IReadOnlyList<HomeAssistantEntityInfo>> GetPowerEntitiesAsync(string baseUrl, string token, CancellationToken ct);

    /// <summary>
    /// Returns the current state of a single HA entity.
    /// </summary>
    Task<HomeAssistantEntityState?> GetStateAsync(string baseUrl, string token, string entityId, CancellationToken ct);
}

public sealed record HomeAssistantConnectionResult(
    bool Success,
    string? Version,
    int? EntityCount,
    string? ErrorMessage);

public sealed record HomeAssistantEntityInfo(
    string EntityId,
    string FriendlyName,
    string Domain,
    string? DeviceClass,
    string State);

public sealed record HomeAssistantEntityState(
    string EntityId,
    string State,
    IReadOnlyDictionary<string, object?> Attributes);
