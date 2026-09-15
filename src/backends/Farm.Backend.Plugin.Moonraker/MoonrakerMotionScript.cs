using System.Globalization;
using Farm.Infrastructure;
using Farm.Infrastructure.Services.Printers;

namespace Farm.Backend.Plugin.Moonraker;

/// <summary>Encodes bounded Klipper motion and drains the command queue before completion.</summary>
internal static class MoonrakerMotionScript
{
    private static readonly string DecimalFormat = "0." + new string('#', 339);

    public static string BuildScript(PrinterControlRequest request)
    {
        if (!PrinterControlIntent.IsValid(request))
        {
            throw new ArgumentException("Invalid motion intent.", nameof(request));
        }

        string command = request.Kind switch
        {
            PrinterControlKind.HomeAll => "G28",
            PrinterControlKind.HomeXY => "G28 X Y",
            PrinterControlKind.HomeZ => "G28 Z",
            _ => "G1" + Axis("X", request.X) + Axis("Y", request.Y) + Axis("Z", request.Z) + Axis("F", request.F),
        };
        return request.Kind switch
        {
            // Restore the preceding coordinate/extrusion modes without a restore movement.
            PrinterControlKind.Jog => $"SAVE_GCODE_STATE NAME=printfarmer_motion\nG91\n{command}\nRESTORE_GCODE_STATE NAME=printfarmer_motion MOVE=0\nM400",
            PrinterControlKind.MoveTo => $"SAVE_GCODE_STATE NAME=printfarmer_motion\nG90\n{command}\nRESTORE_GCODE_STATE NAME=printfarmer_motion MOVE=0\nM400",
            _ => $"{command}\nM400",
        };
    }

    private static string Axis(string name, double? value) =>
        value.HasValue ? $" {name}{value.Value.ToString(DecimalFormat, CultureInfo.InvariantCulture)}" : string.Empty;
}
