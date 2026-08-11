using System.Globalization;
using Farm.Infrastructure.PrinterCalibration;

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
public interface ICalibrationGcodeSafetyValidator
{
    /// <summary>Validates emitted G-code against its specification, plan and manifest.</summary>
    /// <param name="request">The complete validation request.</param>
    /// <returns>The clean report, or the ordered rejection reasons.</returns>
    CalibrationGenerationResult<CalibrationGcodeSafetyReport> Validate(
        CalibrationGcodeSafetyRequest request);
}

/// <summary>Default <see cref="ICalibrationGcodeSafetyValidator"/>.</summary>
public sealed class CalibrationGcodeSafetyValidator(
    CalibrationSlicerCompatibilityPolicy? compatibilityPolicy = null)
    : ICalibrationGcodeSafetyValidator
{
    private readonly CalibrationSlicerCompatibilityPolicy _compatibilityPolicy =
        compatibilityPolicy ?? CalibrationSlicerCompatibilityPolicy.Default;

    /// <summary>Absolute pressure advance ceiling accepted in any emitted program.</summary>
    public const decimal AbsolutePressureAdvanceCeiling = 2.0m;

    /// <summary>Absolute retraction ceiling, in millimetres, accepted in any emitted program.</summary>
    public const decimal AbsoluteRetractionCeiling = 10.0m;

    /// <summary>Volumetric flow tolerance, in mm³/s, applied before a move is rejected.</summary>
    public const decimal VolumetricFlowTolerance = 0.05m;

    /// <summary>Coordinate tolerance, in millimetres, applied before a move is rejected.</summary>
    public const decimal CoordinateTolerance = 0.01m;

    private static readonly string[] HostCommandMarkers =
    [
        "RUN_SHELL_COMMAND",
        "/bin/sh",
        "cmd.exe",
        "powershell ",
        "curl ",
        "wget ",
        "nc ",
        "ssh ",
    ];

    private static readonly string[] CredentialMarkers =
    [
        "api_key",
        "apikey",
        "password",
        "authorization:",
        "bearer ",
        "client_secret",
        "x-worker-key",
    ];

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
        CalibrationInterpreterState state = Interpret(request, document, problems);
        ValidateEnvelope(state, problems);

        return problems.Count > 0
            ? CalibrationGenerationResults.Failure<CalibrationGcodeSafetyReport>(problems)
            : CalibrationGenerationResults.Success(new CalibrationGcodeSafetyReport(
                request.Checkpoint,
                request.Manifest.GcodeSha256,
                state.CommandCount,
                DateTime.SpecifyKind(request.ObservedAtUtc, DateTimeKind.Utc)));
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

    private static CalibrationInterpreterState Interpret(
        CalibrationGcodeSafetyRequest request,
        CalibrationSpecificationDocument document,
        List<CalibrationGenerationProblem> problems)
    {
        CalibrationInterpreterState state = new(document);
        int lineNumber = 0;

        foreach (string rawLine in request.Gcode.Split('\n'))
        {
            lineNumber++;
            string line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            ScanRedaction(line, lineNumber, problems);
            if (line[0] == ';')
            {
                TrackMarker(state, line, lineNumber, problems);
                continue;
            }

            string command = ReadCommand(line);
            if (line.Contains(KlipperCalibrationCommands.TuningTower, StringComparison.OrdinalIgnoreCase))
            {
                Add(
                    problems,
                    CalibrationGenerationProblemCodes.GcodeTuningTowerForbidden,
                    Field(lineNumber),
                    "Calibration G-code must never drive a firmware tuning tower.");
                continue;
            }

            if (!KlipperCalibrationCommands.IsAllowed(command))
            {
                Add(
                    problems,
                    CalibrationGenerationProblemCodes.GcodeCommandNotAllowlisted,
                    Field(lineNumber),
                    "Calibration G-code contains a command outside the trusted allowlist.");
                continue;
            }

            state.CommandCount++;
            Execute(state, command, line, lineNumber, problems);
        }

        return state;
    }

    private static void Execute(
        CalibrationInterpreterState state,
        string command,
        string line,
        int lineNumber,
        List<CalibrationGenerationProblem> problems)
    {
        switch (command)
        {
            case "G28":
                state.Homed = true;
                state.X = 0m;
                state.Y = 0m;
                state.Z = 0m;
                break;
            case "G90":
                state.AbsolutePositioning = true;
                break;
            case "G91":
                state.AbsolutePositioning = false;
                break;
            case "M82":
                state.AbsoluteExtrusion = true;
                break;
            case "M83":
                state.AbsoluteExtrusion = false;
                break;
            case "G92":
                if (TryReadParameter(line, 'E', out decimal reset))
                {
                    state.E = reset;
                }

                break;
            case "M104":
            case "M109":
                ApplyNozzleTemperature(state, line, lineNumber, problems);
                break;
            case "M140":
            case "M190":
                ApplyBedTemperature(state, line, lineNumber, problems);
                break;
            case "M141":
            case "M191":
                ApplyChamberTemperature(state, line, lineNumber, problems);
                break;
            case "M204":
                ApplyAcceleration(state, line, lineNumber, problems);
                break;
            case "SET_VELOCITY_LIMIT":
                ApplyVelocityLimit(state, line, lineNumber, problems);
                break;
            case "SET_PRESSURE_ADVANCE":
                ApplyPressureAdvance(state, line, lineNumber, problems);
                break;
            case "TURN_OFF_HEATERS":
                state.HeatersOff = true;
                state.NozzleTemperature = 0;
                state.BedTemperature = 0;
                break;
            case "M84":
                state.MotorsOff = true;
                break;
            case "G0":
            case "G1":
                ApplyMove(state, line, lineNumber, problems);
                break;
            default:
                break;
        }
    }

    private static void ApplyNozzleTemperature(
        CalibrationInterpreterState state,
        string line,
        int lineNumber,
        List<CalibrationGenerationProblem> problems)
    {
        if (!TryReadParameter(line, 'S', out decimal value))
        {
            return;
        }

        state.NozzleTemperature = value;
        int ceiling = state.Document.Toolhead.NozzleMaxTemperatureCelsius ??
            state.Document.Toolhead.HotendMaxTemperatureCelsius ??
            0;
        if (value > ceiling)
        {
            Add(
                problems,
                CalibrationGenerationProblemCodes.GcodeTemperatureAboveLimit,
                Field(lineNumber),
                "A commanded nozzle temperature exceeds the authoritative ceiling.");
        }
    }

    private static void ApplyBedTemperature(
        CalibrationInterpreterState state,
        string line,
        int lineNumber,
        List<CalibrationGenerationProblem> problems)
    {
        if (!TryReadParameter(line, 'S', out decimal value))
        {
            return;
        }

        state.BedTemperature = value;
        if (value > (state.Document.Limits.MaxBedTemperatureCelsius ?? 0))
        {
            Add(
                problems,
                CalibrationGenerationProblemCodes.GcodeTemperatureAboveLimit,
                Field(lineNumber),
                "A commanded bed temperature exceeds the authoritative ceiling.");
        }
    }

    private static void ApplyChamberTemperature(
        CalibrationInterpreterState state,
        string line,
        int lineNumber,
        List<CalibrationGenerationProblem> problems)
    {
        if (!TryReadParameter(line, 'S', out decimal value))
        {
            return;
        }

        if (state.Document.Limits.HasHeatedChamber != true ||
            value > (state.Document.Limits.MaxChamberTemperatureCelsius ?? 0))
        {
            Add(
                problems,
                CalibrationGenerationProblemCodes.GcodeTemperatureAboveLimit,
                Field(lineNumber),
                "A commanded chamber temperature is unsupported or above the authoritative ceiling.");
        }
    }

    private static void ApplyAcceleration(
        CalibrationInterpreterState state,
        string line,
        int lineNumber,
        List<CalibrationGenerationProblem> problems)
    {
        if (!TryReadParameter(line, 'S', out decimal value))
        {
            return;
        }

        if (value > (state.Document.Limits.MaxAcceleration ?? 0))
        {
            Add(
                problems,
                CalibrationGenerationProblemCodes.GcodeAccelerationAboveLimit,
                Field(lineNumber),
                "A commanded acceleration exceeds the authoritative ceiling.");
        }
    }

    private static void ApplyVelocityLimit(
        CalibrationInterpreterState state,
        string line,
        int lineNumber,
        List<CalibrationGenerationProblem> problems)
    {
        if (TryReadNamedParameter(line, "VELOCITY=", out decimal velocity) &&
            velocity > (state.Document.Limits.MaxTravelSpeedMillimetersPerSecond ?? 0))
        {
            Add(
                problems,
                CalibrationGenerationProblemCodes.GcodeSpeedAboveLimit,
                Field(lineNumber),
                "A commanded velocity limit exceeds the authoritative ceiling.");
        }

        if (TryReadNamedParameter(line, "ACCEL=", out decimal acceleration) &&
            acceleration > (state.Document.Limits.MaxAcceleration ?? 0))
        {
            Add(
                problems,
                CalibrationGenerationProblemCodes.GcodeAccelerationAboveLimit,
                Field(lineNumber),
                "A commanded acceleration limit exceeds the authoritative ceiling.");
        }
    }

    private static void ApplyPressureAdvance(
        CalibrationInterpreterState state,
        string line,
        int lineNumber,
        List<CalibrationGenerationProblem> problems)
    {
        if (!TryReadNamedParameter(line, "ADVANCE=", out decimal value))
        {
            return;
        }

        state.PressureAdvance = value;
        decimal ceiling = Math.Min(
            AbsolutePressureAdvanceCeiling,
            state.Document.Toolhead.IsDirectDrive == true ? 0.5m : 2.0m);
        if (value < 0m || value > ceiling)
        {
            Add(
                problems,
                CalibrationGenerationProblemCodes.GcodePressureAdvanceOutOfRange,
                Field(lineNumber),
                "A commanded pressure advance value is outside the safe range.");
        }
    }

    private static void ApplyMove(
        CalibrationInterpreterState state,
        string line,
        int lineNumber,
        List<CalibrationGenerationProblem> problems)
    {
        decimal previousX = state.X;
        decimal previousY = state.Y;
        bool hasX = TryReadParameter(line, 'X', out decimal x);
        bool hasY = TryReadParameter(line, 'Y', out decimal y);
        bool hasZ = TryReadParameter(line, 'Z', out decimal z);
        bool hasE = TryReadParameter(line, 'E', out decimal e);
        bool hasF = TryReadParameter(line, 'F', out decimal feedRate);

        if (state.AbsolutePositioning)
        {
            if (hasX)
            {
                state.X = x;
            }

            if (hasY)
            {
                state.Y = y;
            }

            if (hasZ)
            {
                state.Z = z;
            }
        }
        else
        {
            if (hasX)
            {
                state.X += x;
            }

            if (hasY)
            {
                state.Y += y;
            }

            if (hasZ)
            {
                state.Z += z;
            }
        }

        decimal extrusion = 0m;
        if (hasE)
        {
            extrusion = state.AbsoluteExtrusion ? e - state.E : e;
            state.E = state.AbsoluteExtrusion ? e : state.E + e;
        }

        if (hasF)
        {
            decimal speed = feedRate / 60m;
            int ceiling = extrusion > 0m
                ? state.Document.Limits.MaxPrintSpeedMillimetersPerSecond ?? 0
                : state.Document.Limits.MaxTravelSpeedMillimetersPerSecond ?? 0;
            if (speed > ceiling)
            {
                Add(
                    problems,
                    CalibrationGenerationProblemCodes.GcodeSpeedAboveLimit,
                    Field(lineNumber),
                    "A commanded feed rate exceeds the authoritative speed ceiling.");
            }

            state.FeedRate = feedRate;
        }

        if (extrusion < 0m)
        {
            decimal retraction = -extrusion;
            state.TransitionRetracted = true;
            decimal ceiling = Math.Min(
                AbsoluteRetractionCeiling,
                state.Document.Toolhead.IsDirectDrive == true ? 3.0m : 10.0m);
            if (retraction > ceiling)
            {
                Add(
                    problems,
                    CalibrationGenerationProblemCodes.GcodeRetractionAboveLimit,
                    Field(lineNumber),
                    "A commanded retraction exceeds the safe range.");
            }
        }

        if (extrusion > 0m)
        {
            state.HasExtruded = true;
            if (!state.Homed)
            {
                Add(
                    problems,
                    CalibrationGenerationProblemCodes.GcodeUnsafeInitialization,
                    Field(lineNumber),
                    "Calibration G-code extrudes before the printer is homed.");
            }

            if (state.NozzleTemperature <= 0m)
            {
                Add(
                    problems,
                    CalibrationGenerationProblemCodes.GcodeUnsafeInitialization,
                    Field(lineNumber),
                    "Calibration G-code extrudes before a nozzle temperature is commanded.");
            }

            ValidateVolumetricFlow(state, previousX, previousY, extrusion, lineNumber, problems);
        }

        if (state.AbsolutePositioning && (hasX || hasY))
        {
            ValidatePosition(state, lineNumber, problems);
        }

        if (hasZ && state.Z > (state.Document.Bed.SizeZMillimeters ?? decimal.MaxValue))
        {
            Add(
                problems,
                CalibrationGenerationProblemCodes.GcodeMotionOutsideBuildVolume,
                Field(lineNumber),
                "A commanded Z height exceeds the authoritative build volume.");
        }
    }

    private static void ValidateVolumetricFlow(
        CalibrationInterpreterState state,
        decimal previousX,
        decimal previousY,
        decimal extrusion,
        int lineNumber,
        List<CalibrationGenerationProblem> problems)
    {
        decimal deltaX = state.X - previousX;
        decimal deltaY = state.Y - previousY;
        decimal distance = Math.Abs(deltaX) + Math.Abs(deltaY);
        if (distance <= 0m || state.FeedRate <= 0m)
        {
            return;
        }

        decimal radius = state.Document.Print.FilamentDiameterMillimeters / 2m;
        decimal filamentArea = 3.1415926535897932384626433833m * radius * radius;
        decimal volume = extrusion * filamentArea;
        decimal speed = state.FeedRate / 60m;
        decimal flow = volume / distance * speed;
        decimal ceiling = state.Document.Print.MaxVolumetricFlow + VolumetricFlowTolerance;
        if (flow > ceiling)
        {
            Add(
                problems,
                CalibrationGenerationProblemCodes.GcodeVolumetricFlowAboveLimit,
                Field(lineNumber),
                "A commanded move exceeds the authoritative volumetric flow ceiling.");
        }
    }

    private static void ValidatePosition(
        CalibrationInterpreterState state,
        int lineNumber,
        List<CalibrationGenerationProblem> problems)
    {
        CalibrationBedGeometry bed = state.Document.Bed;
        decimal originX = bed.OriginXMillimeters ?? 0m;
        decimal originY = bed.OriginYMillimeters ?? 0m;
        if (bed.SizeXMillimeters is { } sizeX && bed.SizeYMillimeters is { } sizeY &&
            (state.X < originX - CoordinateTolerance ||
                state.X > originX + sizeX + CoordinateTolerance ||
                state.Y < originY - CoordinateTolerance ||
                state.Y > originY + sizeY + CoordinateTolerance))
        {
            Add(
                problems,
                CalibrationGenerationProblemCodes.GcodeMotionOutsideBuildVolume,
                Field(lineNumber),
                "A commanded move falls outside the authoritative build volume.");
            return;
        }

        if (bed.PrintablePolygon.Count >= 3 &&
            !CalibrationGeometry.ContainsPoint(bed.PrintablePolygon, state.X, state.Y))
        {
            Add(
                problems,
                CalibrationGenerationProblemCodes.GcodeMotionOutsidePrintablePolygon,
                Field(lineNumber),
                "A commanded move falls outside the authoritative printable polygon.");
            return;
        }

        if (bed.ExcludedRegions.Any(region =>
                region.Polygon.Count >= 3 &&
                CalibrationGeometry.ContainsPoint(region.Polygon, state.X, state.Y)))
        {
            Add(
                problems,
                CalibrationGenerationProblemCodes.GcodeMotionInsideExcludedRegion,
                Field(lineNumber),
                "A commanded move enters an authoritative excluded region.");
        }
    }

    private static void TrackMarker(
        CalibrationInterpreterState state,
        string line,
        int lineNumber,
        List<CalibrationGenerationProblem> problems)
    {
        if (line.StartsWith(CalibrationGcodeMarkers.SegmentTransition, StringComparison.Ordinal))
        {
            state.SawTransition = true;
            state.TransitionRetracted = false;
            return;
        }

        if (line.StartsWith(CalibrationGcodeMarkers.SegmentBegin, StringComparison.Ordinal))
        {
            state.SegmentDepth++;
            if (state.SegmentsSeen > 0 && (!state.SawTransition || !state.TransitionRetracted))
            {
                Add(
                    problems,
                    CalibrationGenerationProblemCodes.GcodeUnsafeSegmentTransition,
                    Field(lineNumber),
                    "A calibration segment starts without a retracting safe transition.");
            }

            state.SegmentsSeen++;
            state.SawTransition = false;
            return;
        }

        if (line.StartsWith(CalibrationGcodeMarkers.SegmentEnd, StringComparison.Ordinal))
        {
            state.SegmentDepth--;
        }
    }

    private static void ValidateEnvelope(
        CalibrationInterpreterState state,
        List<CalibrationGenerationProblem> problems)
    {
        if (!state.Homed)
        {
            Add(
                problems,
                CalibrationGenerationProblemCodes.GcodeUnsafeInitialization,
                "gcode.initialization",
                "Calibration G-code never homes the printer.");
        }

        if (!state.HeatersOff || !state.MotorsOff)
        {
            Add(
                problems,
                CalibrationGenerationProblemCodes.GcodeMissingFinalReset,
                "gcode.finalization",
                "Calibration G-code does not end with a safe heater and motor reset.");
        }

        if (state.SegmentDepth != 0)
        {
            Add(
                problems,
                CalibrationGenerationProblemCodes.GcodeUnsafeSegmentTransition,
                "gcode.segments",
                "Calibration G-code contains an unbalanced segment marker.");
        }
    }

    private static void ScanRedaction(
        string line,
        int lineNumber,
        List<CalibrationGenerationProblem> problems)
    {
        if (HostCommandMarkers.Any(marker => line.Contains(marker, StringComparison.OrdinalIgnoreCase)))
        {
            Add(
                problems,
                CalibrationGenerationProblemCodes.GcodeContainsHostCommand,
                Field(lineNumber),
                "Calibration G-code contains a shell, host or network command.");
            return;
        }

        if (CredentialMarkers.Any(marker => line.Contains(marker, StringComparison.OrdinalIgnoreCase)))
        {
            Add(
                problems,
                CalibrationGenerationProblemCodes.GcodeContainsCredential,
                Field(lineNumber),
                "Calibration G-code contains a credential-bearing token.");
            return;
        }

        if (line.Contains("://", StringComparison.Ordinal))
        {
            Add(
                problems,
                CalibrationGenerationProblemCodes.GcodeContainsPrivateUrl,
                Field(lineNumber),
                "Calibration G-code contains a URL.");
            return;
        }

        if (ContainsAbsolutePath(line))
        {
            Add(
                problems,
                CalibrationGenerationProblemCodes.GcodeContainsFilesystemPath,
                Field(lineNumber),
                "Calibration G-code contains an absolute filesystem path.");
        }
    }

    private static bool ContainsAbsolutePath(string line)
    {
        foreach (string token in line.Split(
            [' ', '\t', '=', '"', '\''],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string candidate = token.TrimStart(';');
            if (candidate.StartsWith(@"\\", StringComparison.Ordinal) ||
                (candidate.Length >= 3 &&
                    char.IsAsciiLetter(candidate[0]) &&
                    candidate[1] == ':' &&
                    (candidate[2] == '\\' || candidate[2] == '/')) ||
                (candidate.Length > 1 && candidate[0] == '/' && char.IsAsciiLetter(candidate[1])))
            {
                return true;
            }
        }

        return false;
    }

    private static string ReadCommand(string line)
    {
        int end = line.IndexOfAny([' ', '\t', ';']);
        string token = end < 0 ? line : line[..end];
        return token.ToUpperInvariant();
    }

    private static bool TryReadParameter(string line, char parameter, out decimal value)
    {
        value = 0m;
        for (int index = 1; index < line.Length; index++)
        {
            if (char.ToUpperInvariant(line[index]) != parameter ||
                (line[index - 1] != ' ' && line[index - 1] != '\t'))
            {
                continue;
            }

            int start = index + 1;
            int end = start;
            while (end < line.Length &&
                (char.IsAsciiDigit(line[end]) || line[end] == '.' || line[end] == '-' ||
                    line[end] == '+'))
            {
                end++;
            }

            return end > start &&
                decimal.TryParse(
                    line.AsSpan(start, end - start),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out value);
        }

        return false;
    }

    private static bool TryReadNamedParameter(string line, string key, out decimal value)
    {
        value = 0m;
        int start = line.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return false;
        }

        start += key.Length;
        int end = start;
        while (end < line.Length &&
            (char.IsAsciiDigit(line[end]) || line[end] == '.' || line[end] == '-' ||
                line[end] == '+'))
        {
            end++;
        }

        return end > start &&
            decimal.TryParse(
                line.AsSpan(start, end - start),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out value);
    }

    private static string Field(int lineNumber) =>
        string.Create(CultureInfo.InvariantCulture, $"gcode.line[{lineNumber}]");

    private static void Add(
        List<CalibrationGenerationProblem> problems,
        string code,
        string field,
        string message)
    {
        // One reason per code keeps a rejected 64-segment program from producing thousands of rows.
        if (!problems.Any(problem => string.Equals(problem.Code, code, StringComparison.Ordinal)))
        {
            problems.Add(new(code, field, message));
        }
    }

    private sealed class CalibrationInterpreterState(CalibrationSpecificationDocument document)
    {
        public CalibrationSpecificationDocument Document { get; } = document;

        public bool AbsolutePositioning { get; set; } = true;

        public bool AbsoluteExtrusion { get; set; } = true;

        public bool Homed { get; set; }

        public bool HeatersOff { get; set; }

        public bool MotorsOff { get; set; }

        public bool HasExtruded { get; set; }

        public bool SawTransition { get; set; }

        public bool TransitionRetracted { get; set; }

        public int SegmentsSeen { get; set; }

        public decimal X { get; set; }

        public decimal Y { get; set; }

        public decimal Z { get; set; }

        public decimal E { get; set; }

        public decimal FeedRate { get; set; }

        public decimal NozzleTemperature { get; set; }

        public decimal BedTemperature { get; set; }

        public decimal PressureAdvance { get; set; }

        public int SegmentDepth { get; set; }

        public int CommandCount { get; set; }
    }
}
