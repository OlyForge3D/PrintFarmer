using Farm.Infrastructure.PrinterCalibration;
using Farm.Web.Api.Services.Gcode.Safety;

namespace Farm.Web.Api.Services.Calibration.Generation;

/// <summary>The lifecycle point a static safety validation is being performed for.</summary>
public enum CalibrationSafetyCheckpoint
{
    /// <summary>Unknown. Never a valid value.</summary>
    Unspecified = 0,

    /// <summary>Before a worker artifact is accepted as complete.</summary>
    BeforeArtifactCompletion = 1,

    /// <summary>Before an artifact is promoted into the G-code library.</summary>
    BeforePromotion = 2,

    /// <summary>Before a print job is queued.</summary>
    BeforeQueueing = 3,

    /// <summary>Before a print job is started.</summary>
    BeforeStart = 4,
}

/// <summary>Everything the static validator needs, with no ambient state.</summary>
/// <param name="Specification">The compiled specification the G-code must match.</param>
/// <param name="Plan">The compiled plan the G-code must match.</param>
/// <param name="Manifest">The manifest that must describe the G-code.</param>
/// <param name="Gcode">The final annotated G-code.</param>
/// <param name="Checkpoint">The lifecycle point being validated.</param>
/// <param name="CurrentPrinterConfigurationRevision">The printer revision observed now.</param>
/// <param name="ObservedAtUtc">The evaluation time used for freshness.</param>
public sealed record CalibrationGcodeSafetyRequest(
    CalibrationSpecification Specification,
    OrcaCalibrationPlan Plan,
    CalibrationGcodeManifest Manifest,
    string Gcode,
    CalibrationSafetyCheckpoint Checkpoint,
    long CurrentPrinterConfigurationRevision,
    DateTime ObservedAtUtc);

/// <summary>A successful static validation record.</summary>
/// <param name="Checkpoint">The lifecycle point that was validated.</param>
/// <param name="GcodeSha256">The digest of the validated G-code.</param>
/// <param name="CommandCount">The number of interpreted commands.</param>
/// <param name="ValidatedAtUtc">When the validation ran.</param>
public sealed record CalibrationGcodeSafetyReport(
    CalibrationSafetyCheckpoint Checkpoint,
    string GcodeSha256,
    int CommandCount,
    DateTime ValidatedAtUtc);

/// <summary>
/// Reject-only static validation of emitted calibration G-code.
/// </summary>
/// <remarks>
/// The validator never rewrites, repairs or normalizes G-code. It parses the program statefully and
/// either returns a clean report or the ordered reasons the program must not be completed, promoted,
/// queued or started. It is safe and intended to run at every one of those lifecycle points.
/// </remarks>
public interface ICalibrationGcodeProgramValidator
{
    /// <summary>Validates emitted G-code against its specification, plan and manifest.</summary>
    /// <param name="request">The complete validation request.</param>
    /// <returns>The clean report, or the ordered rejection reasons.</returns>
    CalibrationGenerationResult<CalibrationGcodeSafetyReport> Validate(
        CalibrationGcodeSafetyRequest request);
}

/// <summary>Default <see cref="ICalibrationGcodeProgramValidator"/>.</summary>
/// <remarks>
/// This adapter composes two independent concerns: calibration-only provenance/digest/manifest
/// matching (<see cref="ValidateProvenance"/>, which never moved) and general physical g-code safety
/// checking, which is delegated to the calibration-independent <see cref="IGcodeSafetyValidator"/>
/// extracted from the calibration-only validator originally added in PR #947. The general
/// validator is given the calibration generator's trusted command allowlist, so previously-
/// enforced calibration behavior is unchanged.
/// </remarks>
public sealed class CalibrationGcodeProgramValidator(
    IGcodeSafetyValidator gcodeSafetyValidator,
    CalibrationSlicerCompatibilityPolicy? compatibilityPolicy = null)
    : ICalibrationGcodeProgramValidator
{
    private readonly IGcodeSafetyValidator _gcodeSafetyValidator =
        gcodeSafetyValidator ?? throw new ArgumentNullException(nameof(gcodeSafetyValidator));

    private readonly CalibrationSlicerCompatibilityPolicy _compatibilityPolicy =
        compatibilityPolicy ?? CalibrationSlicerCompatibilityPolicy.Default;

    /// <inheritdoc/>
    public CalibrationGenerationResult<CalibrationGcodeSafetyReport> Validate(
        CalibrationGcodeSafetyRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrEmpty(request.Gcode))
        {
            return CalibrationGenerationResults.Failure<CalibrationGcodeSafetyReport>(
                CalibrationGenerationProblemCodes.GcodeMalformed,
                "gcode",
                "The calibration program is empty.");
        }

        List<CalibrationGenerationProblem> problems = [];
        CalibrationSpecificationDocument document = request.Specification.Document;

        ValidateProvenance(request, document, problems);

        GcodeSafetyResult<GcodeSafetyReport> safetyResult = _gcodeSafetyValidator.Validate(
            new GcodeSafetyRequest(
                ToSafetyLimits(document),
                request.Gcode,
                GcodeSafetyCheckpoint.BeforeArtifactCompletion,
                KlipperCalibrationCommands.Allowlist));

        foreach (GcodeSafetyProblem problem in safetyResult.Problems)
        {
            problems.Add(new(problem.Code, problem.Field, problem.Message));
        }

        return problems.Count > 0
            ? CalibrationGenerationResults.Failure<CalibrationGcodeSafetyReport>(problems)
            : CalibrationGenerationResults.Success(new CalibrationGcodeSafetyReport(
                request.Checkpoint,
                request.Manifest.GcodeSha256,
                safetyResult.Value?.CommandCount ?? 0,
                DateTime.SpecifyKind(request.ObservedAtUtc, DateTimeKind.Utc)));
    }

    /// <summary>
    /// Maps the compiled calibration specification's authoritative machine envelope to the
    /// calibration-independent <see cref="GcodeSafetyLimits"/> shape, coalescing every ceiling to the
    /// same fail-closed default (zero, or an empty geometry list) the original validator applied when
    /// a resolved calibration document field was unset. This preserves the exact original calibration
    /// behavior: <see cref="IGcodeSafetyValidator"/> itself treats an unset ceiling as "skip this
    /// check", which is the correct, more lenient default for arbitrary send-to-printer machines whose
    /// optional profile fields were never configured, but is not what calibration's own compiled,
    /// resolved document ever intended by a null field.
    /// </summary>
    private static GcodeSafetyLimits ToSafetyLimits(CalibrationSpecificationDocument document)
    {
        CalibrationToolheadContext toolhead = document.Toolhead;
        CalibrationMachineLimits limits = document.Limits;
        CalibrationBedGeometry bed = document.Bed;
        CalibrationPrintParameters print = document.Print;

        return new GcodeSafetyLimits(
            new GcodeSafetyToolheadLimits(
                toolhead.NozzleMaxTemperatureCelsius ?? toolhead.HotendMaxTemperatureCelsius ?? 0,
                null,
                toolhead.IsDirectDrive),
            new GcodeSafetyBedLimits(
                bed.SizeXMillimeters,
                bed.SizeYMillimeters,
                bed.SizeZMillimeters,
                bed.OriginXMillimeters,
                bed.OriginYMillimeters,
                bed.PrintablePolygon.Select(point => new GcodeSafetyPoint(point.X, point.Y)).ToArray(),
                bed.ExcludedRegions
                    .Select(region => new GcodeSafetyExcludedRegion(
                        region.Name,
                        region.Polygon.Select(point => new GcodeSafetyPoint(point.X, point.Y)).ToArray()))
                    .ToArray()),
            new GcodeSafetyMachineLimits(
                limits.MaxBedTemperatureCelsius ?? 0,
                limits.HasHeatedChamber,
                limits.MaxChamberTemperatureCelsius ?? 0,
                limits.MaxPrintSpeedMillimetersPerSecond ?? 0,
                limits.MaxTravelSpeedMillimetersPerSecond ?? 0,
                limits.MaxAcceleration ?? 0),
            new GcodeSafetyPrintLimits(
                print.FilamentDiameterMillimeters,
                print.MaxVolumetricFlow));
    }

    private void ValidateProvenance(
        CalibrationGcodeSafetyRequest request,
        CalibrationSpecificationDocument document,
        List<CalibrationGenerationProblem> problems)
    {
        CalibrationSupportedTupleValidator.Validate(
            document.Compatibility,
            problems,
            _compatibilityPolicy);

        if (!string.Equals(
            request.Manifest.FirmwareFamily,
            CalibrationSupportedTuple.FirmwareFamily,
            StringComparison.Ordinal))
        {
            problems.Add(new(
                CalibrationGenerationProblemCodes.FirmwareFamilyUnsupported,
                "manifest.firmwareFamily",
                "The manifest declares an unsupported firmware family."));
        }

        if (!string.Equals(
            request.Manifest.GcodeDialect,
            CalibrationSupportedTuple.GcodeDialect,
            StringComparison.Ordinal))
        {
            problems.Add(new(
                CalibrationGenerationProblemCodes.GcodeDialectUnsupported,
                "manifest.gcodeDialect",
                "The manifest declares an unsupported G-code dialect."));
        }

        string computed = CalibrationCanonicalJson.ComputeTextSha256(request.Gcode);
        if (!CalibrationCanonicalJson.DigestsMatch(computed, request.Manifest.GcodeSha256))
        {
            problems.Add(new(
                CalibrationGenerationProblemCodes.GcodeHashMismatch,
                "manifest.gcodeSha256",
                "The manifest digest does not match the supplied G-code."));
        }

        if (!CalibrationCanonicalJson.DigestsMatch(
            request.Manifest.SpecificationSha256,
            request.Specification.Sha256))
        {
            problems.Add(new(
                CalibrationGenerationProblemCodes.SpecificationHashMismatch,
                "manifest.specificationSha256",
                "The manifest references a different specification."));
        }

        if (!CalibrationCanonicalJson.DigestsMatch(
            request.Manifest.PlanManifestSha256,
            request.Plan.ManifestSha256))
        {
            problems.Add(new(
                CalibrationGenerationProblemCodes.ManifestMismatch,
                "manifest.planManifestSha256",
                "The manifest references a different plan."));
        }

        // The annotated program records baseline provenance digests, so they are what the manifest
        // must agree with; the effective documents are the worker's contract, not the program's.
        CompareDigest(
            request.Manifest.MachineProfileSha256,
            request.Plan.MachineProfile.SourceSha256,
            "manifest.machineProfileSha256",
            problems);
        CompareDigest(
            request.Manifest.ProcessProfileSha256,
            request.Plan.ProcessProfile.SourceSha256,
            "manifest.processProfileSha256",
            problems);
        CompareDigest(
            request.Manifest.FilamentProfileSha256,
            request.Plan.FilamentProfile.SourceSha256,
            "manifest.filamentProfileSha256",
            problems);

        if (!CalibrationCanonicalJson.DigestsMatch(
            request.Manifest.PrinterConfigurationSnapshotSha256,
            document.PrinterConfigurationSnapshotSha256))
        {
            problems.Add(new(
                CalibrationGenerationProblemCodes.SnapshotHashMismatch,
                "manifest.printerConfigurationSnapshotSha256",
                "The manifest references a different printer configuration snapshot."));
        }

        if (document.PrinterConfigurationRevision != request.CurrentPrinterConfigurationRevision)
        {
            problems.Add(new(
                CalibrationGenerationProblemCodes.PrinterConfigurationStale,
                "specification.printerConfigurationRevision",
                "The printer configuration changed after this program was generated."));
        }

        if (!string.Equals(
            request.Manifest.SlicerVersion,
            document.Compatibility.SlicerVersion,
            StringComparison.Ordinal))
        {
            problems.Add(new(
                CalibrationGenerationProblemCodes.SlicerVersionUnsupported,
                "manifest.slicerVersion",
                "The manifest slicer version does not match the exact version recorded by the specification."));
        }

        if (string.IsNullOrWhiteSpace(request.Manifest.SlicerContainerDigest))
        {
            problems.Add(new(
                CalibrationGenerationProblemCodes.SlicerContainerDigestMissing,
                "manifest.slicerContainerDigest",
                "The manifest does not record the pinned slicer container digest."));
        }

        if (request.Checkpoint == CalibrationSafetyCheckpoint.Unspecified)
        {
            problems.Add(new(
                CalibrationGenerationProblemCodes.ManifestMismatch,
                "request.checkpoint",
                "A static safety validation must declare its lifecycle checkpoint."));
        }
    }

    private static void CompareDigest(
        string manifestDigest,
        string planDigest,
        string field,
        List<CalibrationGenerationProblem> problems)
    {
        if (!CalibrationCanonicalJson.DigestsMatch(manifestDigest, planDigest))
        {
            problems.Add(new(
                CalibrationGenerationProblemCodes.ProfileHashMismatch,
                field,
                "The manifest references a different exact profile than the plan."));
        }
    }
}
