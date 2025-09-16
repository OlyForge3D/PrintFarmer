using Farm.Web.Shared;

namespace Farm.Web.Api.Services.Interfaces;

/// <summary>
/// Manages SignalR configuration settings including logging levels and console logging preferences.
/// </summary>
public interface ISignalRSettingsService
{
    /// <summary>
    /// Gets the current SignalR settings including logging configuration.
    /// </summary>
    /// <returns>Current SignalR configuration</returns>
    SignalRSettingsDto GetSettings();

    /// <summary>
    /// Saves new SignalR settings, replacing the current configuration.
    /// </summary>
    /// <param name="settings">SignalR settings including logging level and console logging preference</param>
    void SaveSettings(SignalRSettingsDto settings);
}
