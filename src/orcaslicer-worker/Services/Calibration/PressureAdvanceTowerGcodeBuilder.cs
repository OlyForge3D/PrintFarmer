using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Farm.OrcaSlicer.Worker.Services.Calibration;

/// <summary>
/// Firmware dialects the pressure advance tower calibration method (issue #2136) knows how to
/// emit gcode for. This is the worker pipeline's own firmware-flavour notion, derived from the
/// machine profile's OrcaSlicer <c>gcode_flavor</c> field (see
/// <see cref="PressureAdvanceTowerGcodeBuilder.TryResolveFirmwareFlavor"/> and
/// <c>OrcaProfilesService.GcodeDialect</c>) — it is unrelated to <c>PrinterGcodeDialect</c>
/// (<c>Farm.Infra.Domain.CalibrationPrinterMetadata</c>), which is a printer-discovery concept
/// that only distinguishes Klipper from "Other".
/// </summary>
public enum CalibrationFirmwareFlavor
{
    /// <summary>Klipper, whose pressure advance is set with <c>SET_PRESSURE_ADVANCE ADVANCE=...</c>.</summary>
    Klipper,

    /// <summary>Marlin (and Marlin2), whose linear advance is set with <c>M900 K...</c>.</summary>
    Marlin,
}

/// <summary>
/// Builds the <c>layer_change_gcode</c> hook for a pressure advance tower calibration slice
/// (issue #2136).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists:</b> OrcaSlicer's GUI-only "PA Tower" calibration mode
/// (<c>Calib_PA_Tower</c> in <c>Plater.cpp</c>) drives per-band pressure-advance changes from the
/// desktop app and is not reachable from the worker's CLI-driven pipeline. This mirrors
/// <see cref="TemperatureTowerGcodeBuilder"/>'s solution to the exact same problem for the
/// temperature tower: reimplement the per-band stepping directly as an OrcaSlicer
/// <c>{if}/{elsif}/{else}/{endif}</c> custom-gcode template keyed on the <c>layer_z</c> built-in
/// placeholder variable.
/// </para>
/// <para>
/// <b>Firmware-flavour decision (stated explicitly per issue #2136):</b> pressure advance is not
/// one gcode command — it is <c>SET_PRESSURE_ADVANCE ADVANCE=...</c> on Klipper and linear advance
/// <c>M900 K...</c> on Marlin/Marlin2. Rather than hard-coding one firmware and silently
/// mis-slicing (or silently no-op'ing) for the other, this builder emits per-flavour gcode for
/// both, resolved from the machine profile's existing <c>gcode_flavor</c> field — the worker
/// pipeline's established firmware-flavour notion (see <c>OrcaProfilesService.GcodeDialect</c>) —
/// rather than inventing a new one. Any other flavour (reprap, reprapfirmware, repetier, unset, or
/// unrecognized) is refused explicitly by <see cref="TryResolveFirmwareFlavor"/>: this calibration
/// method must never silently produce a tower that does nothing.
/// </para>
/// </remarks>
public static class PressureAdvanceTowerGcodeBuilder
{
    /// <summary>
    /// Resolves the calibration firmware flavour from a machine profile's <c>gcode_flavor</c>
    /// value. Returns <see langword="null"/> for any flavour this calibration method does not
    /// (yet) support, so the caller can refuse the job explicitly instead of guessing which
    /// command syntax to emit.
    /// </summary>
    public static CalibrationFirmwareFlavor? TryResolveFirmwareFlavor(string? gcodeFlavor)
    {
        if (string.IsNullOrWhiteSpace(gcodeFlavor))
        {
            return null;
        }

        return gcodeFlavor.Trim().ToLowerInvariant() switch
        {
            "klipper" => CalibrationFirmwareFlavor.Klipper,
            "marlin" or "marlin2" => CalibrationFirmwareFlavor.Marlin,
            _ => null,
        };
    }

    /// <summary>
    /// Reads the machine profile JSON's top-level <c>gcode_flavor</c> string, if present.
    /// Returns <see langword="null"/> for missing/malformed JSON or a missing/non-string field —
    /// callers treat that the same as any other unresolved flavour.
    /// </summary>
    public static string? ReadGcodeFlavor(string? machineProfileJson) => ReadTopLevelString(machineProfileJson, "gcode_flavor");

    /// <summary>
    /// Reads the machine profile JSON's top-level <c>printer_model</c> string, if present. Used to
    /// detect Bambu Lab (BBL) machines, which upstream OrcaSlicer branches on separately from
    /// <c>gcode_flavor</c> (see <see cref="IsBambuLabPrinterModel"/>).
    /// </summary>
    public static string? ReadPrinterModel(string? machineProfileJson) => ReadTopLevelString(machineProfileJson, "printer_model");

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="printerModel"/> identifies a Bambu Lab
    /// (BBL) machine (e.g. <c>"Bambu Lab X1 Carbon 0.4 nozzle"</c>).
    /// </summary>
    /// <remarks>
    /// BBL machine profiles inherit <c>gcode_flavor: "marlin"</c> from
    /// <c>fdm_machine_common</c> in upstream OrcaSlicer, but upstream's own
    /// <c>GCodeWriter::set_pressure_advance</c> branches on a distinct <c>is_bbl_printers</c> flag
    /// *before* consulting <c>gcode_flavor</c> at all, emitting
    /// <c>M900 K{v} L1000 M10 ; Override pressure advance value</c> rather than the generic Marlin
    /// <c>M900 K{v}</c>. Treating BBL's inherited "marlin" flavour as ordinary Marlin would
    /// therefore silently slice a tower with the wrong command for that hardware — exactly the
    /// silent-mis-slice outcome this calibration method must never produce. Since neither this
    /// builder nor <see cref="CalibrationFirmwareFlavor"/> emit BBL's distinct dialect yet, BBL
    /// machines are refused explicitly by the caller instead.
    /// </remarks>
    public static bool IsBambuLabPrinterModel(string? printerModel) =>
        !string.IsNullOrWhiteSpace(printerModel) && printerModel.TrimStart().StartsWith("Bambu Lab", StringComparison.OrdinalIgnoreCase);

    private static string? ReadTopLevelString(string? machineProfileJson, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(machineProfileJson))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(machineProfileJson);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(propertyName, out JsonElement valueElement)
                && valueElement.ValueKind == JsonValueKind.String)
            {
                return valueElement.GetString();
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    /// <summary>
    /// Builds the per-band <c>layer_change_gcode</c> template that raises the pressure/linear
    /// advance coefficient by print height, using <paramref name="flavor"/>'s command syntax.
    /// </summary>
    /// <param name="flavor">The already-resolved firmware flavour (see <see cref="TryResolveFirmwareFlavor"/>).</param>
    /// <param name="startAdvance">Advance value of the bottom-most band.</param>
    /// <param name="advanceStep">Advance increase applied per band.</param>
    /// <param name="bandHeightMm">Print height of each band, in millimetres.</param>
    /// <param name="bandCount">Total number of bands, including the bottom-most one.</param>
    public static string BuildLayerChangeGcode(
        CalibrationFirmwareFlavor flavor,
        double startAdvance,
        double advanceStep,
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
            // A single band has no threshold to branch on -- emit the bare command so the
            // template is never a bare "{else}...{endif}" with no preceding "{if}", which
            // OrcaSlicer's custom-gcode template parser rejects as malformed.
            return BuildSetAdvanceCommand(flavor, startAdvance);
        }

        var builder = new StringBuilder();
        for (int band = bandCount - 1; band >= 1; band--)
        {
            double threshold = band * bandHeightMm;
            double advance = startAdvance + (band * advanceStep);
            string keyword = band == bandCount - 1 ? "if" : "elsif";
            builder
                .Append('{').Append(keyword).Append(" layer_z >= ").Append(threshold.ToString(CultureInfo.InvariantCulture)).Append('}')
                .Append(BuildSetAdvanceCommand(flavor, advance));
        }

        builder.Append("{else}").Append(BuildSetAdvanceCommand(flavor, startAdvance)).Append("{endif}\n");
        return builder.ToString();
    }

    private static string BuildSetAdvanceCommand(CalibrationFirmwareFlavor flavor, double advance) => flavor switch
    {
        CalibrationFirmwareFlavor.Klipper => $"SET_PRESSURE_ADVANCE ADVANCE={advance.ToString(CultureInfo.InvariantCulture)}\n",
        CalibrationFirmwareFlavor.Marlin => $"M900 K{advance.ToString(CultureInfo.InvariantCulture)}\n",
        _ => throw new ArgumentOutOfRangeException(nameof(flavor), flavor, "Unsupported firmware flavour."),
    };
}
