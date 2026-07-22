namespace Farm.Web.Api.Services.SmartPlug;

/// <summary>
/// Abstraction for smart plug providers that expose real-time power readings.
/// </summary>
public interface ISmartPlugProvider
{
    /// <summary>
    /// Gets the unique provider type identifier (e.g., "Kasa", "Tasmota", "Shelly", "HomeAssistant").
    /// </summary>
    string ProviderType { get; }

    /// <summary>
    /// Returns the current power reading from the device at <paramref name="deviceAddress"/>.
    /// Returns null if the device is offline or the reading is unavailable.
    /// </summary>
    Task<PowerReading?> GetCurrentReadingAsync(string deviceAddress, CancellationToken ct);

    /// <summary>
    /// Verifies connectivity to the device at <paramref name="deviceAddress"/>.
    /// </summary>
    Task<bool> TestConnectionAsync(string deviceAddress, CancellationToken ct);
}
