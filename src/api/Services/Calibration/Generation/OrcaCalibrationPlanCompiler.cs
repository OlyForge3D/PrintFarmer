using System.Globalization;
using System.Text.Json;

namespace Farm.Web.Api.Services.Calibration.Generation;

/// <summary>A single allowlisted native upstream-Orca setting override.</summary>
/// <param name="Key">The native Orca setting key.</param>
/// <param name="Value">The override value, already formatted for the native profile.</param>
/// <param name="Unit">The explicit unit, or <c>none</c> for dimensionless settings.</param>
/// <param name="Source">Where the value came from, for example <c>specification.print</c>.</param>
public sealed record OrcaSettingOverride(string Key, string Value, string Unit, string Source);

/// <summary>The exact native profile document a plan carries, with its verified digest.</summary>
/// <param name="Id">Authoritative profile identity.</param>
/// <param name="Kind">Profile kind.</param>
/// <param name="Revision">Profile revision.</param>
/// <param name="ExactJson">The verbatim native JSON document.</param>
/// <param name="Sha256">The verified digest of <paramref name="ExactJson"/>.</param>
public sealed record OrcaPlanProfile(
    Guid Id,
    string Kind,
    string? Revision,
    string ExactJson,
    string Sha256);

/// <summary>The deterministic plan body a manifest digest is computed over.</summary>
public sealed record OrcaCalibrationPlanManifest
{
    /// <summary>Gets the manifest schema version.</summary>
    public required string SchemaVersion { get; init; }

    /// <summary>Gets the project identifier.</summary>
    public required Guid ProjectId { get; init; }

    /// <summary>Gets the attempt identifier.</summary>
    public required Guid AttemptId { get; init; }

    /// <summary>Gets the orchestration identifier.</summary>
    public required Guid OrchestrationId { get; init; }

    /// <summary>Gets the canonical method name.</summary>
    public required string Method { get; init; }

    /// <summary>Gets the specification digest this plan was compiled from.</summary>
    public required string SpecificationSha256 { get; init; }

    /// <summary>Gets the slicer engine.</summary>
    public required string SlicerEngine { get; init; }

    /// <summary>Gets the slicer distribution.</summary>
    public required string SlicerDistribution { get; init; }

    /// <summary>Gets the pinned slicer version.</summary>
    public required string SlicerVersion { get; init; }

    /// <summary>Gets the pinned slicer container digest.</summary>
    public required string SlicerContainerDigest { get; init; }

    /// <summary>Gets the pinned slicer binary digest.</summary>
    public required string SlicerBinarySha256 { get; init; }

    /// <summary>Gets the machine profile identity and digest.</summary>
    public required OrcaPlanProfileReference Machine { get; init; }

    /// <summary>Gets the process profile identity and digest.</summary>
    public required OrcaPlanProfileReference Process { get; init; }

    /// <summary>Gets the filament profile identity and digest.</summary>
    public required OrcaPlanProfileReference Filament { get; init; }

    /// <summary>Gets the model identity and digest the plan slices.</summary>
    public required OrcaPlanModelReference Model { get; init; }

    /// <summary>Gets the generator name.</summary>
    public required string GeneratorName { get; init; }

    /// <summary>Gets the generator version.</summary>
    public required string GeneratorVersion { get; init; }

    /// <summary>Gets the ordered allowlisted overrides.</summary>
    public required IReadOnlyList<OrcaSettingOverride> Overrides { get; init; }

    /// <summary>Gets the deterministic segment plan carried into annotation.</summary>
    public required IReadOnlyList<CalibrationSegmentSpecification> Segments { get; init; }
}

/// <summary>A profile identity and digest recorded in a plan manifest.</summary>
/// <param name="Id">Authoritative profile identity.</param>
/// <param name="Revision">Profile revision.</param>
/// <param name="Sha256">Verified profile digest.</param>
public sealed record OrcaPlanProfileReference(Guid Id, string? Revision, string Sha256);

/// <summary>A model identity and digest recorded in a plan manifest.</summary>
/// <param name="Model3DId">Stored model identity, or empty for trusted generated geometry.</param>
/// <param name="Sha256">Verified model content digest.</param>
/// <param name="Format">Canonical format token.</param>
/// <param name="Provenance">Preserved provenance token.</param>
public sealed record OrcaPlanModelReference(
    Guid Model3DId,
    string Sha256,
    string Format,
    string Provenance);

/// <summary>A compiled, deterministic upstream-Orca calibration plan.</summary>
/// <param name="Manifest">The canonical manifest body.</param>
/// <param name="ManifestJson">The canonical manifest JSON.</param>
/// <param name="ManifestSha256">The digest of <paramref name="ManifestJson"/>.</param>
/// <param name="MachineProfile">The exact native machine profile.</param>
/// <param name="ProcessProfile">The exact native process profile.</param>
/// <param name="FilamentProfile">The exact native filament profile.</param>
public sealed record OrcaCalibrationPlan(
    OrcaCalibrationPlanManifest Manifest,
    string ManifestJson,
    string ManifestSha256,
    OrcaPlanProfile MachineProfile,
    OrcaPlanProfile ProcessProfile,
    OrcaPlanProfile FilamentProfile);

/// <summary>
/// Compiles a deterministic upstream-Orca plan from a verified specification and validated model.
/// </summary>
public interface IOrcaCalibrationPlanCompiler
{
    /// <summary>
    /// Compiles the plan.
    /// </summary>
    /// <param name="specification">The compiled specification.</param>
    /// <param name="model">The validated calibration model.</param>
    /// <returns>The compiled plan, or the ordered rejection reasons.</returns>
    /// <remarks>
    /// The compiler consumes the exact native profile documents supplied by the authoritative resolver
    /// or snapshot and verifies each digest before use. It never invents a missing container or binary
    /// digest: an unavailable pinned identity is returned as an explicit dependency error.
    /// </remarks>
    CalibrationGenerationResult<OrcaCalibrationPlan> Compile(
        CalibrationSpecification specification,
        CalibrationValidatedModel model);
}

/// <summary>Default <see cref="IOrcaCalibrationPlanCompiler"/>.</summary>
public sealed class OrcaCalibrationPlanCompiler : IOrcaCalibrationPlanCompiler
{
    /// <summary>The plan manifest schema version emitted by this build.</summary>
    public const string ManifestSchemaVersion = "1.0";

    private const decimal NozzleDiameterTolerance = 0.001m;

    /// <summary>
    /// The complete allowlist of native upstream-Orca settings a calibration plan may override.
    /// </summary>
    /// <remarks>
    /// Anything outside this set is rejected rather than passed through, so a caller cannot reach an
    /// arbitrary command, script or post-processing field through the plan.
    /// </remarks>
    public static IReadOnlySet<string> AllowedOverrideKeys { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "bottom_shell_layers",
            "chamber_temperature",
            "default_acceleration",
            "enable_pressure_advance",
            "enable_support",
            "filament_flow_ratio",
            "filament_max_volumetric_speed",
            "hot_plate_temp",
            "hot_plate_temp_initial_layer",
            "initial_layer_line_width",
            "initial_layer_print_height",
            "initial_layer_speed",
            "inner_wall_speed",
            "layer_height",
            "line_width",
            "nozzle_temperature",
            "nozzle_temperature_initial_layer",
            "outer_wall_speed",
            "pressure_advance",
            "retraction_length",
            "retraction_speed",
            "skirt_loops",
            "sparse_infill_density",
            "top_shell_layers",
            "travel_speed",
            "wall_loops",
        };

    private static readonly string[] ForbiddenProfileKeys =
    [
        "post_process",
        "machine_start_gcode",
        "machine_end_gcode",
        "before_layer_change_gcode",
        "layer_change_gcode",
        "change_filament_gcode",
        "template_custom_gcode",
        "printer_notes",
    ];

    /// <inheritdoc/>
    public CalibrationGenerationResult<OrcaCalibrationPlan> Compile(
        CalibrationSpecification specification,
        CalibrationValidatedModel model)
    {
        ArgumentNullException.ThrowIfNull(specification);
        ArgumentNullException.ThrowIfNull(model);

        CalibrationSpecificationDocument document = specification.Document;
        List<CalibrationGenerationProblem> problems = [];

        CalibrationSupportedTupleValidator.Validate(document.Compatibility, problems);

        string recomputedSpecification =
            CalibrationCanonicalJson.ComputeTextSha256(specification.CanonicalJson);
        if (!CalibrationCanonicalJson.DigestsMatch(recomputedSpecification, specification.Sha256))
        {
            problems.Add(new(
                CalibrationGenerationProblemCodes.SpecificationHashMismatch,
                "specification.sha256",
                "The specification digest does not match its canonical JSON."));
        }

        OrcaPlanProfile? machine = VerifyProfile(document.Profiles.Machine, "machine", problems);
        OrcaPlanProfile? process = VerifyProfile(document.Profiles.Process, "process", problems);
        OrcaPlanProfile? filament = VerifyProfile(document.Profiles.Filament, "filament", problems);

        if (machine is not null)
        {
            VerifyNozzle(machine, document.Toolhead, problems);
        }

        VerifyModel(document, model, problems);

        if (problems.Count > 0 || machine is null || process is null || filament is null)
        {
            if (problems.Count == 0)
            {
                problems.Add(new(
                    CalibrationGenerationProblemCodes.PlanDependencyUnavailable,
                    "specification.profiles",
                    "A required exact native profile is unavailable."));
            }

            return CalibrationGenerationResults.Failure<OrcaCalibrationPlan>(problems);
        }

        IReadOnlyList<OrcaSettingOverride> overrides = BuildOverrides(document);
        foreach (OrcaSettingOverride setting in overrides)
        {
            if (!AllowedOverrideKeys.Contains(setting.Key))
            {
                problems.Add(new(
                    CalibrationGenerationProblemCodes.PlanSettingNotAllowlisted,
                    $"plan.overrides.{setting.Key}",
                    "The plan attempted to override a setting outside the allowlist."));
            }
        }

        if (problems.Count > 0)
        {
            return CalibrationGenerationResults.Failure<OrcaCalibrationPlan>(problems);
        }

        OrcaCalibrationPlanManifest manifest = new()
        {
            SchemaVersion = ManifestSchemaVersion,
            ProjectId = document.ProjectId,
            AttemptId = document.AttemptId,
            OrchestrationId = document.OrchestrationId,
            Method = document.Method,
            SpecificationSha256 = specification.Sha256,
            SlicerEngine = CalibrationSupportedTuple.SlicerEngine,
            SlicerDistribution = CalibrationSupportedTuple.SlicerDistribution,
            SlicerVersion = document.Compatibility.SlicerVersion!,
            SlicerContainerDigest = document.Compatibility.SlicerContainerDigest!.Trim(),
            SlicerBinarySha256 = document.Compatibility.SlicerBinarySha256!.Trim(),
            Machine = new OrcaPlanProfileReference(machine.Id, machine.Revision, machine.Sha256),
            Process = new OrcaPlanProfileReference(process.Id, process.Revision, process.Sha256),
            Filament = new OrcaPlanProfileReference(filament.Id, filament.Revision, filament.Sha256),
            Model = new OrcaPlanModelReference(
                model.Model3DId,
                model.Sha256,
                model.Format,
                model.Provenance),
            GeneratorName = document.Generator.Name,
            GeneratorVersion = document.Generator.Version,
            Overrides = overrides,
            Segments = document.Segments,
        };

        string manifestJson = CalibrationCanonicalJson.Serialize(manifest);
        string manifestSha256 = CalibrationCanonicalJson.ComputeTextSha256(manifestJson);
        return CalibrationGenerationResults.Success(new OrcaCalibrationPlan(
            manifest,
            manifestJson,
            manifestSha256,
            machine,
            process,
            filament));
    }

    private static void VerifyModel(
        CalibrationSpecificationDocument document,
        CalibrationValidatedModel model,
        List<CalibrationGenerationProblem> problems)
    {
        if (document.ImportedAsset is not { } asset)
        {
            return;
        }

        if (asset.Model3DId != model.Model3DId ||
            !CalibrationCanonicalJson.DigestsMatch(asset.Sha256, model.Sha256))
        {
            problems.Add(new(
                CalibrationGenerationProblemCodes.PlanModelMismatch,
                "plan.model",
                "The validated model does not match the linked asset in the specification."));
        }
    }

    private static OrcaPlanProfile? VerifyProfile(
        CalibrationExactProfile? profile,
        string kind,
        List<CalibrationGenerationProblem> problems)
    {
        string field = $"specification.profiles.{kind}";
        if (profile is null || string.IsNullOrWhiteSpace(profile.ExactJson))
        {
            problems.Add(new(
                CalibrationGenerationProblemCodes.PlanDependencyUnavailable,
                field,
                $"The exact native {kind} profile is unavailable."));
            return null;
        }

        string computed = CalibrationCanonicalJson.ComputeTextSha256(profile.ExactJson);
        if (!CalibrationCanonicalJson.DigestsMatch(computed, profile.Sha256))
        {
            problems.Add(new(
                CalibrationGenerationProblemCodes.ProfileHashMismatch,
                $"{field}.sha256",
                $"The {kind} profile digest does not match the exact profile JSON."));
            return null;
        }

        CalibrationProfileSafetyResult safety =
            CalibrationProfileSafetyValidator.Validate(profile.ExactJson, field);
        if (!safety.IsSafe)
        {
            problems.Add(new(
                safety.Code ?? CalibrationGenerationProblemCodes.PlanProfileUnsafeCommand,
                safety.Field ?? field,
                safety.Message ?? "The native profile contains unsafe content."));
            return null;
        }

        JsonElement root = safety.Json!.Value;
        if (root.ValueKind != JsonValueKind.Object)
        {
            problems.Add(new(
                CalibrationGenerationProblemCodes.PlanProfileJsonInvalid,
                field,
                $"The exact native {kind} profile is not a JSON object."));
            return null;
        }

        if (root.TryGetProperty("inherits", out JsonElement inherits) &&
            inherits.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(inherits.GetString()))
        {
            problems.Add(new(
                CalibrationGenerationProblemCodes.PlanProfileInheritanceUnsupported,
                $"{field}.inherits",
                "A calibration plan requires a fully resolved profile with no inheritance."));
            return null;
        }

        foreach (string forbidden in ForbiddenProfileKeys)
        {
            if (root.TryGetProperty(forbidden, out JsonElement value) && HasContent(value))
            {
                problems.Add(new(
                    CalibrationGenerationProblemCodes.PlanProfileUnsafeCommand,
                    $"{field}.{forbidden}",
                    "The native profile carries an arbitrary command field."));
                return null;
            }
        }

        return new OrcaPlanProfile(
            profile.Id,
            kind,
            profile.Revision,
            profile.ExactJson,
            profile.Sha256!.Trim().ToLowerInvariant());
    }

    private static bool HasContent(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => !string.IsNullOrWhiteSpace(value.GetString()),
        JsonValueKind.Array => value.GetArrayLength() > 0 &&
            value.EnumerateArray().Any(HasContent),
        JsonValueKind.Object => value.EnumerateObject().Any(),
        JsonValueKind.Null or JsonValueKind.Undefined => false,
        _ => true,
    };

    private static void VerifyNozzle(
        OrcaPlanProfile machine,
        CalibrationToolheadContext toolhead,
        List<CalibrationGenerationProblem> problems)
    {
        using JsonDocument document = JsonDocument.Parse(machine.ExactJson);
        if (!document.RootElement.TryGetProperty("nozzle_diameter", out JsonElement nozzle))
        {
            problems.Add(new(
                CalibrationGenerationProblemCodes.PlanNozzleMismatch,
                "specification.profiles.machine.nozzle_diameter",
                "The native machine profile does not declare a nozzle diameter."));
            return;
        }

        decimal? declared = ReadFirstDecimal(nozzle);
        if (declared is null)
        {
            problems.Add(new(
                CalibrationGenerationProblemCodes.PlanNozzleMismatch,
                "specification.profiles.machine.nozzle_diameter",
                "The native machine profile nozzle diameter is not a readable number."));
            return;
        }

        if (Math.Abs(declared.Value - toolhead.NozzleDiameterMillimeters) > NozzleDiameterTolerance)
        {
            problems.Add(new(
                CalibrationGenerationProblemCodes.PlanNozzleMismatch,
                "specification.profiles.machine.nozzle_diameter",
                "The native machine profile nozzle diameter does not match the authoritative toolhead."));
        }
    }

    private static decimal? ReadFirstDecimal(JsonElement element)
    {
        JsonElement candidate = element.ValueKind == JsonValueKind.Array
            ? element.GetArrayLength() > 0 ? element[0] : default
            : element;

        return candidate.ValueKind switch
        {
            JsonValueKind.Number => candidate.TryGetDecimal(out decimal number) ? number : null,
            JsonValueKind.String =>
                decimal.TryParse(
                    candidate.GetString(),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out decimal parsed)
                    ? parsed
                    : null,
            _ => null,
        };
    }

    private static OrcaSettingOverride[] BuildOverrides(
        CalibrationSpecificationDocument document)
    {
        CalibrationPrintParameters print = document.Print;
        List<OrcaSettingOverride> overrides =
        [
            Number("layer_height", print.LayerHeightMillimeters, CalibrationUnits.Millimeters),
            Number(
                "initial_layer_print_height",
                print.FirstLayerHeightMillimeters,
                CalibrationUnits.Millimeters),
            Number("line_width", print.LineWidthMillimeters, CalibrationUnits.Millimeters),
            Number(
                "initial_layer_line_width",
                print.LineWidthMillimeters,
                CalibrationUnits.Millimeters),
            Integer("nozzle_temperature", print.NozzleTemperatureCelsius, CalibrationUnits.Celsius),
            Integer(
                "nozzle_temperature_initial_layer",
                print.NozzleTemperatureCelsius,
                CalibrationUnits.Celsius),
            Integer("hot_plate_temp", print.BedTemperatureCelsius, CalibrationUnits.Celsius),
            Integer(
                "hot_plate_temp_initial_layer",
                print.BedTemperatureCelsius,
                CalibrationUnits.Celsius),
            Number("filament_flow_ratio", print.FlowRatio, CalibrationUnits.Ratio),
            Number(
                "filament_max_volumetric_speed",
                print.MaxVolumetricFlow,
                CalibrationUnits.CubicMillimetersPerSecond),
            Number("pressure_advance", print.PressureAdvance, CalibrationUnits.Seconds),
            Number(
                "retraction_length",
                print.RetractionLengthMillimeters,
                CalibrationUnits.Millimeters),
            Integer(
                "retraction_speed",
                print.RetractionSpeedMillimetersPerSecond,
                CalibrationUnits.MillimetersPerSecond),
            Integer(
                "outer_wall_speed",
                print.PrintSpeedMillimetersPerSecond,
                CalibrationUnits.MillimetersPerSecond),
            Integer(
                "inner_wall_speed",
                print.PrintSpeedMillimetersPerSecond,
                CalibrationUnits.MillimetersPerSecond),
            Integer(
                "initial_layer_speed",
                print.FirstLayerSpeedMillimetersPerSecond,
                CalibrationUnits.MillimetersPerSecond),
            Integer(
                "travel_speed",
                print.TravelSpeedMillimetersPerSecond,
                CalibrationUnits.MillimetersPerSecond),
            Integer(
                "default_acceleration",
                print.AccelerationMillimetersPerSecondSquared,
                "mm/s2"),
            Integer("wall_loops", 2, CalibrationUnits.Count),
            Integer("skirt_loops", 2, CalibrationUnits.Count),
            Text("enable_support", "0", CalibrationUnits.Count),
        ];

        if (print.ChamberTemperatureCelsius is { } chamber)
        {
            overrides.Add(Integer("chamber_temperature", chamber, CalibrationUnits.Celsius));
        }

        if (document.Method is CalibrationMethodNames.PressureAdvanceTower or
            CalibrationMethodNames.PressureAdvanceLine or
            CalibrationMethodNames.PressureAdvancePattern)
        {
            overrides.Add(Text("enable_pressure_advance", "1", CalibrationUnits.Count));
        }

        if (document.Method is CalibrationMethodNames.FlowRatioCoarse or
            CalibrationMethodNames.FlowRatioFine or
            CalibrationMethodNames.FlowRatioHighRange or
            CalibrationMethodNames.FlowVerification)
        {
            overrides.Add(Integer("sparse_infill_density", 0, CalibrationUnits.Count));
            overrides.Add(Integer("top_shell_layers", 0, CalibrationUnits.Count));
            overrides.Add(Integer("bottom_shell_layers", 1, CalibrationUnits.Count));
        }

        return overrides
            .OrderBy(setting => setting.Key, StringComparer.Ordinal)
            .ToArray();
    }

    private static OrcaSettingOverride Number(string key, decimal value, string unit) =>
        new(key, value.ToString("0.####", CultureInfo.InvariantCulture), unit, "specification.print");

    private static OrcaSettingOverride Integer(string key, int value, string unit) =>
        new(key, value.ToString(CultureInfo.InvariantCulture), unit, "specification.print");

    private static OrcaSettingOverride Text(string key, string value, string unit) =>
        new(key, value, unit, "specification.method");
}
