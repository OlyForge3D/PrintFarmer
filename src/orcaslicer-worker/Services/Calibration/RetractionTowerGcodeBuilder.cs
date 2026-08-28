using System.Globalization;
using System.Text;

namespace Farm.OrcaSlicer.Worker.Services.Calibration;

/// <summary>
/// Builds the <c>layer_change_gcode</c> snippet for a retraction tower calibration slice (issue
/// #2137), mirroring <see cref="TemperatureTowerGcodeBuilder"/>'s approach. Upstream OrcaSlicer's
/// native retraction-tower sweep (<c>CalibMode::Calib_Retraction_tower</c> in <c>GCode.cpp</c>)
/// mutates the slicing engine's internal <c>retraction_length</c> config directly, per layer — a
/// hook the CLI-driven worker cannot reach. Unlike the flow-rate towers
/// (<see cref="FlowRateCalibrationConfigurator"/>), the bundled <c>retraction_tower.drc</c>
/// resource is also a single raw Draco mesh with no per-object names or metadata to edit
/// (confirmed: its first bytes are the Draco magic header, not a 3MF/zip container), so there is
/// no per-object config to rewrite either. Instead this reimplements the sweep as a plain
/// custom-gcode template using OrcaSlicer's <c>{if}/{elsif}/{else}/{endif}</c> placeholder
/// conditionals and the <c>layer_z</c> variable, issuing <c>M207 S...</c> ("set firmware retract
/// settings") once per band.
/// <para>
/// <c>M207</c> only takes effect when the printer profile has firmware retraction enabled
/// (<c>use_firmware_retraction</c>): with it on, retract/unretract moves become <c>G10</c>/<c>G11</c>
/// commands that use whatever length the firmware last had set via <c>M207</c>; without it, the
/// slicer bakes a fixed retraction length into <c>G1 E...</c> moves at slice time, which injected
/// gcode cannot then change. <see cref="OrcaSlicingPipelineService.ApplyRetractionTowerGcodeAsync"/>
/// forces <c>use_firmware_retraction</c> on in the machine profile for retraction-tower jobs so
/// this sweep has any physical effect.
/// </para>
/// </summary>
public static class RetractionTowerGcodeBuilder
{
    /// <summary>
    /// Builds a single <c>M207 S...</c> line whose retraction length increases by
    /// <paramref name="retractionStepMm"/> millimetres every <paramref name="bandHeightMm"/>
    /// millimetres of print height, for <paramref name="bandCount"/> total bands starting at
    /// <paramref name="startRetractionMm"/>.
    /// </summary>
    /// <param name="startRetractionMm">The retraction length of the bottom-most band (band 0), in millimetres.</param>
    /// <param name="retractionStepMm">The retraction length increase applied per band above band 0, in millimetres.</param>
    /// <param name="bandHeightMm">The print height of each band, in millimetres.</param>
    /// <param name="bandCount">The total number of bands (must be at least 1).</param>
    /// <returns>
    /// A gcode snippet such as
    /// <c>M207 S{if layer_z >= 35}1.4{elsif layer_z >= 30}1.2{else}0.2{endif}</c>, evaluated by
    /// OrcaSlicer once per layer change.
    /// </returns>
    public static string BuildLayerChangeGcode(
        double startRetractionMm,
        double retractionStepMm,
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

        if (bandCount == 1)
        {
            // A single band has no threshold to branch on: emitting a conditional chain here
            // would produce a bare "{else}...{endif}" with no matching "{if}", which OrcaSlicer's
            // gcode-placeholder processor rejects. Emit a plain, unconditional M207 instead.
            return $"M207 S{Math.Round(startRetractionMm, 4).ToString(CultureInfo.InvariantCulture)}\n";
        }

        var sb = new StringBuilder();
        sb.Append("M207 S");

        // {if}/{elsif} chains evaluate top-to-bottom and stop at the first true branch, so the
        // tallest band (highest layer_z threshold) must be checked first.
        for (int band = bandCount - 1; band >= 1; band--)
        {
            double threshold = Math.Round(band * bandHeightMm, 4);

            // Rounded to avoid emitting floating-point noise (e.g. "1.4000000000000001" instead
            // of "1.4") into the gcode from repeated fractional-millimetre addition/multiplication.
            double retractionLength = Math.Round(startRetractionMm + (band * retractionStepMm), 4);
            string keyword = band == bandCount - 1 ? "if" : "elsif";
            sb.Append('{').Append(keyword).Append(" layer_z >= ")
                .Append(threshold.ToString(CultureInfo.InvariantCulture)).Append('}')
                .Append(retractionLength.ToString(CultureInfo.InvariantCulture));
        }

        sb.Append("{else}").Append(startRetractionMm.ToString(CultureInfo.InvariantCulture)).Append("{endif}\n");
        return sb.ToString();
    }
}
