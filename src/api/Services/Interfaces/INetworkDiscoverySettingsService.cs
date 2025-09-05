using Farm.Web.Shared;

namespace Farm.Web.Api.Services.Interfaces;

/// <summary>
/// Manages network discovery configuration settings including network ranges, timeouts, and port scanning settings.
/// </summary>
public interface INetworkDiscoverySettingsService
{
    /// <summary>
    /// Gets the current network discovery settings including network ranges and scanning parameters.
    /// </summary>
    /// <returns>Current network discovery configuration</returns>
    NetworkDiscoverySettingsDto GetSettings();

    /// <summary>
    /// Saves new network discovery settings, replacing the current configuration.
    /// </summary>
    /// <param name="settings">Network discovery settings including CIDR ranges, timeouts, and ports</param>
    void SaveSettings(NetworkDiscoverySettingsDto settings);
}
