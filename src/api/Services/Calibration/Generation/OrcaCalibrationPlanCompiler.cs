using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Farm.Web.Api.Services.Calibration.Generation;

/// <summary>A single allowlisted native upstream-Orca setting override.</summary>
/// <param name="Key">The native Orca setting key.</param>
/// <param name="Value">The override value, already formatted for the native profile.</param>
/// <param name="Unit">The explicit unit, or <c>none</c> for dimensionless settings.</param>
/// <param name="Source">Where the value came from, for example <c>specification.print</c>.</param>
public sealed record OrcaSettingOverride(string Key, string Value, string Unit, string Source);

/// <summary>
/// The rule that decides which native upstream-Orca profile keys can carry arbitrary commands,
/// post-processing scripts or preset-matching notes.
/// </summary>
/// <remarks>
/// <para>
/// Official upstream vendor profiles populate these keys, so a calibration plan neutralizes them
/// rather than refusing the profile outright: the immutable baseline document keeps its original
/// bytes and digest as provenance, while the document a worker receives carries none of their
/// values.
/// </para>
/// <para>
/// The rule is stated by shape rather than by enumeration, because upstream adds custom G-code
/// hooks release by release: every key whose native name ends in <see cref="GcodeSuffix"/> carries
/// commands by construction, so a hook this build has never heard of — a future
/// <c>vendor_magic_gcode</c> — is neutralized on sight instead of surviving as an unknown command
/// field. <see cref="AlwaysForbidden"/> adds the two command-bearing keys that do not carry the
/// suffix. The rule is fixed in this build: it is never extended, narrowed or supplied by a caller,
/// a request or a profile.
/// </para>
/// </remarks>
public static class OrcaProfileCommandKeys
{
    /// <summary>The native suffix every upstream custom G-code hook key ends with.</summary>
    public const string GcodeSuffix = "_gcode";

    /// <summary>
    /// Gets the command-bearing keys that do not end in <see cref="GcodeSuffix"/>, in ordinal order.
    /// </summary>
    public static IReadOnlyList<string> AlwaysForbidden { get; } =
    [
        "post_process",
        "printer_notes",
    ];

    private static readonly HashSet<string> AlwaysForbiddenKeys =
        new(AlwaysForbidden, StringComparer.OrdinalIgnoreCase);

    /// <summary>Decides whether a native profile key is neutralized before a worker sees it.</summary>
    /// <param name="key">The native profile key name.</param>
    /// <returns><see langword="true"/> when the key carries server-owned content.</returns>
    /// <remarks>
    /// Native Orca keys are lowercase snake_case. The comparison ignores case anyway, so a cased
    /// variant of a hook name cannot smuggle a command field past the rule.
    /// </remarks>
    public static bool IsForbidden(string? key) =>
        !string.IsNullOrEmpty(key) &&
        (key.EndsWith(GcodeSuffix, StringComparison.OrdinalIgnoreCase) ||
            AlwaysForbiddenKeys.Contains(key));
}

/// <summary>The effective native profile document derived from an exact upstream baseline.</summary>
/// <param name="Json">The canonical effective JSON a worker is allowed to receive.</param>
/// <param name="Sha256">The lowercase hexadecimal SHA-256 of <paramref name="Json"/>.</param>
/// <param name="NeutralizedKeys">
/// The names of the keys that were neutralized, in ordinal order.
/// </param>
public sealed record OrcaEffectiveProfileDocument(
    string Json,
    string Sha256,
    IReadOnlyList<string> NeutralizedKeys);

/// <summary>
/// Derives the effective native profile document a pinned slicing worker may receive from an exact
/// upstream baseline document.
/// </summary>
/// <remarks>
/// <para>
/// The derivation is total, deterministic and driven only by <see cref="OrcaProfileCommandKeys"/>:
/// every top-level key the rule forbids is emptied in place when its declared shape allows it
/// (text becomes <c>""</c>, a list becomes <c>[]</c>) and dropped otherwise. No other key is added,
/// removed, reordered in meaning or rewritten, and no forbidden value is ever copied into the
/// result, so no caller-authored command or note can reach the slicer, a log, a manifest or emitted
/// G-code.
/// </para>
/// <para>
/// The result is canonical: object members are ordered ordinally at every depth, so the same
/// baseline always yields the same bytes and therefore the same digest. Numbers are copied as their
/// original JSON tokens rather than re-formatted through a CLR numeric type, so a profile keeps the
/// precision, magnitude and spelling the vendor shipped.
/// </para>
/// </remarks>
public static class OrcaEffectiveProfileFactory
{
    /// <summary>Derives the effective document from exact baseline JSON text.</summary>
    /// <param name="exactJson">The verbatim upstream baseline document.</param>
    /// <returns>The effective document, its digest and the ordered neutralized keys.</returns>
    /// <exception cref="ArgumentException">The text is absent or is not a JSON object.</exception>
    /// <exception cref="JsonException">The text is not valid JSON.</exception>
    public static OrcaEffectiveProfileDocument Derive(string exactJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exactJson);
        using JsonDocument document = JsonDocument.Parse(exactJson);
        return Derive(document.RootElement);
    }

    /// <summary>Derives the effective document from an already-parsed baseline document.</summary>
    /// <param name="exact">The verbatim upstream baseline document.</param>
    /// <returns>The effective document, its digest and the ordered neutralized keys.</returns>
    /// <exception cref="ArgumentException">The element is not a JSON object.</exception>
    /// <exception cref="JsonException">A value in the document cannot be written back out.</exception>
    public static OrcaEffectiveProfileDocument Derive(JsonElement exact)
    {
        if (exact.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException(
                "An exact native profile document must be a JSON object.",
                nameof(exact));
        }

        List<string> neutralized = [];
        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();

            // Members are visited in ordinal order, so the audit list is identical for two documents
            // that differ only in how their members are laid out.
            foreach (JsonProperty property in exact
                .EnumerateObject()
                .OrderBy(property => property.Name, StringComparer.Ordinal))
            {
                if (!OrcaProfileCommandKeys.IsForbidden(property.Name))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                    continue;
                }

                neutralized.Add(property.Name);
                switch (property.Value.ValueKind)
                {
                    case JsonValueKind.String:
                        writer.WriteString(property.Name, string.Empty);
                        break;
                    case JsonValueKind.Array:
                        writer.WritePropertyName(property.Name);
                        writer.WriteStartArray();
                        writer.WriteEndArray();
                        break;
                    default:

                        // A shape that is neither text nor a list cannot be emptied in place, so the
                        // key is dropped entirely instead of being carried in any form.
                        break;
                }
            }

            writer.WriteEndObject();
        }

        string json = Encoding.UTF8.GetString(buffer.ToArray());
        return new OrcaEffectiveProfileDocument(
            json,
            CalibrationCanonicalJson.ComputeTextSha256(json),
            neutralized);
    }

    /// <summary>
    /// Writes one baseline value in the canonical form an effective profile document uses.
    /// </summary>
    /// <param name="writer">A writer positioned where the value belongs.</param>
    /// <param name="element">The value to write.</param>
    /// <remarks>
    /// This is deliberately separate from the canonicalization every calibration digest uses. That
    /// one serializes trusted server-owned models, so it may normalize numbers through CLR types;
    /// this one copies an untrusted third-party document a worker must be able to slice, so a
    /// number is emitted as the exact token the vendor wrote. Reading <c>1e999</c> as a
    /// <see cref="double"/> would yield infinity, which is not writable as JSON at all, and
    /// <c>99999999999999999999</c> or a twenty-digit decimal would silently lose digits.
    /// </remarks>
    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (JsonProperty property in element
                    .EnumerateObject()
                    .OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in element.EnumerateArray())
                {
                    WriteCanonical(writer, item);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(element.GetRawText());
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                writer.WriteNullValue();
                break;
            default:
                throw new ArgumentException(
                    $"Unsupported JSON value kind '{element.ValueKind}'.",
                    nameof(element));
        }
    }
}

/// <summary>The native profile documents a plan carries, with their verified digests.</summary>
/// <param name="Id">Authoritative profile identity.</param>
/// <param name="Kind">Profile kind.</param>
/// <param name="Revision">Profile revision.</param>
/// <param name="SourceExactJson">
/// The immutable upstream baseline document, byte for byte as the authoritative snapshot stored it.
/// It is provenance only: it is never written to a worker, a slice job, a log or emitted G-code.
/// </param>
/// <param name="SourceSha256">The verified digest of <paramref name="SourceExactJson"/>.</param>
/// <param name="EffectiveJson">
/// The canonical document the worker receives: the baseline with every forbidden command or notes
/// key neutralized and nothing else changed.
/// </param>
/// <param name="EffectiveSha256">
/// The digest of <paramref name="EffectiveJson"/>. This is the digest delivered on the claim,
/// verified by the worker before it writes the document, and reported back on completion.
/// </param>
/// <param name="NeutralizedKeys">
/// The forbidden keys neutralized in <paramref name="EffectiveJson"/>, in ordinal order; empty when
/// the baseline declared none.
/// </param>
public sealed record OrcaPlanProfile(
    Guid Id,
    string Kind,
    string? Revision,
    string SourceExactJson,
    string SourceSha256,
    string EffectiveJson,
    string EffectiveSha256,
    IReadOnlyList<string> NeutralizedKeys);

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

/// <summary>A profile identity and both digests recorded in a plan manifest.</summary>
/// <param name="Id">Authoritative profile identity.</param>
/// <param name="Revision">Profile revision.</param>
/// <param name="SourceSha256">
/// The verified digest of the immutable upstream baseline document. This is the provenance digest
/// the authoritative snapshot, the specification and the G-code manifest all agree on.
/// </param>
/// <param name="EffectiveSha256">
/// The digest of the effective document the worker receives and verifies.
/// </param>
/// <param name="NeutralizedKeys">
/// The command or notes keys neutralized between the two documents, in ordinal order. Only key
/// names are recorded; a neutralized value is never carried into the manifest.
/// </param>
public sealed record OrcaPlanProfileReference(
    Guid Id,
    string? Revision,
    string SourceSha256,
    string EffectiveSha256,
    IReadOnlyList<string> NeutralizedKeys);

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
/// <param name="MachineProfile">The machine profile baseline and its effective document.</param>
/// <param name="ProcessProfile">The process profile baseline and its effective document.</param>
/// <param name="FilamentProfile">The filament profile baseline and its effective document.</param>
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
    /// <para>
    /// The compiler consumes the exact native profile documents supplied by the authoritative resolver
    /// or snapshot and verifies each digest before use. It never invents a missing container or binary
    /// digest: an unavailable pinned identity is returned as an explicit dependency error.
    /// </para>
    /// <para>
    /// Only after a profile has passed every check — original digest, profile safety, JSON shape,
    /// fully resolved inheritance and, for the machine profile, nozzle compatibility — does the
    /// compiler derive the effective document the worker receives, by neutralizing the keys
    /// <see cref="OrcaProfileCommandKeys.IsForbidden(string?)"/> names. The baseline document and
    /// its digest are left untouched as immutable provenance, and the plan carries both digests plus
    /// the ordered neutralized keys so the change is auditable. Everything else still fails closed:
    /// unknown dangerous fields, credential, URL or path content, malformed JSON, an unresolved
    /// parent reference and a nozzle mismatch are rejections, not neutralizations.
    /// </para>
    /// </remarks>
    CalibrationGenerationResult<OrcaCalibrationPlan> Compile(
        CalibrationSpecification specification,
        CalibrationValidatedModel model);
}

/// <summary>
/// The plan manifest schema versions this build can write, and the rule that recognizes a durable
/// checkpoint written by a superseded one.
/// </summary>
/// <remarks>
/// <para>
/// A plan manifest digest is a durable checkpoint: a run that was accepted with one is expected to
/// recompile byte-identically on every later pass, and a difference normally means the inputs
/// drifted. Upgrading the server can also change the digest without changing the plan at all, when
/// a release changes only how a manifest is written down. That is a trusted change, not drift, so
/// this type can still write every superseded layout and can tell the two cases apart.
/// </para>
/// <para>
/// Superseded layouts are frozen: they are reproduced exactly as the release that emitted them
/// wrote them, are only ever used to recognize and continue an in-flight run, and are never chosen
/// for a newly accepted one.
/// </para>
/// </remarks>
public static class OrcaCalibrationPlanManifestSchema
{
    /// <summary>The schema every newly compiled plan is written with.</summary>
    /// <remarks>
    /// <c>1.1</c> replaced the single per-profile <c>sha256</c> with the baseline digest, the
    /// effective digest and the ordered neutralized keys, so a manifest states both what the
    /// authoritative snapshot stored and what the worker was actually given.
    /// </remarks>
    public const string Current = "1.1";

    /// <summary>The schema that recorded one digest per profile and no neutralization record.</summary>
    public const string SingleProfileDigest = "1.0";

    /// <summary>Gets the superseded schemas, newest first.</summary>
    public static IReadOnlyList<string> Superseded { get; } = [SingleProfileDigest];

    /// <summary>Writes the canonical manifest JSON for one schema version.</summary>
    /// <param name="manifest">The compiled manifest body.</param>
    /// <param name="schemaVersion">The schema version to write.</param>
    /// <returns>The canonical JSON that version produces for this body.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The version is not written by this build.</exception>
    public static string Serialize(OrcaCalibrationPlanManifest manifest, string schemaVersion)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaVersion);

        return schemaVersion switch
        {
            Current => CalibrationCanonicalJson.Serialize(manifest with { SchemaVersion = Current }),
            SingleProfileDigest =>
                CalibrationCanonicalJson.Serialize(ToSingleProfileDigestBody(manifest)),
            _ => throw new ArgumentOutOfRangeException(
                nameof(schemaVersion),
                schemaVersion,
                "The plan manifest schema version is not written by this build."),
        };
    }

    /// <summary>
    /// Re-expresses a freshly compiled plan under the superseded schema a durable checkpoint was
    /// written by, when that schema reproduces the checkpointed digest exactly.
    /// </summary>
    /// <param name="plan">The plan as this build compiled it.</param>
    /// <param name="checkpointSha256">The plan manifest digest a run was accepted with.</param>
    /// <returns>
    /// The same plan carrying the checkpointed manifest identity, or <see langword="null"/> when no
    /// superseded schema explains the digest.
    /// </returns>
    /// <remarks>
    /// A match proves the recompiled plan body is identical and only the manifest layout changed, so
    /// the run keeps completing under the schema it was accepted with: its checkpoint, its submitted
    /// job, its composed program and its promotion all stay byte-identical, and nothing durable is
    /// rewritten. No match means the plan body itself changed, which stays a terminal mismatch.
    /// </remarks>
    public static OrcaCalibrationPlan? BindToCheckpoint(
        OrcaCalibrationPlan plan,
        string? checkpointSha256)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (string.IsNullOrWhiteSpace(checkpointSha256))
        {
            return null;
        }

        foreach (string version in Superseded)
        {
            OrcaCalibrationPlanManifest manifest = plan.Manifest with { SchemaVersion = version };
            string json = Serialize(manifest, version);
            string digest = CalibrationCanonicalJson.ComputeTextSha256(json);
            if (!CalibrationCanonicalJson.DigestsMatch(digest, checkpointSha256))
            {
                continue;
            }

            return plan with
            {
                Manifest = manifest,
                ManifestJson = json,
                ManifestSha256 = digest,
            };
        }

        return null;
    }

    private static SingleProfileDigestManifest ToSingleProfileDigestBody(
        OrcaCalibrationPlanManifest manifest) =>
        new()
        {
            SchemaVersion = SingleProfileDigest,
            ProjectId = manifest.ProjectId,
            AttemptId = manifest.AttemptId,
            OrchestrationId = manifest.OrchestrationId,
            Method = manifest.Method,
            SpecificationSha256 = manifest.SpecificationSha256,
            SlicerEngine = manifest.SlicerEngine,
            SlicerDistribution = manifest.SlicerDistribution,
            SlicerVersion = manifest.SlicerVersion,
            SlicerContainerDigest = manifest.SlicerContainerDigest,
            SlicerBinarySha256 = manifest.SlicerBinarySha256,
            Machine = ToSingleProfileDigestReference(manifest.Machine),
            Process = ToSingleProfileDigestReference(manifest.Process),
            Filament = ToSingleProfileDigestReference(manifest.Filament),
            Model = manifest.Model,
            GeneratorName = manifest.GeneratorName,
            GeneratorVersion = manifest.GeneratorVersion,
            Overrides = manifest.Overrides,
            Segments = manifest.Segments,
        };

    // The 1.0 layout recorded exactly one digest per profile, and that digest was the immutable
    // upstream baseline, which is still what SourceSha256 carries.
    private static SingleProfileDigestReference ToSingleProfileDigestReference(
        OrcaPlanProfileReference reference) =>
        new(reference.Id, reference.Revision, reference.SourceSha256);

    /// <summary>The frozen 1.0 manifest body.</summary>
    private sealed record SingleProfileDigestManifest
    {
        public required string SchemaVersion { get; init; }

        public required Guid ProjectId { get; init; }

        public required Guid AttemptId { get; init; }

        public required Guid OrchestrationId { get; init; }

        public required string Method { get; init; }

        public required string SpecificationSha256 { get; init; }

        public required string SlicerEngine { get; init; }

        public required string SlicerDistribution { get; init; }

        public required string SlicerVersion { get; init; }

        public required string SlicerContainerDigest { get; init; }

        public required string SlicerBinarySha256 { get; init; }

        public required SingleProfileDigestReference Machine { get; init; }

        public required SingleProfileDigestReference Process { get; init; }

        public required SingleProfileDigestReference Filament { get; init; }

        public required OrcaPlanModelReference Model { get; init; }

        public required string GeneratorName { get; init; }

        public required string GeneratorVersion { get; init; }

        public required IReadOnlyList<OrcaSettingOverride> Overrides { get; init; }

        public required IReadOnlyList<CalibrationSegmentSpecification> Segments { get; init; }
    }

    /// <summary>The frozen 1.0 profile reference.</summary>
    private sealed record SingleProfileDigestReference(Guid Id, string? Revision, string Sha256);
}

/// <summary>Default <see cref="IOrcaCalibrationPlanCompiler"/>.</summary>
public sealed class OrcaCalibrationPlanCompiler : IOrcaCalibrationPlanCompiler
{
    /// <summary>The plan manifest schema version emitted by this build.</summary>
    public const string ManifestSchemaVersion = OrcaCalibrationPlanManifestSchema.Current;

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

        VerifiedBaselineProfile? machine =
            VerifyProfile(document.Profiles.Machine, "machine", problems);
        VerifiedBaselineProfile? process =
            VerifyProfile(document.Profiles.Process, "process", problems);
        VerifiedBaselineProfile? filament =
            VerifyProfile(document.Profiles.Filament, "filament", problems);

        if (machine is not null)
        {
            VerifyNozzle(machine.Root, document.Toolhead, problems);
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

        // Every check has now passed, so the effective documents the worker will receive are derived
        // from the verified baselines. A rejected compilation never produces one.
        OrcaPlanProfile? machinePlan = ToPlanProfile(machine, problems);
        OrcaPlanProfile? processPlan = ToPlanProfile(process, problems);
        OrcaPlanProfile? filamentPlan = ToPlanProfile(filament, problems);
        if (problems.Count > 0 || machinePlan is null || processPlan is null || filamentPlan is null)
        {
            return CalibrationGenerationResults.Failure<OrcaCalibrationPlan>(problems);
        }

        OrcaCalibrationPlanManifest manifest = new()
        {
            SchemaVersion = OrcaCalibrationPlanManifestSchema.Current,
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
            Machine = Reference(machinePlan),
            Process = Reference(processPlan),
            Filament = Reference(filamentPlan),
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

        string manifestJson = OrcaCalibrationPlanManifestSchema.Serialize(
            manifest,
            OrcaCalibrationPlanManifestSchema.Current);
        string manifestSha256 = CalibrationCanonicalJson.ComputeTextSha256(manifestJson);
        return CalibrationGenerationResults.Success(new OrcaCalibrationPlan(
            manifest,
            manifestJson,
            manifestSha256,
            machinePlan,
            processPlan,
            filamentPlan));
    }

    private static OrcaPlanProfile? ToPlanProfile(
        VerifiedBaselineProfile baseline,
        List<CalibrationGenerationProblem> problems)
    {
        OrcaEffectiveProfileDocument effective;
        try
        {
            effective = OrcaEffectiveProfileFactory.Derive(baseline.Root);
        }
        catch (Exception error) when (error is JsonException or ArgumentException)
        {
            // A document that parsed but cannot be written back out is still a malformed profile,
            // not a server fault: it is reported as a rejection like every other profile problem.
            problems.Add(new(
                CalibrationGenerationProblemCodes.PlanProfileJsonInvalid,
                $"specification.profiles.{baseline.Kind}",
                $"The exact native {baseline.Kind} profile cannot be reduced to an effective document."));
            return null;
        }

        return new OrcaPlanProfile(
            baseline.Id,
            baseline.Kind,
            baseline.Revision,
            baseline.ExactJson,
            baseline.Sha256,
            effective.Json,
            effective.Sha256,
            effective.NeutralizedKeys);
    }

    private static OrcaPlanProfileReference Reference(OrcaPlanProfile profile) =>
        new(
            profile.Id,
            profile.Revision,
            profile.SourceSha256,
            profile.EffectiveSha256,
            profile.NeutralizedKeys);

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

    /// <summary>An exact upstream baseline document that has passed every plan-level check.</summary>
    /// <param name="Id">Authoritative profile identity.</param>
    /// <param name="Kind">Profile kind.</param>
    /// <param name="Revision">Profile revision.</param>
    /// <param name="ExactJson">The verbatim baseline document.</param>
    /// <param name="Sha256">The verified digest of <paramref name="ExactJson"/>.</param>
    /// <param name="Root">The parsed baseline document, so it is never parsed twice.</param>
    private sealed record VerifiedBaselineProfile(
        Guid Id,
        string Kind,
        string? Revision,
        string ExactJson,
        string Sha256,
        JsonElement Root);

    private static VerifiedBaselineProfile? VerifyProfile(
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

        // Safety runs against the baseline, so a credential, a private URL, an absolute path or a
        // host command anywhere in the document is still a rejection and is never neutralized away.
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

        return new VerifiedBaselineProfile(
            profile.Id,
            kind,
            profile.Revision,
            profile.ExactJson,
            profile.Sha256!.Trim().ToLowerInvariant(),
            root);
    }

    private static void VerifyNozzle(
        JsonElement machine,
        CalibrationToolheadContext toolhead,
        List<CalibrationGenerationProblem> problems)
    {
        if (!machine.TryGetProperty("nozzle_diameter", out JsonElement nozzle))
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
