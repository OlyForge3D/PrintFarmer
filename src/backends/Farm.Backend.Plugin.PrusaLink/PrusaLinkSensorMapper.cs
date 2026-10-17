using Farm.Infrastructure;
using Farm.Infrastructure.Contracts.Printers.PrusaLink;
using Farm.Infrastructure.Services.Printers;

namespace Farm.Backend.Plugin.PrusaLink;

/// <summary>
/// Maps the PrusaLink v1 <c>/api/v1/status</c> printer block onto backend-neutral sensors.
/// PrusaLink reports nozzle and bed temperatures/targets and two fan speeds
/// (<c>fan_print</c>, <c>fan_hotend</c>) in RPM. PrusaLink has no fan-speed write endpoint,
/// so every entry is read-only; temperature targets stay on the existing temperature controls.
/// No chamber field is reported by the v1 status model, so no chamber entry is invented.
/// </summary>
internal static class PrusaLinkSensorMapper
{
    internal const string NozzleId = "nozzle";
    internal const string BedId = "bed";
    internal const string PrintFanId = "fan_print";
    internal const string HotendFanId = "fan_hotend";

    public static IReadOnlyList<PrinterSensorDto> Map(StatusPrinterInfo? printer)
    {
        if (printer is null)
        {
            return PrinterSensorNormalizer.Normalize([]);
        }

        List<PrinterSensorDto> sensors = [];
        AddHeater(sensors, NozzleId, "Nozzle", PrinterSensorKind.Nozzle, printer.TempNozzle, printer.TargetNozzle);
        AddHeater(sensors, BedId, "Bed", PrinterSensorKind.Bed, printer.TempBed, printer.TargetBed);
        AddFan(sensors, PrintFanId, "Print Fan", PrinterFanRole.PartCooling, printer.FanPrint);
        AddFan(sensors, HotendFanId, "Hotend Fan", PrinterFanRole.Hotend, printer.FanHotend);
        return PrinterSensorNormalizer.Normalize(sensors);
    }

    private static void AddHeater(
        List<PrinterSensorDto> sensors,
        string id,
        string displayName,
        PrinterSensorKind kind,
        double? actual,
        double? target)
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

        if (readings.Count > 0)
        {
            sensors.Add(new PrinterSensorDto(id, displayName, kind) { Readings = readings });
        }
    }

    private static void AddFan(List<PrinterSensorDto> sensors, string id, string displayName, PrinterFanRole role, int? rpm)
    {
        if (rpm is int value)
        {
            sensors.Add(new PrinterSensorDto(id, displayName, PrinterSensorKind.Fan)
            {
                FanRole = role,
                Readings = [new(PrinterSensorQuantity.FanRpm, PrinterSensorValue.FromNumber(value), PrinterSensorUnit.Rpm)],
            });
        }
    }
}
