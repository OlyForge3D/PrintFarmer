using System.Globalization;
using System.Text;

namespace Farm.OrcaSlicer.Worker.Services.Calibration;

/// <summary>
/// Builds the <c>layer_change_gcode</c> snippet for a temperature tower calibration slice (issue
/// #1938). OrcaSlicer's GUI-only per-band configuration (Plater.cpp) is not reachable from the
/// worker's CLI-driven pipeline, so this reimplements the same idea as a plain custom-gcode
/// template using OrcaSlicer's built-in <c>{if}/{elsif}/{else}/{endif}</c> placeholder
/// conditionals and the <c>layer_z</c> variable, which the CLI slicer does evaluate.
/// </summary>
public static class TemperatureTowerGcodeBuilder
{
    /// <summary>
    /// Builds a single <c>M104 S...</c> line whose target temperature steps down by
    /// <paramref name="temperatureStepC"/> degrees every <paramref name="bandHeightMm"/>
    /// millimetres of print height, for <paramref name="bandCount"/> total bands starting at
    /// <paramref name="startTemperatureC"/>.
    /// </summary>
    /// <param name="startTemperatureC">The temperature of the bottom-most band (band 0).</param>
    /// <param name="temperatureStepC">The temperature decrease applied per band above band 0.</param>
    /// <param name="bandHeightMm">The print height of each band, in millimetres.</param>
    /// <param name="bandCount">The total number of bands (must be at least 1).</param>
    /// <returns>
    /// A gcode snippet such as
    /// <c>M104 S{if layer_z >= 80}190{elsif layer_z >= 70}195{else}200{endif}</c>, evaluated by
    /// OrcaSlicer once per layer change.
    /// </returns>
    public static string BuildLayerChangeGcode(
        double startTemperatureC,
        double temperatureStepC,
        double bandHeightMm,
        int bandCount)
    {
        if (bandCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(bandCount), bandCount, "At least one band is required.");
        }

        if (bandHeightMm <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bandHeightMm), bandHeightMm, "Band height must be positive.");
        }

        var sb = new StringBuilder();
        sb.Append("M104 S");

        // {if}/{elsif} chains evaluate top-to-bottom and stop at the first true branch, so the
        // tallest band (highest layer_z threshold) must be checked first.
        for (int band = bandCount - 1; band >= 1; band--)
        {
            double threshold = band * bandHeightMm;
            double temperature = startTemperatureC - (band * temperatureStepC);
            string keyword = band == bandCount - 1 ? "if" : "elsif";
            sb.Append('{').Append(keyword).Append(" layer_z >= ")
                .Append(threshold.ToString(CultureInfo.InvariantCulture)).Append('}')
                .Append(temperature.ToString(CultureInfo.InvariantCulture));
        }

        sb.Append("{else}").Append(startTemperatureC.ToString(CultureInfo.InvariantCulture)).Append("{endif}\n");
        return sb.ToString();
    }
}
