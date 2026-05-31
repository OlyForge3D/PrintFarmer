namespace Farm.Web.Api.Services.HomeAssistant;

/// <summary>
/// Provides the active Home Assistant connection configuration from persisted settings.
/// Returns null when HA integration is disabled or not configured.
/// </summary>
public interface IHomeAssistantSettingsProvider
{
    Task<HomeAssistantConnectionConfig?> GetEnabledConfigAsync(CancellationToken ct);
}

/// <summary>Decrypted Home Assistant connection parameters ready for use.</summary>
public sealed record HomeAssistantConnectionConfig(string BaseUrl, string Token);
