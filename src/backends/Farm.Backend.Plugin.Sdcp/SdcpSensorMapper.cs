using Farm.Infrastructure;
using Farm.Infrastructure.Services.Printers;

namespace Farm.Backend.Plugin.Sdcp;

/// <summary>Maps only quantities actually present in an SDCP status packet.</summary>
internal static class SdcpSensorMapper
{
    internal static IReadOnlyList<PrinterSensorDto> Map(SdcpStatus status)
    {
        List<PrinterSensorDto> sensors = [];
        AddTemperature(sensors, "TempOfNozzle", "Nozzle", PrinterSensorKind.Nozzle, status.TempOfNozzle, status.TempTargetNozzle);
        AddTemperature(sensors, "TempOfHotbed", "Bed", PrinterSensorKind.Bed, status.TempOfHotbed, status.TempTargetHotbed);
        AddTemperature(sensors, "TempOfBox", "Chamber", PrinterSensorKind.Chamber, status.TempOfBox, status.TempTargetBox);
        AddFan(sensors, "ModelFan", "Part cooling fan", PrinterFanRole.PartCooling, status.CurrentFanSpeed?.ModelFan);
        AddFan(sensors, "AuxiliaryFan", "Auxiliary fan", PrinterFanRole.Auxiliary, status.CurrentFanSpeed?.AuxiliaryFan);
        AddFan(sensors, "BoxFan", "Chamber fan", PrinterFanRole.Chamber, status.CurrentFanSpeed?.BoxFan);

        // Cmd 403 fan/chamber writes have not been verified on a device/firmware pair (#3408).
        // Telemetry does not prove write support: all entries deliberately have no Writable descriptors.
        // LightStatus is actuator state, not a measured quantity; resin UV/LED fields vary by firmware.
        return PrinterSensorNormalizer.Normalize(sensors);
    }

    /// <summary>Legacy part-cooling percentage, or null when ModelFan is absent or out of range.</summary>
    internal static double? GetPartCoolingPercent(SdcpStatus status) =>
        status.CurrentFanSpeed?.ModelFan is int speed and >= 0 and <= 100 ? speed : null;

    private static void AddTemperature(List<PrinterSensorDto> sensors, string id, string name, PrinterSensorKind kind, double? actual, double? target)
    {
        List<PrinterSensorReadingDto> readings = [];
        if (actual is double temperature && double.IsFinite(temperature))
        {
            readings.Add(new(PrinterSensorQuantity.Temperature, PrinterSensorValue.FromNumber(temperature), PrinterSensorUnit.Celsius));
        }

        if (target is double targetTemperature && double.IsFinite(targetTemperature))
        {
            readings.Add(new(PrinterSensorQuantity.TargetTemperature, PrinterSensorValue.FromNumber(targetTemperature), PrinterSensorUnit.Celsius));
        }

        if (readings.Count > 0)
        {
            sensors.Add(new(id, name, kind) { Readings = readings });
        }
    }

    private static void AddFan(List<PrinterSensorDto> sensors, string id, string name, PrinterFanRole role, int? speed)
    {
        if (speed is int percent and >= 0 and <= 100)
        {
            PrinterSensorReadingDto reading = new(PrinterSensorQuantity.FanSpeed, PrinterSensorValue.FromNumber(percent), PrinterSensorUnit.Percent);
            sensors.Add(new(id, name, PrinterSensorKind.Fan) { FanRole = role, Readings = [reading] });
        }
    }
}
