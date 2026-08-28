using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Farm.OrcaSlicer.Worker.Services.Calibration;

/// <summary>
/// Applies per-object flow-ratio overrides to OrcaSlicer's linear-regression "YOLO" flow-rate
/// calibration projects (issue #2141): <c>Orca-LinearFlow.3mf</c> ("Recommended") and
/// <c>Orca-LinearFlow_fine.3mf</c> ("Perfectionist").
/// <para>
/// Unlike the legacy two-pass towers (<see cref="FlowRateCalibrationConfigurator"/>), these
/// projects encode each printable object's target flow ratio as a signed delta relative to the
/// filament's own baseline flow ratio — for example <c>flowrate_0.01</c> (+0.01) or
/// <c>flowrate_m0.01</c> (-0.01, the <c>m</c> prefix standing in for a minus sign that cannot
/// appear in a 3MF object name). The effective per-object flow ratio is
/// <c>baseline + delta</c>, where the baseline is the source filament profile's current
/// <c>filament_flow_ratio</c>. Reusing <see cref="FlowRateCalibrationConfigurator"/>'s absolute-
/// percentage parser here would silently mis-scale or skip every object (issue #2051) — this
/// class exists so that never happens.
/// </para>
/// </summary>
public static partial class FlowRateDeltaCalibrationConfigurator
{
    // Mirrors FlowRateCalibrationConfigurator's separator tolerance ("flow rate", "flow-rate",
    // "flow_rate", "flowrate"), with an optional "m" immediately before the digits standing in
    // for a minus sign (3MF object names cannot carry a literal '-').
    [GeneratedRegex(@"flow[_\s-]*rate[_\s-]*(m)?(\d+(?:\.\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex FlowRateDeltaObjectNamePattern();

    /// <summary>
    /// Parses an object's name for an embedded baseline-relative flow-ratio delta (for example
    /// <c>"flowrate_0.01"</c> for +0.01, or <c>"flowrate_m0.01"</c> for -0.01). Returns
    /// <see langword="null"/> when the name carries no recognizable delta — the caller must treat
    /// that as a hard failure (see <see cref="ResolveObjectFlowRatios"/>), never as "no override".
    /// </summary>
    public static double? TryParseFlowDelta(string? objectName)
    {
        if (string.IsNullOrWhiteSpace(objectName))
        {
            return null;
        }

        Match match = FlowRateDeltaObjectNamePattern().Match(objectName);
        if (!match.Success)
        {
            return null;
        }

        double magnitude = double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        return match.Groups[1].Success ? -magnitude : magnitude;
    }

    /// <summary>
    /// Resolves the effective <c>baseline + delta</c> flow ratio for every object, refusing rather
    /// than guessing when any object's name carries no recognizable delta.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown for the first object whose name cannot be parsed as a delta. A calibration slice
    /// that silently skipped or mis-scaled such an object would defeat the entire point of the
    /// calibration (it must produce a distinct, correctly-offset flow ratio per block), so this
    /// fails the whole job instead of degrading silently.
    /// </exception>
    public static IReadOnlyDictionary<int, double> ResolveObjectFlowRatios(
        IEnumerable<(int Id, string? Name)> objects,
        double baselineFlowRatio)
    {
        ArgumentNullException.ThrowIfNull(objects);

        var result = new Dictionary<int, double>();
        foreach ((int id, string? name) in objects)
        {
            double? delta = TryParseFlowDelta(name);
            if (delta is null)
            {
                throw new InvalidOperationException(
                    $"Calibration object '{name ?? "(unnamed)"}' (id {id.ToString(CultureInfo.InvariantCulture)}) " +
                    "does not carry a recognizable delta-based flow-rate name (expected 'flowrate_<delta>' or " +
                    "'flowrate_m<delta>'); refusing rather than guessing its flow ratio.");
            }

            result[id] = baselineFlowRatio + delta.Value;
        }

        return result;
    }

    /// <summary>
    /// Copies <paramref name="source3mfPath"/> into <paramref name="destinationDirectory"/> and
    /// injects the per-object <c>flow_ratio</c> overrides — <paramref name="baselineFlowRatio"/>
    /// plus each object's parsed delta — into the copy's <c>Metadata/Slic3r_PE_model.config</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the resource is missing its model entry, carries an unparseable object name, or
    /// otherwise cannot be parsed/rewritten. See <see cref="ResolveObjectFlowRatios"/> for why an
    /// unparseable name fails the whole job rather than being skipped.
    /// </exception>
    public static string ApplyPerObjectFlowRatioDeltas(
        string source3mfPath,
        string destinationDirectory,
        double baselineFlowRatio,
        ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source3mfPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        ArgumentNullException.ThrowIfNull(logger);

        // Path.GetFileName never returns a rooted path in practice, but guard explicitly so the
        // intent is verifiable by inspection rather than relying on GetFileName's undocumented
        // behavior. Use Path.Join (not Path.Combine) for the actual concatenation: unlike
        // Path.Combine, Path.Join never discards earlier segments even if a later one were
        // rooted, so it structurally cannot exhibit CodeQL's cs/path-combine finding.
        string sourceFileName = Path.GetFileName(source3mfPath);
        if (string.IsNullOrEmpty(sourceFileName) || Path.IsPathRooted(sourceFileName))
        {
            throw new InvalidOperationException(
                $"Calibration resource path '{source3mfPath}' does not resolve to a valid file name.");
        }

        string destinationPath = Path.Join(destinationDirectory, sourceFileName);
        File.Copy(source3mfPath, destinationPath, overwrite: true);

        try
        {
            using ZipArchive archive = ZipFile.Open(destinationPath, ZipArchiveMode.Update);
            ZipArchiveEntry? modelEntry = archive.GetEntry("3D/3dmodel.model");
            if (modelEntry is null)
            {
                // Log the path server-side only; the thrown message (which can surface as a job
                // failure reason) intentionally omits the internal filesystem layout.
                logger.LogError(
                    "Calibration project '{Path}' has no 3D/3dmodel.model entry.",
                    source3mfPath);
                throw new InvalidOperationException(
                    "Calibration resource is not a valid 3MF project (missing 3D/3dmodel.model); " +
                    "cannot apply per-object flow-ratio overrides.");
            }

            string modelXml = FlowRateCalibrationConfigurator.ReadEntryText(modelEntry);
            IReadOnlyList<(int Id, string? Name)> objects = FlowRateCalibrationConfigurator.ParseObjectNames(modelXml);
            if (objects.Count == 0)
            {
                logger.LogError(
                    "Calibration project '{Path}' has no printable objects.",
                    source3mfPath);
                throw new InvalidOperationException(
                    "Calibration resource has no printable objects; cannot apply per-object flow-ratio overrides.");
            }

            // Resolution failures are not caught here: HttpJobPollerService already logs the
            // failed job's exception once (job-level catch), so logging again on the way up would
            // duplicate the same stack trace in the logs for no benefit.
            IReadOnlyDictionary<int, double> flowRatios = ResolveObjectFlowRatios(objects, baselineFlowRatio);

            ZipArchiveEntry? configEntry = archive.GetEntry("Metadata/Slic3r_PE_model.config");
            string? existingConfigXml = configEntry is null ? null : FlowRateCalibrationConfigurator.ReadEntryText(configEntry);
            string newConfigXml = FlowRateCalibrationConfigurator.BuildObjectConfigXml(flowRatios, existingConfigXml);

            configEntry?.Delete();
            ZipArchiveEntry newConfigEntry = archive.CreateEntry("Metadata/Slic3r_PE_model.config");
            FlowRateCalibrationConfigurator.WriteEntryText(newConfigEntry, newConfigXml);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or System.Xml.XmlException)
        {
            logger.LogError(
                ex,
                "Failed to apply per-object flow-ratio deltas to calibration project '{Path}'.",
                source3mfPath);
            throw new InvalidOperationException(
                "Failed to parse or rewrite the calibration resource's 3MF model/metadata; " +
                "cannot apply per-object flow-ratio overrides.",
                ex);
        }

        return destinationPath;
    }
}
