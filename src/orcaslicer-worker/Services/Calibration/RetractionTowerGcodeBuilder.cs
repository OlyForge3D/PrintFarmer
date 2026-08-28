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
/// conditionals and the <c>layer_z</c> variable, issuing a per-band firmware-retraction-length
/// command.
/// <para>
/// <b>Firmware-flavour decision (mirrors <see cref="PressureAdvanceTowerGcodeBuilder"/> for the
/// exact same reason):</b> the command that sets the firmware's retraction length at runtime is
/// not one gcode command — it is <c>M207 S...</c> on Marlin/Marlin2, but Marlin's <c>M207</c>/
/// <c>M208</c> are not recognized by Klipper at all (confirmed against Klipper's own docs: Klipper
/// only supports firmware retraction via <c>G10</c>/<c>G11</c> plus its own runtime
/// <c>SET_RETRACTION RETRACT_LENGTH=...</c> command, configured under <c>[firmware_retraction]</c>
/// in <c>printer.cfg</c>). Emitting <c>M207</c> unconditionally on a Klipper machine would be
/// silently ignored as an unknown gcode: the tower would print top to bottom with the firmware's
/// one static configured retraction length, never sweeping at all, and the job would still report
/// success — exactly the silent no-op failure mode this calibration method must never produce.
/// This builder therefore requires the caller to resolve and pass the firmware flavour (see
/// <see cref="PressureAdvanceTowerGcodeBuilder.TryResolveFirmwareFlavor"/>, reused here rather than
/// duplicated) and emits the matching command per flavour.
/// </para>
/// <para>
/// On Marlin, the emitted <c>M207</c> only takes effect when the printer profile has firmware
/// retraction enabled (<c>use_firmware_retraction</c>): with it on, retract/unretract moves become
/// <c>G10</c>/<c>G11</c> commands that use whatever length the firmware last had set via
/// <c>M207</c>; without it, the slicer bakes a fixed retraction length into <c>G1 E...</c> moves at
/// slice time, which injected gcode cannot then change. On Klipper, <c>SET_RETRACTION</c> has the
/// same requirement: it has no effect unless <c>use_firmware_retraction</c> is on (so the slicer
/// emits <c>G10</c>/<c>G11</c>) and the machine's Klipper config has a <c>[firmware_retraction]</c>
/// section. <see cref="OrcaSlicingPipelineService.ApplyRetractionTowerGcodeAsync"/> forces
/// <c>use_firmware_retraction</c> on in the machine profile for retraction-tower jobs on either
/// flavour so this sweep has any physical effect; it cannot verify the Klipper-side
/// <c>[firmware_retraction]</c> config exists, since that lives outside any profile the worker
/// sees.
/// </para>
/// </summary>
public static class RetractionTowerGcodeBuilder
{
    /// <summary>
    /// Builds a per-band firmware-retraction-length command chain whose retraction length
    /// increases by <paramref name="retractionStepMm"/> millimetres every
    /// <paramref name="bandHeightMm"/> millimetres of print height, for
    /// <paramref name="bandCount"/> total bands starting at <paramref name="startRetractionMm"/>,
    /// using <paramref name="flavor"/>'s command syntax (see the type-level remarks on why this is
    /// not one command across firmwares).
    /// </summary>
    /// <param name="flavor">The already-resolved firmware flavour (see <see cref="PressureAdvanceTowerGcodeBuilder.TryResolveFirmwareFlavor"/>).</param>
    /// <param name="startRetractionMm">The retraction length of the bottom-most band (band 0), in millimetres.</param>
    /// <param name="retractionStepMm">The retraction length increase applied per band above band 0, in millimetres.</param>
    /// <param name="bandHeightMm">The print height of each band, in millimetres.</param>
    /// <param name="bandCount">The total number of bands (must be at least 1).</param>
    /// <returns>
    /// A gcode snippet such as
    /// <c>M207 S{if layer_z >= 35}1.4{elsif layer_z >= 30}1.2{else}0.2{endif}</c> (Marlin) or
    /// <c>SET_RETRACTION RETRACT_LENGTH={if layer_z >= 35}1.4{elsif layer_z >= 30}1.2{else}0.2{endif}</c>
    /// (Klipper), evaluated by OrcaSlicer once per layer change.
    /// </returns>
    public static string BuildLayerChangeGcode(
        CalibrationFirmwareFlavor flavor,
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

        (string prefix, string suffix) = flavor switch
        {
            CalibrationFirmwareFlavor.Marlin => ("M207 S", "\n"),
            CalibrationFirmwareFlavor.Klipper => ("SET_RETRACTION RETRACT_LENGTH=", "\n"),
            _ => throw new ArgumentOutOfRangeException(nameof(flavor), flavor, "Unsupported firmware flavour."),
        };

        if (bandCount == 1)
        {
            // A single band has no threshold to branch on: emitting a conditional chain here
            // would produce a bare "{else}...{endif}" with no matching "{if}", which OrcaSlicer's
            // gcode-placeholder processor rejects. Emit a plain, unconditional command instead.
            return $"{prefix}{Math.Round(startRetractionMm, 4).ToString(CultureInfo.InvariantCulture)}{suffix}";
        }

        var sb = new StringBuilder();
        sb.Append(prefix);

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

        sb.Append("{else}").Append(Math.Round(startRetractionMm, 4).ToString(CultureInfo.InvariantCulture)).Append("{endif}").Append(suffix);
        return sb.ToString();
    }
}
