using System.Globalization;
using System.Text.Json;
using Farm.Infrastructure.PrinterCalibration;
using Farm.Web.Api.Contracts;

namespace Farm.Web.Api.Services.Calibration.Generation;

/// <summary>The tunable a calibration observation resolves to.</summary>
public enum CalibrationPatchParameter
{
    /// <summary>Unknown. Never a valid value.</summary>
    Unspecified = 0,

    /// <summary>Nozzle temperature, in degrees Celsius.</summary>
    NozzleTemperature = 1,

    /// <summary>Filament flow ratio, dimensionless.</summary>
    FlowRatio = 2,

    /// <summary>Pressure advance, in seconds.</summary>
    PressureAdvance = 3,

    /// <summary>Retraction length, in millimetres.</summary>
    RetractionLength = 4,

    /// <summary>Maximum volumetric speed, in mm³/s.</summary>
    MaximumVolumetricSpeed = 5,
}

/// <summary>
/// The selected observation a patch is exported from, expressed as a typed value.
/// </summary>
/// <param name="ObservationId">The authoritative observation identity.</param>
/// <param name="Parameter">The tunable the observation selected.</param>
/// <param name="Value">The selected value.</param>
/// <param name="Unit">The explicit unit of <paramref name="Value"/>.</param>
/// <remarks>
/// The selection carries no free-form settings map, so an operator cannot smuggle an arbitrary native
/// key into an exported profile through an observation.
/// </remarks>
public sealed record CalibrationObservationSelection(
    Guid ObservationId,
    CalibrationPatchParameter Parameter,
    decimal Value,
    string Unit);

/// <summary>One normalized typed settings change produced by an export.</summary>
/// <param name="Parameter">The tunable that changed.</param>
/// <param name="NativeKey">The native upstream-Orca key the change maps to.</param>
/// <param name="Unit">The explicit unit.</param>
/// <param name="BaselineValue">The baseline value read from the exact source profile.</param>
/// <param name="Value">The exported value.</param>
public sealed record CalibrationProfilePatchEntry(
    string Parameter,
    string NativeKey,
    string Unit,
    string? BaselineValue,
    string Value);

/// <summary>The canonical normalized patch body.</summary>
public sealed record CalibrationProfilePatch
{
    /// <summary>Gets the patch schema version.</summary>
    public required string SchemaVersion { get; init; }

    /// <summary>Gets the owning project identifier.</summary>
    public required Guid ProjectId { get; init; }

    /// <summary>Gets the source attempt identifier.</summary>
    public required Guid SourceAttemptId { get; init; }

    /// <summary>Gets the source observation identifier.</summary>
    public required Guid SourceObservationId { get; init; }

    /// <summary>Gets the profile kind the patch applies to.</summary>
    public required string ProfileType { get; init; }

    /// <summary>Gets the baseline machine profile digest.</summary>
    public required string BaselineMachineProfileSha256 { get; init; }

    /// <summary>Gets the baseline process profile digest.</summary>
    public required string BaselineProcessProfileSha256 { get; init; }

    /// <summary>Gets the baseline filament profile digest.</summary>
    public required string BaselineFilamentProfileSha256 { get; init; }

    /// <summary>Gets the generator name.</summary>
    public required string GeneratorName { get; init; }

    /// <summary>Gets the generator version.</summary>
    public required string GeneratorVersion { get; init; }

    /// <summary>Gets the slicer engine.</summary>
    public required string SlicerEngine { get; init; }

    /// <summary>Gets the slicer distribution.</summary>
    public required string SlicerDistribution { get; init; }

    /// <summary>Gets the pinned slicer version.</summary>
    public required string SlicerVersion { get; init; }

    /// <summary>Gets the pinned slicer container digest.</summary>
    public required string SlicerContainerDigest { get; init; }

    /// <summary>Gets the ordered normalized entries.</summary>
    public required IReadOnlyList<CalibrationProfilePatchEntry> Entries { get; init; }
}

/// <summary>An exported patch and the immutable revision it was persisted as.</summary>
/// <param name="Patch">The canonical normalized patch body.</param>
/// <param name="PatchJson">The canonical patch JSON.</param>
/// <param name="PatchSha256">The digest of <paramref name="PatchJson"/>.</param>
/// <param name="ExactProfileJson">The exact upstream-Orca JSON artifact.</param>
/// <param name="ExactProfileSha256">The digest of <paramref name="ExactProfileJson"/>.</param>
/// <param name="Revision">The persisted immutable generated profile revision.</param>
public sealed record CalibrationProfilePatchExport(
    CalibrationProfilePatch Patch,
    string PatchJson,
    string PatchSha256,
    string ExactProfileJson,
    string ExactProfileSha256,
    GeneratedProfileRevisionDto Revision);

/// <summary>
/// Converts a selected observation into a typed normalized patch plus an exact upstream-Orca artifact.
/// </summary>
public interface ICalibrationProfilePatchExporter
{
    /// <summary>
    /// Exports the patch and persists it through the authoritative generated profile history.
    /// </summary>
    /// <param name="specification">The compiled specification of the source attempt.</param>
    /// <param name="selection">The typed observation selection.</param>
    /// <param name="profileName">The operator-visible name for the exported revision.</param>
    /// <param name="operationId">The idempotency operation identifier.</param>
    /// <param name="actor">The calling actor.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The export, or the ordered rejection reasons.</returns>
    /// <remarks>
    /// The exporter never mutates a baseline or published profile. It writes a new immutable
    /// <c>GeneratedProfileRevision</c> through the authoritative calibration project service, which
    /// keeps the append-only history and idempotent replay behaviour of that service intact.
    /// </remarks>
    Task<CalibrationGenerationResult<CalibrationProfilePatchExport>> ExportAsync(
        CalibrationSpecification specification,
        CalibrationObservationSelection selection,
        string profileName,
        string operationId,
        CalibrationActor actor,
        CancellationToken cancellationToken);
}

/// <summary>Default <see cref="ICalibrationProfilePatchExporter"/>.</summary>
/// <param name="projectService">The authoritative calibration project service.</param>
/// <param name="compatibilityPolicy">Configured upstream OrcaSlicer allow-list.</param>
public sealed class CalibrationProfilePatchExporter(
    ICalibrationProjectService projectService,
    CalibrationSlicerCompatibilityPolicy? compatibilityPolicy = null)
    : ICalibrationProfilePatchExporter
{
    private readonly CalibrationSlicerCompatibilityPolicy _compatibilityPolicy =
        compatibilityPolicy ?? CalibrationSlicerCompatibilityPolicy.Default;

    /// <summary>The patch schema version emitted by this build.</summary>
    public const string PatchSchemaVersion = "1.0";

    private readonly ICalibrationProjectService _projectService = projectService ??
        throw new ArgumentNullException(nameof(projectService));

    /// <inheritdoc/>
    public async Task<CalibrationGenerationResult<CalibrationProfilePatchExport>> ExportAsync(
        CalibrationSpecification specification,
        CalibrationObservationSelection selection,
        string profileName,
        string operationId,
        CalibrationActor actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(specification);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(actor);
        if (string.IsNullOrWhiteSpace(profileName))
        {
            throw new ArgumentException(
                "An exported profile revision requires a name.",
                nameof(profileName));
        }

        if (string.IsNullOrWhiteSpace(operationId))
        {
            throw new ArgumentException(
                "An exported profile revision requires an operation identifier.",
                nameof(operationId));
        }

        CalibrationSpecificationDocument document = specification.Document;
        List<CalibrationGenerationProblem> problems = [];
        CalibrationSupportedTupleValidator.Validate(
            document.Compatibility,
            problems,
            _compatibilityPolicy);

        (string profileType, string nativeKey) = MapParameter(selection.Parameter, problems);
        ValidateRange(selection, document, problems);

        CalibrationExactProfile? baseline = profileType switch
        {
            "filament" => document.Profiles.Filament,
            "process" => document.Profiles.Process,
            "machine" => document.Profiles.Machine,
            _ => null,
        };

        if (baseline is null || string.IsNullOrWhiteSpace(baseline.ExactJson))
        {
            problems.Add(new(
                CalibrationGenerationProblemCodes.PatchBaselineMissing,
                "specification.profiles",
                "The baseline profile required by this patch is unavailable."));
        }

        if (problems.Count > 0)
        {
            return CalibrationGenerationResults.Failure<CalibrationProfilePatchExport>(problems);
        }

        string value = FormatValue(selection);
        string? baselineValue = ReadBaseline(baseline!.ExactJson!, nativeKey);

        CalibrationProfilePatch patch = new()
        {
            SchemaVersion = PatchSchemaVersion,
            ProjectId = document.ProjectId,
            SourceAttemptId = document.AttemptId,
            SourceObservationId = selection.ObservationId,
            ProfileType = profileType,
            BaselineMachineProfileSha256 = document.Profiles.Machine?.Sha256 ?? string.Empty,
            BaselineProcessProfileSha256 = document.Profiles.Process?.Sha256 ?? string.Empty,
            BaselineFilamentProfileSha256 = document.Profiles.Filament?.Sha256 ?? string.Empty,
            GeneratorName = document.Generator.Name,
            GeneratorVersion = document.Generator.Version,
            SlicerEngine = CalibrationSupportedTuple.SlicerEngine,
            SlicerDistribution = CalibrationSupportedTuple.SlicerDistribution,
            SlicerVersion = document.Compatibility.SlicerVersion!,
            SlicerContainerDigest = document.Compatibility.SlicerContainerDigest!.Trim(),
            Entries =
            [
                new CalibrationProfilePatchEntry(
                    selection.Parameter.ToString(),
                    nativeKey,
                    selection.Unit,
                    baselineValue,
                    value),
            ],
        };

        string patchJson = CalibrationCanonicalJson.Serialize(patch);
        string exactProfileJson = BuildExactProfileJson(
            baseline.ExactJson!,
            nativeKey,
            value,
            profileName);
        string exactProfileSha256 = CalibrationCanonicalJson.ComputeTextSha256(exactProfileJson);

        GeneratedProfileRevisionCreateRequest request = new()
        {
            ClientId = document.Generator.Name,
            GenerationRequestId = operationId.Trim(),
            SourceAttemptId = document.AttemptId,
            ProfileType = profileType,
            SchemaVersion = PatchSchemaVersion,
            SlicerEngine = CalibrationSupportedTuple.SlicerEngine,
            SlicerDistribution = CalibrationSupportedTuple.SlicerDistribution,
            SlicerVersion = document.Compatibility.SlicerVersion,
            SlicerContainerDigest = document.Compatibility.SlicerContainerDigest,
            Name = profileName.Trim(),
            NormalizedSettings = JsonSerializer.Deserialize<JsonElement>(patchJson),
            ExactProfileJson = exactProfileJson,
            SourceProfileFingerprint = BuildFingerprint(document),
            GeneratorVersion = document.Generator.Version,
            SourceMachineProfileId = document.Profiles.Machine?.Id,
            SourceProcessProfileId = document.Profiles.Process?.Id,
            SourceFilamentProfileId = document.Profiles.Filament?.Id,
            NozzleTemperature = selection.Parameter == CalibrationPatchParameter.NozzleTemperature
                ? (int)selection.Value
                : null,
            FlowRatio = selection.Parameter == CalibrationPatchParameter.FlowRatio
                ? selection.Value
                : null,
            PressureAdvance = selection.Parameter == CalibrationPatchParameter.PressureAdvance
                ? selection.Value
                : null,
            RetractionLength = selection.Parameter == CalibrationPatchParameter.RetractionLength
                ? selection.Value
                : null,
            MaximumVolumetricFlow =
                selection.Parameter == CalibrationPatchParameter.MaximumVolumetricSpeed
                    ? selection.Value
                    : null,
        };

        CalibrationApiResult<GeneratedProfileRevisionDto> persisted =
            await _projectService.CreateGeneratedProfileAsync(
                document.ProjectId,
                request,
                actor,
                cancellationToken).ConfigureAwait(false);

        if (!persisted.IsSuccess || persisted.Value is null)
        {
            return CalibrationGenerationResults.Failure<CalibrationProfilePatchExport>(
                CalibrationGenerationProblemCodes.PatchPersistenceRejected,
                "profileHistory",
                persisted.Code ?? "The authoritative profile history rejected the patch.");
        }

        return CalibrationGenerationResults.Success(new CalibrationProfilePatchExport(
            patch,
            patchJson,
            CalibrationCanonicalJson.ComputeTextSha256(patchJson),
            exactProfileJson,
            exactProfileSha256,
            persisted.Value));
    }

    private static (string ProfileType, string NativeKey) MapParameter(
        CalibrationPatchParameter parameter,
        List<CalibrationGenerationProblem> problems)
    {
        switch (parameter)
        {
            case CalibrationPatchParameter.NozzleTemperature:
                return ("filament", "nozzle_temperature");
            case CalibrationPatchParameter.FlowRatio:
                return ("filament", "filament_flow_ratio");
            case CalibrationPatchParameter.MaximumVolumetricSpeed:
                return ("filament", "filament_max_volumetric_speed");
            case CalibrationPatchParameter.PressureAdvance:
                return ("filament", "pressure_advance");
            case CalibrationPatchParameter.RetractionLength:
                return ("machine", "retraction_length");
            default:
                problems.Add(new(
                    CalibrationGenerationProblemCodes.PatchObservationUnsupported,
                    "selection.parameter",
                    "The selected observation does not map to a supported profile setting."));
                return (string.Empty, string.Empty);
        }
    }

    private static void ValidateRange(
        CalibrationObservationSelection selection,
        CalibrationSpecificationDocument document,
        List<CalibrationGenerationProblem> problems)
    {
        bool valid = selection.Parameter switch
        {
            CalibrationPatchParameter.NozzleTemperature =>
                selection.Value >= 150m &&
                selection.Value <= (document.Toolhead.NozzleMaxTemperatureCelsius ??
                    document.Toolhead.HotendMaxTemperatureCelsius ??
                    0),
            CalibrationPatchParameter.FlowRatio => selection.Value is >= 0.50m and <= 1.50m,
            CalibrationPatchParameter.PressureAdvance =>
                selection.Value >= 0m &&
                selection.Value <= (document.Toolhead.IsDirectDrive == true ? 0.5m : 2.0m),
            CalibrationPatchParameter.RetractionLength =>
                selection.Value >= 0m &&
                selection.Value <= (document.Toolhead.IsDirectDrive == true ? 3.0m : 10.0m),
            CalibrationPatchParameter.MaximumVolumetricSpeed =>
                selection.Value > 0m && selection.Value <= document.Print.MaxVolumetricFlow,
            _ => false,
        };

        if (!valid)
        {
            problems.Add(new(
                CalibrationGenerationProblemCodes.PatchValueOutOfRange,
                "selection.value",
                "The selected observation value is outside the authoritative safe range."));
        }
    }

    private static string FormatValue(CalibrationObservationSelection selection) =>
        selection.Parameter == CalibrationPatchParameter.NozzleTemperature
            ? ((int)selection.Value).ToString(CultureInfo.InvariantCulture)
            : decimal.Round(selection.Value, 4).ToString("0.####", CultureInfo.InvariantCulture);

    private static string? ReadBaseline(string exactJson, string nativeKey)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(exactJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty(nativeKey, out JsonElement value))
            {
                return null;
            }

            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Array when value.GetArrayLength() > 0 =>
                    value[0].ValueKind == JsonValueKind.String
                        ? value[0].GetString()
                        : value[0].GetRawText(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string BuildExactProfileJson(
        string baselineJson,
        string nativeKey,
        string value,
        string profileName)
    {
        // The exported artifact is the baseline document with exactly one allowlisted key replaced and
        // a new name. The baseline document itself is never written back to storage.
        using JsonDocument baseline = JsonDocument.Parse(baselineJson);
        Dictionary<string, JsonElement> members = new(StringComparer.Ordinal);
        if (baseline.RootElement.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in baseline.RootElement.EnumerateObject())
            {
                members[property.Name] = property.Value.Clone();
            }
        }

        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            foreach (KeyValuePair<string, JsonElement> member in members
                .OrderBy(member => member.Key, StringComparer.Ordinal))
            {
                if (string.Equals(member.Key, nativeKey, StringComparison.Ordinal) ||
                    string.Equals(member.Key, "name", StringComparison.Ordinal))
                {
                    continue;
                }

                writer.WritePropertyName(member.Key);
                member.Value.WriteTo(writer);
            }

            writer.WriteString("name", profileName.Trim());
            WriteNativeValue(writer, nativeKey, value, members);
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteNativeValue(
        Utf8JsonWriter writer,
        string nativeKey,
        string value,
        Dictionary<string, JsonElement> members)
    {
        writer.WritePropertyName(nativeKey);
        bool baselineWasArray = members.TryGetValue(nativeKey, out JsonElement existing) &&
            existing.ValueKind == JsonValueKind.Array;
        if (baselineWasArray)
        {
            writer.WriteStartArray();
            writer.WriteStringValue(value);
            writer.WriteEndArray();
            return;
        }

        writer.WriteStringValue(value);
    }

    private static string BuildFingerprint(CalibrationSpecificationDocument document) =>
        CalibrationCanonicalJson.ComputeSha256(new
        {
            machine = document.Profiles.Machine?.Sha256 ?? string.Empty,
            process = document.Profiles.Process?.Sha256 ?? string.Empty,
            filament = document.Profiles.Filament?.Sha256 ?? string.Empty,
            snapshot = document.PrinterConfigurationSnapshotSha256,
        });
}
