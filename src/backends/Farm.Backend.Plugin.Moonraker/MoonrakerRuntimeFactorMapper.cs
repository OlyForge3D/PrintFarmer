using System.Text.Json;

namespace Farm.Backend.Plugin.Moonraker;

/// <summary>Klipper's public gcode_move factors are multipliers, unlike M220/M221 S percentages.</summary>
internal static class MoonrakerRuntimeFactorMapper
{
    internal static double? ReadPercent(JsonElement gcodeMove, string property)
    {
        if (gcodeMove.ValueKind != JsonValueKind.Object ||
            !gcodeMove.TryGetProperty(property, out JsonElement value) ||
            value.ValueKind != JsonValueKind.Number ||
            !value.TryGetDouble(out double factor) ||
            !double.IsFinite(factor) || factor <= 0)
        {
            return null;
        }

        double percent = factor * 100;
        return double.IsFinite(percent) ? percent : null;
    }
}
