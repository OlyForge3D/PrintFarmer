using System.Globalization;
using Farm.Infrastructure;
using Farm.Infrastructure.Services.Printers;

namespace Farm.Backend.Plugin.FlashForge;

/// <summary>
/// Maps the FlashForge TCP (<c>~M105</c>) status onto backend-neutral sensors. The legacy TCP
/// protocol reports every extruder (<c>T0</c>, <c>T1</c>, …) and the bed as current/target
/// temperatures only; it exposes no chamber, fan speed, or fan control, so none are invented.
/// </summary>
internal static class FlashForgeSensorMapper
{
    internal const string BedId = "bed";

    internal static string ExtruderId(int index) => "T" + index.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Returns the complete sensor snapshot for an online status, or null when the printer was
    /// not reachable (so the cache keeps the last values and marks them unavailable).
    /// </summary>
    public static IReadOnlyList<PrinterSensorDto>? Map(PrinterCompositeStatus status)
    {
        if (!status.IsOnline)
        {
            return null;
        }

        List<PrinterSensorDto> sensors = [];
        foreach ((int index, ExtruderTemperature temperature) in status.ExtruderTemperatures ?? new Dictionary<int, ExtruderTemperature>())
        {
            sensors.Add(Heater(
                ExtruderId(index),
                "Nozzle " + ExtruderId(index),
                PrinterSensorKind.Nozzle,
                temperature.Current,
                temperature.Target));
        }

        if (status.BedTemp is not null || status.BedTarget is not null)
        {
            sensors.Add(Heater(BedId, "Bed", PrinterSensorKind.Bed, status.BedTemp, status.BedTarget));
        }

        return PrinterSensorNormalizer.Normalize(sensors);
    }

    private static PrinterSensorDto Heater(string id, string displayName, PrinterSensorKind kind, double? actual, double? target)
    {
        List<PrinterSensorReadingDto> readings = [];
        if (actual is double temperature)
        {
            readings.Add(new(PrinterSensorQuantity.Temperature, PrinterSensorValue.FromNumber(temperature), PrinterSensorUnit.Celsius));
        }

        if (target is double targetTemperature)
        {
            readings.Add(new(PrinterSensorQuantity.TargetTemperature, PrinterSensorValue.FromNumber(targetTemperature), PrinterSensorUnit.Celsius));
        }

        return new PrinterSensorDto(id, displayName, kind) { Readings = readings };
    }
}
