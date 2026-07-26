using System.Globalization;
using System.Text;

namespace Farm.Web.Api.Services.Calibration.Generation;

/// <summary>One annotated calibration segment with exact line and byte offsets.</summary>
/// <param name="Index">Zero-based segment index.</param>
/// <param name="Method">Canonical method name.</param>
/// <param name="ParameterName">The tuned parameter.</param>
/// <param name="Value">The value applied for the segment.</param>
/// <param name="Unit">The explicit unit of <paramref name="Value"/>.</param>
/// <param name="StartLayer">First one-based layer.</param>
/// <param name="EndLayer">Last one-based layer.</param>
/// <param name="StartZMillimeters">Z height of the first layer.</param>
/// <param name="EndZMillimeters">Z height of the last layer.</param>
/// <param name="StartLine">One-based line number of the segment begin marker.</param>
/// <param name="EndLine">One-based line number of the segment end marker.</param>
/// <param name="StartByteOffset">Zero-based byte offset of the segment begin marker.</param>
/// <param name="EndByteOffset">Zero-based byte offset just past the segment end marker.</param>
/// <param name="TransitionCommands">Commands emitted to reach this segment safely.</param>
public sealed record CalibrationSegmentAnnotation(
    int Index,
    string Method,
    string ParameterName,
    decimal Value,
    string Unit,
    int StartLayer,
    int EndLayer,
    decimal StartZMillimeters,
    decimal EndZMillimeters,
    int StartLine,
    int EndLine,
    int StartByteOffset,
    int EndByteOffset,
    IReadOnlyList<string> TransitionCommands);

/// <summary>The canonical calibration manifest body a digest is computed over.</summary>
/// <remarks>
/// The manifest deliberately carries no path, URL, credential, worker key or log text. Every member is
/// either an identifier, a digest, a version, or a numeric offset.
/// </remarks>
public sealed record CalibrationGcodeManifest
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

    /// <summary>Gets the method definition version.</summary>
    public required string DefinitionVersion { get; init; }

    /// <summary>Gets the specification digest.</summary>
    public required string SpecificationSha256 { get; init; }

    /// <summary>Gets the plan manifest digest.</summary>
    public required string PlanManifestSha256 { get; init; }

    /// <summary>Gets the model identity the program prints.</summary>
    public required Guid Model3DId { get; init; }

    /// <summary>Gets the model content digest.</summary>
    public required string ModelSha256 { get; init; }

    /// <summary>Gets the immutable baseline machine profile digest.</summary>
    /// <remarks>
    /// The G-code manifest and header record baseline digests, so a printed program always points
    /// back at the upstream documents the authoritative snapshot stored. The effective documents the
    /// worker sliced with are recorded, with their digests, in the plan manifest.
    /// </remarks>
    public required string MachineProfileSha256 { get; init; }

    /// <summary>Gets the immutable baseline process profile digest.</summary>
    public required string ProcessProfileSha256 { get; init; }

    /// <summary>Gets the immutable baseline filament profile digest.</summary>
    public required string FilamentProfileSha256 { get; init; }

    /// <summary>Gets the printer configuration snapshot digest.</summary>
    public required string PrinterConfigurationSnapshotSha256 { get; init; }

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

    /// <summary>Gets the pinned slicer binary digest.</summary>
    public required string SlicerBinarySha256 { get; init; }

    /// <summary>Gets the firmware family.</summary>
    public required string FirmwareFamily { get; init; }

    /// <summary>Gets the firmware version.</summary>
    public required string FirmwareVersion { get; init; }

    /// <summary>Gets the firmware detection source.</summary>
    public required string FirmwareDetectionSource { get; init; }

    /// <summary>Gets the G-code dialect.</summary>
    public required string GcodeDialect { get; init; }

    /// <summary>Gets where the printable body comes from.</summary>
    public required string BodySource { get; init; }

    /// <summary>Gets the annotated segments in emission order.</summary>
    public required IReadOnlyList<CalibrationSegmentAnnotation> Segments { get; init; }

    /// <summary>Gets the commands emitted by the safe reset and finalization block.</summary>
    public required IReadOnlyList<string> ResetCommands { get; init; }

    /// <summary>Gets the total emitted line count.</summary>
    public required int LineCount { get; init; }

    /// <summary>Gets the total emitted byte count.</summary>
    public required int ByteCount { get; init; }

    /// <summary>Gets the SHA-256 of the final annotated G-code.</summary>
    public required string GcodeSha256 { get; init; }
}

/// <summary>Final annotated G-code together with its canonical manifest.</summary>
/// <param name="Gcode">The final annotated program text.</param>
/// <param name="GcodeSha256">The digest of <paramref name="Gcode"/>.</param>
/// <param name="Manifest">The canonical manifest body.</param>
/// <param name="ManifestJson">The canonical manifest JSON.</param>
/// <param name="ManifestSha256">The digest of <paramref name="ManifestJson"/>.</param>
public sealed record AnnotatedCalibrationGcode(
    string Gcode,
    string GcodeSha256,
    CalibrationGcodeManifest Manifest,
    string ManifestJson,
    string ManifestSha256);

/// <summary>
/// Prepends stable machine-readable provenance markers and produces the segment manifest.
/// </summary>
public interface ICalibrationGcodeAnnotator
{
    /// <summary>
    /// Annotates a generated program and builds its manifest.
    /// </summary>
    /// <param name="specification">The compiled specification.</param>
    /// <param name="plan">The compiled plan.</param>
    /// <param name="model">The validated model.</param>
    /// <param name="program">The generated Klipper program.</param>
    /// <returns>The annotated program and manifest, or the ordered rejection reasons.</returns>
    /// <remarks>
    /// Offsets are computed over the final annotated bytes, so a manifest offset always addresses the
    /// exact byte in the artifact that is uploaded, promoted and validated.
    /// </remarks>
    CalibrationGenerationResult<AnnotatedCalibrationGcode> Annotate(
        CalibrationSpecification specification,
        OrcaCalibrationPlan plan,
        CalibrationValidatedModel model,
        KlipperCalibrationProgram program);
}

/// <summary>Default <see cref="ICalibrationGcodeAnnotator"/>.</summary>
public sealed class CalibrationGcodeAnnotator : ICalibrationGcodeAnnotator
{
    /// <summary>The manifest schema version emitted by this build.</summary>
    public const string ManifestSchemaVersion = "1.0";

    /// <inheritdoc/>
    public CalibrationGenerationResult<AnnotatedCalibrationGcode> Annotate(
        CalibrationSpecification specification,
        OrcaCalibrationPlan plan,
        CalibrationValidatedModel model,
        KlipperCalibrationProgram program)
    {
        ArgumentNullException.ThrowIfNull(specification);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(program);

        CalibrationSpecificationDocument document = specification.Document;
        List<CalibrationGenerationProblem> problems = [];

        if (!CalibrationCanonicalJson.DigestsMatch(
            plan.Manifest.SpecificationSha256,
            specification.Sha256))
        {
            problems.Add(new(
                CalibrationGenerationProblemCodes.SpecificationHashMismatch,
                "plan.manifest.specificationSha256",
                "The plan was compiled from a different specification."));
        }

        string recomputedProgram = CalibrationCanonicalJson.ComputeTextSha256(program.Text);
        if (!CalibrationCanonicalJson.DigestsMatch(recomputedProgram, program.Sha256))
        {
            problems.Add(new(
                CalibrationGenerationProblemCodes.GcodeHashMismatch,
                "program.sha256",
                "The generated program digest does not match its text."));
        }

        if (problems.Count > 0)
        {
            return CalibrationGenerationResults.Failure<AnnotatedCalibrationGcode>(problems);
        }

        string header = BuildHeader(document, plan, model, program);
        string gcode = header + program.Text;
        string gcodeSha256 = CalibrationCanonicalJson.ComputeTextSha256(gcode);

        IReadOnlyList<CalibrationSegmentAnnotation>? segments =
            BuildSegments(document, gcode, problems);
        if (segments is null || problems.Count > 0)
        {
            return CalibrationGenerationResults.Failure<AnnotatedCalibrationGcode>(problems);
        }

        CalibrationGcodeManifest manifest = new()
        {
            SchemaVersion = ManifestSchemaVersion,
            ProjectId = document.ProjectId,
            AttemptId = document.AttemptId,
            OrchestrationId = document.OrchestrationId,
            Method = document.Method,
            DefinitionVersion = document.DefinitionVersion,
            SpecificationSha256 = specification.Sha256,
            PlanManifestSha256 = plan.ManifestSha256,
            Model3DId = model.Model3DId,
            ModelSha256 = model.Sha256,
            MachineProfileSha256 = plan.MachineProfile.SourceSha256,
            ProcessProfileSha256 = plan.ProcessProfile.SourceSha256,
            FilamentProfileSha256 = plan.FilamentProfile.SourceSha256,
            PrinterConfigurationSnapshotSha256 = document.PrinterConfigurationSnapshotSha256,
            GeneratorName = document.Generator.Name,
            GeneratorVersion = document.Generator.Version,
            SlicerEngine = plan.Manifest.SlicerEngine,
            SlicerDistribution = plan.Manifest.SlicerDistribution,
            SlicerVersion = plan.Manifest.SlicerVersion,
            SlicerContainerDigest = plan.Manifest.SlicerContainerDigest,
            SlicerBinarySha256 = plan.Manifest.SlicerBinarySha256,
            FirmwareFamily = document.Firmware.Family ?? string.Empty,
            FirmwareVersion = document.Firmware.Version ?? string.Empty,
            FirmwareDetectionSource = document.Firmware.DetectionSource ?? string.Empty,
            GcodeDialect = document.Firmware.GcodeDialect ?? string.Empty,
            BodySource = program.BodySource.ToString(),
            Segments = segments,
            ResetCommands = ExtractResetCommands(gcode),
            LineCount = CountLines(gcode),
            ByteCount = Encoding.UTF8.GetByteCount(gcode),
            GcodeSha256 = gcodeSha256,
        };

        string manifestJson = CalibrationCanonicalJson.Serialize(manifest);
        return CalibrationGenerationResults.Success(new AnnotatedCalibrationGcode(
            gcode,
            gcodeSha256,
            manifest,
            manifestJson,
            CalibrationCanonicalJson.ComputeTextSha256(manifestJson)));
    }

    private static string BuildHeader(
        CalibrationSpecificationDocument document,
        OrcaCalibrationPlan plan,
        CalibrationValidatedModel model,
        KlipperCalibrationProgram program)
    {
        StringBuilder builder = new(2048);
        Meta(builder, "schemaVersion", ManifestSchemaVersion);
        Meta(builder, "projectId", document.ProjectId.ToString());
        Meta(builder, "attemptId", document.AttemptId.ToString());
        Meta(builder, "orchestrationId", document.OrchestrationId.ToString());
        Meta(builder, "method", document.Method);
        Meta(builder, "definitionVersion", document.DefinitionVersion);
        Meta(builder, "specificationSha256", plan.Manifest.SpecificationSha256);
        Meta(builder, "planManifestSha256", plan.ManifestSha256);
        Meta(builder, "model3dId", model.Model3DId.ToString());
        Meta(builder, "modelSha256", model.Sha256);
        Meta(builder, "machineProfileSha256", plan.MachineProfile.SourceSha256);
        Meta(builder, "processProfileSha256", plan.ProcessProfile.SourceSha256);
        Meta(builder, "filamentProfileSha256", plan.FilamentProfile.SourceSha256);
        Meta(
            builder,
            "printerConfigurationSnapshotSha256",
            document.PrinterConfigurationSnapshotSha256);
        Meta(builder, "generatorName", document.Generator.Name);
        Meta(builder, "generatorVersion", document.Generator.Version);
        Meta(builder, "slicerEngine", plan.Manifest.SlicerEngine);
        Meta(builder, "slicerDistribution", plan.Manifest.SlicerDistribution);
        Meta(builder, "slicerVersion", plan.Manifest.SlicerVersion);
        Meta(builder, "slicerContainerDigest", plan.Manifest.SlicerContainerDigest);
        Meta(builder, "slicerBinarySha256", plan.Manifest.SlicerBinarySha256);
        Meta(builder, "firmwareFamily", document.Firmware.Family ?? string.Empty);
        Meta(builder, "firmwareVersion", document.Firmware.Version ?? string.Empty);
        Meta(builder, "firmwareDetectionSource", document.Firmware.DetectionSource ?? string.Empty);
        Meta(builder, "gcodeDialect", document.Firmware.GcodeDialect ?? string.Empty);
        Meta(builder, "bodySource", program.BodySource.ToString());
        Meta(
            builder,
            "segmentCount",
            program.SegmentCount.ToString(CultureInfo.InvariantCulture));
        return builder.ToString();
    }

    private static void Meta(StringBuilder builder, string name, string value) =>
        builder
            .Append(CalibrationGcodeMarkers.HeaderPrefix)
            .Append(' ')
            .Append(name)
            .Append('=')
            .Append(value)
            .Append('\n');

    private static List<CalibrationSegmentAnnotation>? BuildSegments(
        CalibrationSpecificationDocument document,
        string gcode,
        List<CalibrationGenerationProblem> problems)
    {
        Dictionary<int, (int Line, int Offset)> begins = [];
        Dictionary<int, (int Line, int Offset)> ends = [];
        Dictionary<int, List<string>> transitions = [];

        int lineNumber = 0;
        int byteOffset = 0;
        int pendingTransitionTarget = -1;
        List<string> pendingTransition = [];

        foreach (string line in EnumerateLines(gcode))
        {
            lineNumber++;
            int lineBytes = Encoding.UTF8.GetByteCount(line) + 1;

            if (line.StartsWith(CalibrationGcodeMarkers.SegmentTransition, StringComparison.Ordinal))
            {
                pendingTransitionTarget = ReadInt(line, "TO=");
                pendingTransition = [line];
            }
            else if (line.StartsWith(CalibrationGcodeMarkers.SegmentBegin, StringComparison.Ordinal))
            {
                int index = ReadInt(line, "INDEX=");
                begins[index] = (lineNumber, byteOffset);
                if (pendingTransitionTarget == index)
                {
                    transitions[index] = pendingTransition;
                }

                pendingTransitionTarget = -1;
                pendingTransition = [];
            }
            else if (line.StartsWith(CalibrationGcodeMarkers.SegmentEnd, StringComparison.Ordinal))
            {
                ends[ReadInt(line, "INDEX=")] = (lineNumber, byteOffset + lineBytes);
            }
            else if (pendingTransitionTarget >= 0)
            {
                pendingTransition.Add(line);
            }

            byteOffset += lineBytes;
        }

        List<CalibrationSegmentAnnotation> annotations = new(document.Segments.Count);
        foreach (CalibrationSegmentSpecification segment in document.Segments)
        {
            if (!begins.TryGetValue(segment.Index, out (int Line, int Offset) begin) ||
                !ends.TryGetValue(segment.Index, out (int Line, int Offset) end))
            {
                problems.Add(new(
                    CalibrationGenerationProblemCodes.ManifestMismatch,
                    "manifest.segments",
                    "The generated program does not contain a marker for every specification segment."));
                return null;
            }

            IReadOnlyList<string> transitionCommands =
                transitions.TryGetValue(segment.Index, out List<string>? commands)
                    ? commands
                    : [];
            annotations.Add(new CalibrationSegmentAnnotation(
                segment.Index,
                document.Method,
                segment.ParameterName,
                segment.Value,
                segment.Unit,
                segment.StartLayer,
                segment.EndLayer,
                segment.StartZMillimeters,
                segment.EndZMillimeters,
                begin.Line,
                end.Line,
                begin.Offset,
                end.Offset,
                transitionCommands));
        }

        return annotations;
    }

    private static List<string> ExtractResetCommands(string gcode)
    {
        List<string> commands = [];
        bool inFinalize = false;
        foreach (string line in EnumerateLines(gcode))
        {
            if (line.StartsWith(CalibrationGcodeMarkers.Finalize, StringComparison.Ordinal))
            {
                inFinalize = true;
                continue;
            }

            if (line.StartsWith(CalibrationGcodeMarkers.ProgramEnd, StringComparison.Ordinal))
            {
                break;
            }

            if (inFinalize && line.Length > 0 && line[0] != ';')
            {
                commands.Add(line);
            }
        }

        return commands;
    }

    private static IEnumerable<string> EnumerateLines(string gcode)
    {
        int start = 0;
        for (int index = 0; index < gcode.Length; index++)
        {
            if (gcode[index] != '\n')
            {
                continue;
            }

            yield return gcode[start..index];
            start = index + 1;
        }

        if (start < gcode.Length)
        {
            yield return gcode[start..];
        }
    }

    private static int CountLines(string gcode)
    {
        int count = 0;
        foreach (char character in gcode)
        {
            if (character == '\n')
            {
                count++;
            }
        }

        return count;
    }

    private static int ReadInt(string line, string key)
    {
        int start = line.IndexOf(key, StringComparison.Ordinal);
        if (start < 0)
        {
            return -1;
        }

        start += key.Length;
        int end = start;
        while (end < line.Length && char.IsAsciiDigit(line[end]))
        {
            end++;
        }

        return end > start &&
            int.TryParse(
                line.AsSpan(start, end - start),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int value)
            ? value
            : -1;
    }
}
