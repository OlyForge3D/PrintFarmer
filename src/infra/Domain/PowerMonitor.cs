namespace Farm.Infrastructure.Domain;

public class PowerMonitor
{
    public int Id { get; set; }

    public Guid PrinterId { get; set; }

    public Printer Printer { get; set; } = null!;

    /// <summary>
    /// Provider type identifier: "Kasa", "Tasmota", "Shelly", or "HomeAssistant".
    /// </summary>
    public string ProviderType { get; set; } = string.Empty;

    /// <summary>
    /// IP address, hostname, or device-specific address string used by the provider.
    /// </summary>
    public string DeviceAddress { get; set; } = string.Empty;

    /// <summary>
    /// Per-printer electricity rate override. When zero the farm-wide fallback from
    /// <c>CostTrackingSettings.ElectricityRatePerKwh</c> is used.
    /// </summary>
    public decimal ElectricityRateUsdPerKwh { get; set; }

    public bool IsEnabled { get; set; } = true;

    public ICollection<PowerReading> Readings { get; set; } = [];
}
