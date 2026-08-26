namespace Farm.Web.Api.Services.SmartPlug;

/// <summary>
/// Instantaneous power reading from a smart plug device.
/// </summary>
/// <param name="WattsNow">Current power consumption in watts.</param>
/// <param name="TotalKwh">Accumulated energy in kWh since last reset, if available from the device.</param>
/// <param name="Voltage">Line voltage in volts, if reported by the device.</param>
/// <param name="CurrentAmps">Line current in amps, if reported by the device.</param>
public record PowerReading(
    double WattsNow,
    double? TotalKwh = null,
    double? Voltage = null,
    double? CurrentAmps = null);
