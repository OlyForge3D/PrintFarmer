namespace Farm.Web.Api.Services.Calibration.Generation;

/// <summary>
/// Compiles typed method input plus an authoritative context into a canonical, hashed specification.
/// </summary>
/// <remarks>
/// The compiler is fail closed. It rejects an unsupported method, an unsupported definition version,
/// an incomplete authoritative context, a compatibility tuple that is not an exact match, a stale or
/// mismatched snapshot or profile digest, and any resolved value outside a machine safe bound. It never
/// infers identity from a manufacturer, printer model, backend, alias or backend response, and it never
/// synthesizes a missing geometry, nozzle, limit, profile, timestamp or freshness value.
/// </remarks>
public interface ICalibrationSpecificationCompiler
{
    /// <summary>
    /// Compiles a canonical specification for one calibration attempt.
    /// </summary>
    /// <param name="context">The authoritative, immutable generation context.</param>
    /// <param name="options">The typed, versioned method options.</param>
    /// <returns>The compiled specification, or the ordered rejection reasons.</returns>
    /// <example>
    /// <code>
    /// CalibrationGenerationResult&lt;CalibrationSpecification&gt; compiled =
    ///     compiler.Compile(context, new TemperatureCalibrationOptions());
    /// if (!compiled.IsValid)
    /// {
    ///     return Unprocessable(compiled.Problems);
    /// }
    /// </code>
    /// </example>
    CalibrationGenerationResult<CalibrationSpecification> Compile(
        CalibrationGenerationContext context,
        CalibrationMethodOptions options);

    /// <summary>
    /// Verifies that an already compiled specification still matches the supplied context.
    /// </summary>
    /// <param name="context">The authoritative context observed now.</param>
    /// <param name="specification">The previously compiled specification.</param>
    /// <returns>An empty list when the specification is still current, otherwise the reasons.</returns>
    IReadOnlyList<CalibrationGenerationProblem> VerifyStillCurrent(
        CalibrationGenerationContext context,
        CalibrationSpecification specification);
}

/// <summary>Default <see cref="ICalibrationSpecificationCompiler"/>.</summary>
/// <param name="timeProvider">Clock used to evaluate snapshot freshness.</param>
public sealed class CalibrationSpecificationCompiler(TimeProvider timeProvider)
    : ICalibrationSpecificationCompiler
{
    /// <summary>The specification schema version emitted by this build.</summary>
    public const string SchemaVersion = "1.0";

    private const int MinimumSegments = 1;
    private const int MaximumSegments = 64;
    private const decimal MinimumNozzleTemperature = 150m;
    private const decimal MinimumBedTemperature = 0m;
    private const decimal AbsoluteFlowRatioFloor = 0.50m;
    private const decimal AbsoluteFlowRatioCeiling = 1.50m;
    private const decimal AbsolutePressureAdvanceCeiling = 2.0m;
    private const decimal AbsoluteRetractionCeiling = 10.0m;
    private const decimal FootprintMarginMillimeters = 5m;

    /// <summary>The smallest footprint edge, in millimetres, a calibration body may occupy.</summary>
    public const decimal MinimumFootprintMillimeters = 20m;

    /// <summary>Row spacing, in millimetres, between pressure advance pattern rows.</summary>
    public const decimal PatternRowSpacingMillimeters = 5m;

    /// <summary>Corner size, in millimetres, of one pressure advance pattern corner pair.</summary>
    public const decimal PatternCornerSizeMillimeters = 10m;

    /// <summary>Line spacing between pressure advance lines, expressed in extrusion widths.</summary>
    public const decimal PressureAdvanceLineSpacingFactor = 4m;

    private readonly TimeProvider _timeProvider = timeProvider ??
        throw new ArgumentNullException(nameof(timeProvider));

    /// <inheritdoc/>
    public CalibrationGenerationResult<CalibrationSpecification> Compile(
        CalibrationGenerationContext context,
        CalibrationMethodOptions options)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(options);

        List<CalibrationGenerationProblem> problems = [];
        ValidateDefinitionVersion(options, problems);
        ValidateIdentity(context, problems);
        CalibrationSupportedTupleValidator.Validate(context.Compatibility, problems);
        ValidateFirmware(context, problems);
        ValidateFreshness(context, problems);
        ValidateProfiles(context, problems);
        ValidateToolhead(context, problems);
        ValidateBed(context, problems);
        ValidateLinkedAsset(context, options, problems);

        if (problems.Count > 0)
        {
            return CalibrationGenerationResults.Failure<CalibrationSpecification>(problems);
        }

        CalibrationPrintParameters? print = ResolvePrintParameters(context, problems);
        if (print is null || problems.Count > 0)
        {
            return CalibrationGenerationResults.Failure<CalibrationSpecification>(problems);
        }

        CalibrationSweepPlan? sweep = CalibrationSweepResolver.Resolve(
            context,
            options,
            print,
            problems);
        if (sweep is null || problems.Count > 0)
        {
            return CalibrationGenerationResults.Failure<CalibrationSpecification>(problems);
        }

        CalibrationFootprint? footprint = ResolveFootprint(context, options, sweep, print, problems);
        if (footprint is null || problems.Count > 0)
        {
            return CalibrationGenerationResults.Failure<CalibrationSpecification>(problems);
        }

        ValidateSweepBounds(context, options, sweep, print, problems);
        if (problems.Count > 0)
        {
            return CalibrationGenerationResults.Failure<CalibrationSpecification>(problems);
        }

        CalibrationSpecificationDocument document = new()
        {
            SchemaVersion = SchemaVersion,
            ProjectId = context.ProjectId,
            AttemptId = context.AttemptId,
            OrchestrationId = context.OrchestrationId,
            PrinterId = context.PrinterId,
            PrinterConfigurationSnapshotId = context.PrinterConfigurationSnapshotId,
            PrinterConfigurationRevision = context.PrinterConfigurationRevision,
            PrinterConfigurationSnapshotSha256 =
                context.PrinterConfigurationSnapshotSha256!.Trim().ToLowerInvariant(),
            SnapshotCapturedAtUtc = DateTime.SpecifyKind(
                context.SnapshotCapturedAtUtc!.Value,
                DateTimeKind.Utc),
            CalibrationKind = CalibrationMethodNames.ToKind(options.Method),
            Method = CalibrationMethodNames.ToName(options.Method),
            DefinitionVersion = options.DefinitionVersion,
            Compatibility = context.Compatibility,
            Firmware = context.Firmware,
            Toolhead = context.Toolhead,
            Bed = context.Bed,
            Limits = context.Limits,
            Filament = context.Filament,
            Profiles = context.Profiles,
            Generator = context.Generator,
            Print = print,
            Footprint = footprint,
            Sweep = sweep.Sweep,
            Segments = sweep.Segments,
            OperationId = context.OperationId!.Trim(),
            ImportedAsset = context.ImportedAsset,
        };

        string canonicalJson = CalibrationCanonicalJson.Serialize(document);
        string sha256 = CalibrationCanonicalJson.ComputeTextSha256(canonicalJson);
        return CalibrationGenerationResults.Success(
            new CalibrationSpecification(document, canonicalJson, sha256));
    }

    /// <inheritdoc/>
    public IReadOnlyList<CalibrationGenerationProblem> VerifyStillCurrent(
        CalibrationGenerationContext context,
        CalibrationSpecification specification)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(specification);

        List<CalibrationGenerationProblem> problems = [];
        CalibrationSpecificationDocument document = specification.Document;

        string recomputed = CalibrationCanonicalJson.ComputeTextSha256(specification.CanonicalJson);
        if (!CalibrationCanonicalJson.DigestsMatch(recomputed, specification.Sha256))
        {
            problems.Add(new(
                CalibrationGenerationProblemCodes.SpecificationHashMismatch,
                "specification.sha256",
                "The specification digest does not match its canonical JSON."));
        }

        if (document.PrinterConfigurationRevision != context.CurrentPrinterConfigurationRevision)
        {
            problems.Add(new(
                CalibrationGenerationProblemCodes.PrinterConfigurationStale,
                "specification.printerConfigurationRevision",
                "The printer configuration changed after the specification was compiled."));
        }

        if (!CalibrationCanonicalJson.DigestsMatch(
            document.PrinterConfigurationSnapshotSha256,
            context.PrinterConfigurationSnapshotSha256))
        {
            problems.Add(new(
                CalibrationGenerationProblemCodes.SnapshotHashMismatch,
                "specification.printerConfigurationSnapshotSha256",
                "The printer configuration snapshot digest changed."));
        }

        CompareProfile(
            document.Profiles.Machine,
            context.Profiles.Machine,
            "machine",
            problems);
        CompareProfile(
            document.Profiles.Process,
            context.Profiles.Process,
            "process",
            problems);
        CompareProfile(
            document.Profiles.Filament,
            context.Profiles.Filament,
            "filament",
            problems);

        CalibrationSupportedTupleValidator.Validate(context.Compatibility, problems);
        ValidateFreshness(context, problems);
        return problems;
    }

    private static void CompareProfile(
        CalibrationExactProfile? expected,
        CalibrationExactProfile? actual,
        string kind,
        List<CalibrationGenerationProblem> problems)
    {
        if (expected is null || actual is null ||
            !CalibrationCanonicalJson.DigestsMatch(expected.Sha256, actual.Sha256) ||
            expected.Id != actual.Id)
        {
            problems.Add(new(
                CalibrationGenerationProblemCodes.ProfileHashMismatch,
                $"specification.profiles.{kind}",
                $"The exact {kind} profile changed after the specification was compiled."));
        }
    }

    private static void ValidateDefinitionVersion(
        CalibrationMethodOptions options,
        List<CalibrationGenerationProblem> problems)
    {
        if (!string.Equals(
            options.DefinitionVersion,
            CalibrationMethodOptions.CurrentDefinitionVersion,
            StringComparison.Ordinal))
        {
            problems.Add(new(
                CalibrationGenerationProblemCodes.MethodDefinitionVersionUnsupported,
                "options.definitionVersion",
                "The requested calibration definition version is not supported."));
        }
    }

    private static void ValidateIdentity(
        CalibrationGenerationContext context,
        List<CalibrationGenerationProblem> problems)
    {
        RequireId(context.ProjectId, "context.projectId", problems);
        RequireId(context.AttemptId, "context.attemptId", problems);
        RequireId(context.OrchestrationId, "context.orchestrationId", problems);
        RequireId(context.PrinterId, "context.printerId", problems);
        RequireId(
            context.PrinterConfigurationSnapshotId,
            "context.printerConfigurationSnapshotId",
            problems);

        if (string.IsNullOrWhiteSpace(context.PrinterConfigurationSnapshotSha256))
        {
            problems.Add(new(
                CalibrationGenerationProblemCodes.SnapshotHashMissing,
                "context.printerConfigurationSnapshotSha256",
                "The authoritative printer configuration snapshot digest is missing."));
        }

        if (string.IsNullOrWhiteSpace(context.OperationId))
        {
            problems.Add(new(
                CalibrationGenerationProblemCodes.OperationIdMissing,
                "context.operationId",
                "An idempotency operation identifier is required."));
        }

        if (string.IsNullOrWhiteSpace(context.Generator.Name) ||
            string.IsNullOrWhiteSpace(context.Generator.Version))
        {
            problems.Add(new(
                CalibrationGenerationProblemCodes.GeneratorIdentityMissing,
                "context.generator",
                "The generator identity is missing."));
        }
    }

    private static void RequireId(
        Guid value,
        string field,
        List<CalibrationGenerationProblem> problems)
    {
        if (value == Guid.Empty)
        {
            problems.Add(new(
                CalibrationGenerationProblemCodes.ContextIdentityMissing,
                field,
                "A required authoritative identifier is missing."));
        }
    }

    private static void ValidateFirmware(
        CalibrationGenerationContext context,
        List<CalibrationGenerationProblem> problems)
    {
        CalibrationFirmwareContext firmware = context.Firmware;
        if (!string.Equals(firmware.Family, CalibrationSupportedTuple.FirmwareFamily, StringComparison.Ordinal))
        {
            problems.Add(new(
                CalibrationGenerationProblemCodes.FirmwareFamilyUnsupported,
                "context.firmware.family",
                "Only the Klipper firmware family is supported."));
        }

        if (!string.Equals(firmware.GcodeDialect, CalibrationSupportedTuple.GcodeDialect, StringComparison.Ordinal))
        {
            problems.Add(new(
                CalibrationGenerationProblemCodes.GcodeDialectUnsupported,
                "context.firmware.gcodeDialect",
                "Only the Klipper G-code dialect is supported."));
        }

        if (!firmware.Verified)
        {
            problems.Add(new(
                CalibrationGenerationProblemCodes.FirmwareUnverified,
                "context.firmware.verified",
                "The firmware identity was not verified by an authoritative source."));
        }

        if (string.IsNullOrWhiteSpace(firmware.Version))
        {
            problems.Add(new(
                CalibrationGenerationProblemCodes.FirmwareVersionMissing,
                "context.firmware.version",
                "The authoritative firmware version is missing."));
        }

        if (string.IsNullOrWhiteSpace(firmware.DetectionSource) ||
            string.Equals(firmware.DetectionSource, "unknown", StringComparison.OrdinalIgnoreCase))
        {
            problems.Add(new(
                CalibrationGenerationProblemCodes.FirmwareDetectionSourceMissing,
                "context.firmware.detectionSource",
                "The authoritative firmware detection source is missing."));
        }
    }

    private void ValidateFreshness(
        CalibrationGenerationContext context,
        List<CalibrationGenerationProblem> problems)
    {
        if (context.SnapshotCapturedAtUtc is not { } capturedAtUtc)
        {
            problems.Add(new(
                CalibrationGenerationProblemCodes.SnapshotTimestampMissing,
                "context.snapshotCapturedAtUtc",
                "The authoritative snapshot capture timestamp is missing."));
            return;
        }

        if (context.PrinterConfigurationRevision != context.CurrentPrinterConfigurationRevision)
        {
            problems.Add(new(
                CalibrationGenerationProblemCodes.PrinterConfigurationStale,
                "context.printerConfigurationRevision",
                "The printer configuration snapshot is stale."));
        }

        DateTime nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        TimeSpan age = nowUtc - DateTime.SpecifyKind(capturedAtUtc, DateTimeKind.Utc);
        if (age > context.SnapshotFreshnessWindow || age < TimeSpan.FromSeconds(-60))
        {
            problems.Add(new(
                CalibrationGenerationProblemCodes.SnapshotStale,
                "context.snapshotCapturedAtUtc",
                "The authoritative printer configuration snapshot is outside its freshness window."));
        }
    }

    private static void ValidateProfiles(
        CalibrationGenerationContext context,
        List<CalibrationGenerationProblem> problems)
    {
        ValidateProfile(context.Profiles.Machine, "machine", problems);
        ValidateProfile(context.Profiles.Process, "process", problems);
        ValidateProfile(context.Profiles.Filament, "filament", problems);
    }

    private static void ValidateProfile(
        CalibrationExactProfile? profile,
        string kind,
        List<CalibrationGenerationProblem> problems)
    {
        string field = $"context.profiles.{kind}";
        if (profile is null || profile.Id == Guid.Empty)
        {
            problems.Add(new(
                CalibrationGenerationProblemCodes.ProfileMissing,
                field,
                $"The exact {kind} profile is missing."));
            return;
        }

        if (string.IsNullOrWhiteSpace(profile.ExactJson))
        {
            problems.Add(new(
                CalibrationGenerationProblemCodes.ProfileJsonMissing,
                $"{field}.exactJson",
                $"The exact {kind} profile JSON is missing."));
            return;
        }

        if (string.IsNullOrWhiteSpace(profile.Sha256))
        {
            problems.Add(new(
                CalibrationGenerationProblemCodes.ProfileHashMissing,
                $"{field}.sha256",
                $"The authoritative {kind} profile digest is missing."));
            return;
        }

        string computed = CalibrationCanonicalJson.ComputeTextSha256(profile.ExactJson);
        if (!CalibrationCanonicalJson.DigestsMatch(computed, profile.Sha256))
        {
            problems.Add(new(
                CalibrationGenerationProblemCodes.ProfileHashMismatch,
                $"{field}.sha256",
                $"The {kind} profile digest does not match the exact profile JSON."));
        }
    }

    private static void ValidateToolhead(
        CalibrationGenerationContext context,
        List<CalibrationGenerationProblem> problems)
    {
        CalibrationToolheadContext toolhead = context.Toolhead;
        if (toolhead.Id == Guid.Empty || toolhead.Index < 0)
        {
            problems.Add(new(
                CalibrationGenerationProblemCodes.ToolheadMissing,
                "context.toolhead.id",
                "The authoritative toolhead identity is missing."));
        }

        if (toolhead.NozzleDiameterMillimeters <= 0m)
        {
            problems.Add(new(
                CalibrationGenerationProblemCodes.NozzleDiameterMissing,
                "context.toolhead.nozzleDiameterMillimeters",
                "The authoritative nozzle diameter is missing."));
        }

        if (toolhead.NozzleMaxTemperatureCelsius is null &&
            toolhead.HotendMaxTemperatureCelsius is null)
        {
            problems.Add(new(
                CalibrationGenerationProblemCodes.NozzleLimitMissing,
                "context.toolhead.nozzleMaxTemperatureCelsius",
                "The authoritative nozzle or hotend temperature ceiling is missing."));
        }

        if (toolhead.MaxVolumetricFlow is null && context.Filament.MaxVolumetricFlow is null)
        {
            problems.Add(new(
                CalibrationGenerationProblemCodes.VolumetricFlowLimitMissing,
                "context.toolhead.maxVolumetricFlow",
                "The authoritative volumetric flow ceiling is missing."));
        }
    }

    private static void ValidateBed(
        CalibrationGenerationContext context,
        List<CalibrationGenerationProblem> problems)
    {
        CalibrationBedGeometry bed = context.Bed;
        if (bed.SizeXMillimeters is not > 0m ||
            bed.SizeYMillimeters is not > 0m ||
            bed.SizeZMillimeters is not > 0m)
        {
            problems.Add(new(
                CalibrationGenerationProblemCodes.BuildVolumeMissing,
                "context.bed.size",
                "The authoritative build volume is missing."));
        }

        if (bed.PrintablePolygon.Count is > 0 and < 3)
        {
            problems.Add(new(
                CalibrationGenerationProblemCodes.PrintablePolygonInvalid,
                "context.bed.printablePolygon",
                "The authoritative printable polygon has fewer than three vertices."));
        }

        foreach (CalibrationExcludedRegion region in bed.ExcludedRegions)
        {
            if (region.Polygon.Count < 3)
            {
                problems.Add(new(
                    CalibrationGenerationProblemCodes.ExcludedRegionInvalid,
                    "context.bed.excludedRegions",
                    "An authoritative excluded region has fewer than three vertices."));
                break;
            }
        }

        if (context.Limits.MaxBedTemperatureCelsius is null)
        {
            problems.Add(new(
                CalibrationGenerationProblemCodes.BedLimitMissing,
                "context.limits.maxBedTemperatureCelsius",
                "The authoritative bed temperature ceiling is missing."));
        }
    }

    private static void ValidateLinkedAsset(
        CalibrationGenerationContext context,
        CalibrationMethodOptions options,
        List<CalibrationGenerationProblem> problems)
    {
        if (options is not FinalVerificationCalibrationOptions verification)
        {
            return;
        }

        CalibrationModelReference? asset = context.ImportedAsset;
        if (asset is null || asset.Model3DId == Guid.Empty || string.IsNullOrWhiteSpace(asset.Sha256))
        {
            problems.Add(new(
                CalibrationGenerationProblemCodes.LinkedAssetMissing,
                "context.importedAsset",
                "Final verification requires an authoritative linked model asset."));
            return;
        }

        if (verification.Model3DId != asset.Model3DId)
        {
            problems.Add(new(
                CalibrationGenerationProblemCodes.LinkedAssetMismatch,
                "options.model3DId",
                "The requested model identity does not match the authoritative linked asset."));
        }

        if (!string.IsNullOrWhiteSpace(verification.ExpectedSha256) &&
            !CalibrationCanonicalJson.DigestsMatch(verification.ExpectedSha256, asset.Sha256))
        {
            problems.Add(new(
                CalibrationGenerationProblemCodes.LinkedAssetMismatch,
                "options.expectedSha256",
                "The requested model digest does not match the authoritative linked asset."));
        }
    }

    private static CalibrationPrintParameters? ResolvePrintParameters(
        CalibrationGenerationContext context,
        List<CalibrationGenerationProblem> problems)
    {
        decimal nozzle = context.Toolhead.NozzleDiameterMillimeters;
        decimal layerHeight = context.Process.LayerHeightMillimeters ?? decimal.Round(nozzle / 2m, 3);
        decimal firstLayerHeight = context.Process.FirstLayerHeightMillimeters ?? layerHeight;
        decimal lineWidth = context.Process.LineWidthMillimeters ?? decimal.Round(nozzle * 1.125m, 3);
        decimal filamentDiameter = context.Filament.DiameterMillimeters ?? 1.75m;

        if (layerHeight <= 0m || layerHeight > nozzle)
        {
            problems.Add(new(
                CalibrationGenerationProblemCodes.LayerHeightOutOfRange,
                "context.process.layerHeightMillimeters",
                "The resolved layer height is outside the nozzle-derived safe range."));
        }

        if (lineWidth < nozzle || lineWidth > nozzle * 2m)
        {
            problems.Add(new(
                CalibrationGenerationProblemCodes.LineWidthOutOfRange,
                "context.process.lineWidthMillimeters",
                "The resolved extrusion width is outside the nozzle-derived safe range."));
        }

        if (filamentDiameter is <= 0m or > 5m)
        {
            problems.Add(new(
                CalibrationGenerationProblemCodes.FilamentDiameterOutOfRange,
                "context.filament.diameterMillimeters",
                "The resolved filament diameter is outside the safe range."));
        }

        int nozzleTemperature = context.Filament.NozzleTemperatureCelsius ?? 0;
        int bedTemperature = context.Filament.BedTemperatureCelsius ?? 0;
        if (nozzleTemperature <= 0)
        {
            problems.Add(new(
                CalibrationGenerationProblemCodes.NozzleTemperatureMissing,
                "context.filament.nozzleTemperatureCelsius",
                "The authoritative baseline nozzle temperature is missing."));
        }

        int maxPrintSpeed = context.Limits.MaxPrintSpeedMillimetersPerSecond ?? 0;
        int maxTravelSpeed = context.Limits.MaxTravelSpeedMillimetersPerSecond ?? 0;
        int maxAcceleration = context.Limits.MaxAcceleration ?? 0;
        if (maxPrintSpeed <= 0 || maxTravelSpeed <= 0 || maxAcceleration <= 0)
        {
            problems.Add(new(
                CalibrationGenerationProblemCodes.MotionLimitMissing,
                "context.limits",
                "The authoritative motion limits are missing."));
        }

        if (problems.Count > 0)
        {
            return null;
        }

        int printSpeed = Math.Min(context.Process.PrintSpeedMillimetersPerSecond ?? 60, maxPrintSpeed);
        int firstLayerSpeed = Math.Min(
            context.Process.FirstLayerSpeedMillimetersPerSecond ?? Math.Max(10, printSpeed / 2),
            maxPrintSpeed);
        int travelSpeed = Math.Min(
            context.Process.TravelSpeedMillimetersPerSecond ?? maxTravelSpeed,
            maxTravelSpeed);
        int acceleration = Math.Min(
            context.Process.AccelerationMillimetersPerSecondSquared ?? maxAcceleration,
            maxAcceleration);

        decimal maxVolumetricFlow =
            context.Toolhead.MaxVolumetricFlow ?? context.Filament.MaxVolumetricFlow ?? 0m;
        if (context.Toolhead.MaxVolumetricFlow is { } toolheadFlow &&
            context.Filament.MaxVolumetricFlow is { } filamentFlow)
        {
            maxVolumetricFlow = Math.Min(toolheadFlow, filamentFlow);
        }

        // Clamp the printed feed rate so a wide nozzle can never be driven past the authoritative
        // volumetric flow ceiling. Without this the emitted program would be rejected later by the
        // static safety validator instead of being resolved safely here.
        decimal crossSection = decimal.Round(lineWidth * layerHeight, 6);
        if (crossSection > 0m && maxVolumetricFlow > 0m)
        {
            int volumetricCap = Math.Max(1, (int)Math.Floor(maxVolumetricFlow / crossSection));
            printSpeed = Math.Min(printSpeed, volumetricCap);
            firstLayerSpeed = Math.Min(firstLayerSpeed, volumetricCap);
        }

        return new CalibrationPrintParameters(
            decimal.Round(layerHeight, 3),
            decimal.Round(firstLayerHeight, 3),
            decimal.Round(lineWidth, 3),
            decimal.Round(filamentDiameter, 3),
            printSpeed,
            firstLayerSpeed,
            travelSpeed,
            acceleration,
            nozzleTemperature,
            bedTemperature,
            context.Filament.ChamberTemperatureCelsius,
            decimal.Round(context.Filament.FlowRatio ?? 1.0m, 4),
            decimal.Round(context.Process.PressureAdvance ?? 0m, 4),
            decimal.Round(context.Process.RetractionLengthMillimeters ?? 0.8m, 3),
            context.Process.RetractionSpeedMillimetersPerSecond ?? 35,
            decimal.Round(maxVolumetricFlow, 3));
    }

    private static CalibrationFootprint? ResolveFootprint(
        CalibrationGenerationContext context,
        CalibrationMethodOptions options,
        CalibrationSweepPlan sweep,
        CalibrationPrintParameters print,
        List<CalibrationGenerationProblem> problems)
    {
        CalibrationBedGeometry bed = context.Bed;
        if (bed.SizeXMillimeters is not { } sizeX || bed.SizeYMillimeters is not { } sizeY)
        {
            return null;
        }

        decimal originX = bed.OriginXMillimeters ?? 0m;
        decimal originY = bed.OriginYMillimeters ?? 0m;
        decimal centerX = decimal.Round(originX + (sizeX / 2m), 3);
        decimal centerY = decimal.Round(originY + (sizeY / 2m), 3);
        decimal availableX = sizeX - (2m * FootprintMarginMillimeters);
        decimal availableY = sizeY - (2m * FootprintMarginMillimeters);

        (decimal requiredX, decimal requiredY) =
            RequiredFootprint(context, options, sweep, print, availableX);
        if (requiredX < MinimumFootprintMillimeters ||
            requiredY < MinimumFootprintMillimeters ||
            requiredX > availableX ||
            requiredY > availableY)
        {
            problems.Add(new(
                CalibrationGenerationProblemCodes.BuildVolumeTooSmall,
                "context.bed.size",
                "The authoritative build volume cannot hold the calibration footprint."));
            return null;
        }

        CalibrationFootprint footprint = new(
            centerX,
            centerY,
            decimal.Round(requiredX, 3),
            decimal.Round(requiredY, 3));
        IReadOnlyList<CalibrationBedPoint> polygon = bed.PrintablePolygon.Count >= 3
            ? bed.PrintablePolygon
            : CalibrationGeometry.BuildVolumeRectangle(bed);

        if (!CalibrationGeometry.ContainsRectangle(
            polygon,
            footprint.MinX,
            footprint.MinY,
            footprint.MaxX,
            footprint.MaxY))
        {
            problems.Add(new(
                CalibrationGenerationProblemCodes.FootprintOutsidePrintablePolygon,
                "context.bed.printablePolygon",
                "The deterministic calibration footprint falls outside the printable polygon."));
            return null;
        }

        foreach (CalibrationExcludedRegion region in bed.ExcludedRegions)
        {
            if (region.Polygon.Count >= 3 &&
                CalibrationGeometry.IntersectsRectangle(
                    region.Polygon,
                    footprint.MinX,
                    footprint.MinY,
                    footprint.MaxX,
                    footprint.MaxY))
            {
                problems.Add(new(
                    CalibrationGenerationProblemCodes.FootprintInsideExcludedRegion,
                    "context.bed.excludedRegions",
                    "The deterministic calibration footprint overlaps an excluded region."));
                return null;
            }
        }

        return footprint;
    }

    private static (decimal RequiredX, decimal RequiredY) RequiredFootprint(
        CalibrationGenerationContext context,
        CalibrationMethodOptions options,
        CalibrationSweepPlan sweep,
        CalibrationPrintParameters print,
        decimal availableX)
    {
        decimal lineWidth = print.LineWidthMillimeters;
        decimal tower = Math.Max(
            MinimumFootprintMillimeters,
            decimal.Round(context.Toolhead.NozzleDiameterMillimeters * 75m, 3));

        switch (options)
        {
            case ShrinkageCalibrationOptions:
                decimal nominal = sweep.Sweep.Start + (4m * lineWidth);
                return (nominal, nominal);
            case PressureAdvanceLineCalibrationOptions line:
                decimal defaultLength = Math.Min(
                    decimal.Round(context.Toolhead.NozzleDiameterMillimeters * 150m, 3),
                    availableX - (4m * lineWidth));
                decimal length = line.LineLengthMillimeters ?? defaultLength;
                return (
                    length + (4m * lineWidth),
                    Math.Max(
                        MinimumFootprintMillimeters,
                        sweep.Segments.Count * PressureAdvanceLineSpacingFactor * lineWidth));
            case PressureAdvancePatternCalibrationOptions pattern:
                int corners = pattern.CornersPerRow ?? 3;
                decimal patternWidth = (corners * PatternCornerSizeMillimeters) +
                    (4m * lineWidth);
                decimal patternHeight =
                    ((sweep.Segments.Count + 1) * PatternRowSpacingMillimeters) +
                    (PatternCornerSizeMillimeters / 2m);
                return (
                    Math.Max(MinimumFootprintMillimeters, patternWidth),
                    Math.Max(MinimumFootprintMillimeters, patternHeight));
            default:
                return (tower, tower);
        }
    }

    private static void ValidateSweepBounds(
        CalibrationGenerationContext context,
        CalibrationMethodOptions options,
        CalibrationSweepPlan sweep,
        CalibrationPrintParameters print,
        List<CalibrationGenerationProblem> problems)
    {
        if (sweep.Segments.Count is < MinimumSegments or > MaximumSegments)
        {
            problems.Add(new(
                CalibrationGenerationProblemCodes.SegmentCountOutOfRange,
                "options",
                "The requested sweep resolves to an unsupported number of segments."));
            return;
        }

        int nozzleCeiling = context.Toolhead.NozzleMaxTemperatureCelsius ??
            context.Toolhead.HotendMaxTemperatureCelsius ??
            0;
        int bedCeiling = context.Limits.MaxBedTemperatureCelsius ?? 0;
        decimal pressureAdvanceCeiling = Math.Min(
            AbsolutePressureAdvanceCeiling,
            context.Toolhead.IsDirectDrive == true ? 0.5m : 2.0m);
        decimal retractionCeiling = Math.Min(
            AbsoluteRetractionCeiling,
            context.Toolhead.IsDirectDrive == true ? 3.0m : 10.0m);

        foreach (CalibrationSegmentSpecification segment in sweep.Segments)
        {
            switch (options.Method)
            {
                case CalibrationMethod.Temperature:
                    if (segment.Value > nozzleCeiling)
                    {
                        Add(
                            problems,
                            CalibrationGenerationProblemCodes.TemperatureAboveNozzleLimit,
                            "options.endCelsius",
                            "A requested tower temperature exceeds the authoritative nozzle ceiling.");
                    }
                    else if (segment.Value < MinimumNozzleTemperature)
                    {
                        Add(
                            problems,
                            CalibrationGenerationProblemCodes.TemperatureBelowSafeMinimum,
                            "options.startCelsius",
                            "A requested tower temperature is below the safe extrusion minimum.");
                    }

                    break;
                case CalibrationMethod.FlowRatioCoarse:
                case CalibrationMethod.FlowRatioFine:
                case CalibrationMethod.FlowRatioHighRange:
                case CalibrationMethod.FlowVerification:
                    if (segment.Value is < AbsoluteFlowRatioFloor or > AbsoluteFlowRatioCeiling)
                    {
                        Add(
                            problems,
                            CalibrationGenerationProblemCodes.FlowRatioOutOfRange,
                            "options.startRatio",
                            "A requested flow ratio is outside the safe range.");
                    }

                    break;
                case CalibrationMethod.PressureAdvanceTower:
                case CalibrationMethod.PressureAdvanceLine:
                case CalibrationMethod.PressureAdvancePattern:
                    if (segment.Value < 0m || segment.Value > pressureAdvanceCeiling)
                    {
                        Add(
                            problems,
                            CalibrationGenerationProblemCodes.PressureAdvanceOutOfRange,
                            "options.endPressureAdvance",
                            "A requested pressure advance value is outside the safe range.");
                    }

                    break;
                case CalibrationMethod.Retraction:
                    if (segment.Value < 0m || segment.Value > retractionCeiling)
                    {
                        Add(
                            problems,
                            CalibrationGenerationProblemCodes.RetractionOutOfRange,
                            "options.endLengthMillimeters",
                            "A requested retraction length is outside the safe range.");
                    }

                    break;
                case CalibrationMethod.MaximumVolumetricSpeed:
                    if (segment.Value <= 0m || segment.Value > print.MaxVolumetricFlow)
                    {
                        Add(
                            problems,
                            CalibrationGenerationProblemCodes.VolumetricFlowOutOfRange,
                            "options.endCubicMillimetersPerSecond",
                            "A requested volumetric speed exceeds the authoritative flow ceiling.");
                    }

                    break;
                case CalibrationMethod.Shrinkage:
                case CalibrationMethod.FinalVerification:
                default:
                    break;
            }
        }

        if (print.BedTemperatureCelsius > bedCeiling)
        {
            Add(
                problems,
                CalibrationGenerationProblemCodes.BedTemperatureAboveLimit,
                "context.filament.bedTemperatureCelsius",
                "The baseline bed temperature exceeds the authoritative bed ceiling.");
        }

        if (print.BedTemperatureCelsius < MinimumBedTemperature)
        {
            Add(
                problems,
                CalibrationGenerationProblemCodes.BedTemperatureAboveLimit,
                "context.filament.bedTemperatureCelsius",
                "The baseline bed temperature is negative.");
        }

        if (print.ChamberTemperatureCelsius is { } chamber)
        {
            if (context.Limits.HasHeatedChamber != true)
            {
                Add(
                    problems,
                    CalibrationGenerationProblemCodes.ChamberTemperatureUnsupported,
                    "context.filament.chamberTemperatureCelsius",
                    "A chamber temperature was requested but the printer has no heated chamber.");
            }
            else if (chamber > (context.Limits.MaxChamberTemperatureCelsius ?? 0))
            {
                Add(
                    problems,
                    CalibrationGenerationProblemCodes.ChamberTemperatureAboveLimit,
                    "context.filament.chamberTemperatureCelsius",
                    "The baseline chamber temperature exceeds the authoritative chamber ceiling.");
            }
        }
    }

    private static void Add(
        List<CalibrationGenerationProblem> problems,
        string code,
        string field,
        string message)
    {
        if (!problems.Any(problem =>
            string.Equals(problem.Code, code, StringComparison.Ordinal) &&
            string.Equals(problem.Field, field, StringComparison.Ordinal)))
        {
            problems.Add(new(code, field, message));
        }
    }
}
