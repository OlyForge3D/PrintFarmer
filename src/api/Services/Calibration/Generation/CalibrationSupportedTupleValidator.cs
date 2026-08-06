using Farm.Infrastructure.PrinterCalibration;

namespace Farm.Web.Api.Services.Calibration.Generation;

/// <summary>
/// Validates the single supported compatibility tuple, conjunctively and fail closed.
/// </summary>
/// <remarks>
/// Every element must match exactly. Nothing is inferred from a manufacturer, printer model, backend
/// kind, alias, or Moonraker/OctoPrint response, and a missing element is never synthesized. The
/// container and binary digests must be present and non-empty: an unverifiable pinned image is a
/// dependency error, not something the compiler is allowed to invent.
/// </remarks>
public static class CalibrationSupportedTupleValidator
{
    /// <summary>Appends a problem for every tuple element that is not an exact match.</summary>
    /// <param name="identity">The authoritative compatibility identity.</param>
    /// <param name="problems">The problem list to append to.</param>
    /// <param name="compatibilityPolicy">Configured upstream OrcaSlicer allow-list.</param>
    public static void Validate(
        CalibrationCompatibilityIdentity identity,
        List<CalibrationGenerationProblem> problems,
        CalibrationSlicerCompatibilityPolicy? compatibilityPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(problems);

        if (!string.Equals(
            identity.FirmwareFamily,
            CalibrationSupportedTuple.FirmwareFamily,
            StringComparison.Ordinal))
        {
            problems.Add(new(
                CalibrationGenerationProblemCodes.FirmwareFamilyUnsupported,
                "context.compatibility.firmwareFamily",
                "Calibration generation supports only the Klipper firmware family."));
        }

        if (!string.Equals(
            identity.GcodeDialect,
            CalibrationSupportedTuple.GcodeDialect,
            StringComparison.Ordinal))
        {
            problems.Add(new(
                CalibrationGenerationProblemCodes.GcodeDialectUnsupported,
                "context.compatibility.gcodeDialect",
                "Calibration generation supports only the Klipper G-code dialect."));
        }

        if (!string.Equals(
            identity.SlicerEngine,
            CalibrationSupportedTuple.SlicerEngine,
            StringComparison.Ordinal))
        {
            problems.Add(new(
                CalibrationGenerationProblemCodes.SlicerEngineUnsupported,
                "context.compatibility.slicerEngine",
                "Calibration generation supports only the OrcaSlicer engine."));
        }

        if (!string.Equals(
            identity.SlicerDistribution,
            CalibrationSupportedTuple.SlicerDistribution,
            StringComparison.Ordinal))
        {
            problems.Add(new(
                CalibrationGenerationProblemCodes.SlicerDistributionUnsupported,
                "context.compatibility.slicerDistribution",
                "Calibration generation supports only the upstream OrcaSlicer distribution."));
        }

        CalibrationSlicerCompatibilityPolicy policy =
            compatibilityPolicy ?? CalibrationSlicerCompatibilityPolicy.Default;
        if (!policy.IsSupported(identity.SlicerVersion))
        {
            problems.Add(new(
                CalibrationGenerationProblemCodes.SlicerVersionUnsupported,
                "context.compatibility.slicerVersion",
                $"OrcaSlicer version '{identity.SlicerVersion ?? "unknown"}' is unsupported; configured versions: {string.Join(", ", policy.SupportedVersions)}."));
        }

        if (string.IsNullOrWhiteSpace(identity.SlicerContainerDigest))
        {
            problems.Add(new(
                CalibrationGenerationProblemCodes.SlicerContainerDigestMissing,
                "context.compatibility.slicerContainerDigest",
                "The authoritative pinned slicer container digest is unavailable."));
        }

        if (string.IsNullOrWhiteSpace(identity.SlicerBinarySha256))
        {
            problems.Add(new(
                CalibrationGenerationProblemCodes.SlicerBinaryDigestMissing,
                "context.compatibility.slicerBinarySha256",
                "The authoritative pinned slicer binary digest is unavailable."));
        }

        if (!string.Equals(
            identity.ProfileFormat,
            CalibrationSupportedTuple.ProfileFormat,
            StringComparison.Ordinal))
        {
            problems.Add(new(
                CalibrationGenerationProblemCodes.ProfileFormatUnsupported,
                "context.compatibility.profileFormat",
                "Calibration generation supports only the upstream OrcaSlicer profile format."));
        }
    }

    /// <summary>Determines whether an identity is an exact match for the supported tuple.</summary>
    /// <param name="identity">The authoritative compatibility identity.</param>
    /// <param name="compatibilityPolicy">Configured upstream OrcaSlicer allow-list.</param>
    /// <returns><see langword="true"/> when every element matches exactly.</returns>
    public static bool IsSupported(
        CalibrationCompatibilityIdentity identity,
        CalibrationSlicerCompatibilityPolicy? compatibilityPolicy = null)
    {
        List<CalibrationGenerationProblem> problems = [];
        Validate(identity, problems, compatibilityPolicy);
        return problems.Count == 0;
    }
}
