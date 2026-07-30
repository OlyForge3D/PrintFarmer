using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Services.Queue;

/// <summary>
/// Server-derived classification and provenance for a G-code artifact
/// (issue #900, defect 3). Produced only by <see cref="QueueJobClassifier"/>.
/// </summary>
/// <param name="JobKind">Authoritative job kind derived from artifact lineage.</param>
/// <param name="CalibrationProjectId">Calibration project from the artifact, when present.</param>
/// <param name="CalibrationAttemptId">Calibration attempt from the artifact, when present.</param>
/// <param name="CalibrationOrchestrationId">Calibration orchestration from the artifact, when present.</param>
/// <param name="SourceArtifactId">Slicer artifact promoted into the library.</param>
/// <param name="SliceJobId">Slice job that produced the artifact.</param>
/// <param name="GcodeContentSha256">Verified content hash of the promoted bytes.</param>
/// <param name="SpecificationSha256">Calibration specification hash.</param>
/// <param name="MachineProfileSha256">Effective machine profile hash.</param>
/// <param name="ProcessProfileSha256">Effective process profile hash.</param>
/// <param name="FilamentProfileSha256">Effective filament profile hash.</param>
/// <param name="RequiredFirmwareFamily">Firmware family the artifact targets.</param>
/// <param name="RequiredGcodeDialect">G-code dialect the artifact was generated for.</param>
/// <param name="RequiredSlicerEngine">Slicer engine recorded at promotion.</param>
/// <param name="RequiredSlicerDistribution">Slicer distribution recorded at promotion.</param>
/// <param name="RequiredSlicerVersion">Pinned slicer version recorded at promotion.</param>
/// <param name="RequiredSlicerContainerDigest">Pinned slicer container digest, when supplied.</param>
public sealed record QueueJobClassification(
    JobKind JobKind,
    Guid? CalibrationProjectId,
    Guid? CalibrationAttemptId,
    Guid? CalibrationOrchestrationId,
    Guid? SourceArtifactId,
    Guid? SliceJobId,
    string? GcodeContentSha256,
    string? SpecificationSha256,
    string? MachineProfileSha256,
    string? ProcessProfileSha256,
    string? FilamentProfileSha256,
    PrinterFirmwareFamily? RequiredFirmwareFamily,
    PrinterGcodeDialect? RequiredGcodeDialect,
    string? RequiredSlicerEngine,
    string? RequiredSlicerDistribution,
    string? RequiredSlicerVersion,
    string? RequiredSlicerContainerDigest);

/// <summary>
/// Server-authoritative classification of a queue request from the promoted, immutable
/// <see cref="GcodeFile"/> lineage (issue #900, defect 3).
///
/// Clients never decide whether a job is a calibration job, nor what its provenance is.
/// The server inspects the artifact that will actually be printed and derives:
/// <list type="bullet">
///   <item><see cref="PrintJob.JobKind"/>;</item>
///   <item>the calibration project / attempt / orchestration lineage;</item>
///   <item>the slicer tuple, firmware family, G-code dialect and content hashes.</item>
/// </list>
/// A promoted calibration artifact can therefore never be queued as
/// <see cref="JobKind.Standard"/> through the primary, analytics or management paths.
/// </summary>
public static class QueueJobClassifier
{
    /// <summary>
    /// An artifact carries calibration lineage when it was promoted from a calibration
    /// attempt or orchestration. Project id alone is not sufficient — the attempt or
    /// orchestration identifies the exact immutable calibration output.
    /// </summary>
    /// <param name="gcode">G-code artifact to inspect.</param>
    /// <returns><see langword="true"/> when the artifact is a calibration output.</returns>
    public static bool IsCalibrationArtifact(GcodeFile gcode)
    {
        ArgumentNullException.ThrowIfNull(gcode);

        return gcode.CalibrationAttemptId.HasValue ||
               gcode.CalibrationOrchestrationId.HasValue ||
               (gcode.CalibrationProjectId.HasValue && !string.IsNullOrWhiteSpace(gcode.CalibrationManifestSha256));
    }

    /// <summary>
    /// Derives the authoritative classification for a queue request.
    /// </summary>
    /// <param name="gcode">The artifact that will actually be printed.</param>
    /// <returns>Server-derived classification and provenance.</returns>
    public static QueueJobClassification Classify(GcodeFile gcode)
    {
        ArgumentNullException.ThrowIfNull(gcode);

        bool isCalibration = IsCalibrationArtifact(gcode);

        if (!isCalibration)
        {
            return new QueueJobClassification(
                JobKind.Standard,
                CalibrationProjectId: null,
                CalibrationAttemptId: null,
                CalibrationOrchestrationId: null,
                SourceArtifactId: gcode.SourceArtifactId,
                SliceJobId: gcode.SourceSliceJobId,
                GcodeContentSha256: gcode.ContentSha256,
                SpecificationSha256: null,
                MachineProfileSha256: null,
                ProcessProfileSha256: null,
                FilamentProfileSha256: null,
                RequiredFirmwareFamily: null,
                RequiredGcodeDialect: null,
                RequiredSlicerEngine: null,
                RequiredSlicerDistribution: null,
                RequiredSlicerVersion: null,
                RequiredSlicerContainerDigest: null);
        }

        return new QueueJobClassification(
            JobKind.FilamentCalibration,
            gcode.CalibrationProjectId,
            gcode.CalibrationAttemptId,
            gcode.CalibrationOrchestrationId,
            gcode.SourceArtifactId,
            gcode.SourceSliceJobId,
            gcode.ContentSha256,
            gcode.SpecificationSha256,
            gcode.MachineProfileSha256,
            gcode.ProcessProfileSha256,
            gcode.FilamentProfileSha256,
            ParseFirmwareFamily(gcode.FirmwareFamily),
            ParseGcodeDialect(gcode.GcodeDialect),
            gcode.SlicerEngineName,
            gcode.SlicerDistribution,
            gcode.PinnedSlicerVersion,
            gcode.SlicerContainerDigest);
    }

    /// <summary>
    /// Message used when a caller attempts to queue a promoted calibration artifact as a
    /// standard job through any path.
    /// </summary>
    /// <param name="gcodeFileId">Artifact identity for diagnostics.</param>
    /// <returns>Validation message.</returns>
    public static string CalibrationMisclassificationMessage(Guid gcodeFileId) =>
        $"G-code file {gcodeFileId} is a promoted calibration artifact and cannot be queued as a Standard job. " +
        "Job classification and provenance are derived by the server from the immutable artifact lineage.";

    private static PrinterFirmwareFamily? ParseFirmwareFamily(string? value) =>
        Enum.TryParse(value, ignoreCase: true, out PrinterFirmwareFamily parsed) &&
        parsed != PrinterFirmwareFamily.Unknown
            ? parsed
            : null;

    private static PrinterGcodeDialect? ParseGcodeDialect(string? value) =>
        Enum.TryParse(value, ignoreCase: true, out PrinterGcodeDialect parsed) &&
        parsed != PrinterGcodeDialect.Unknown
            ? parsed
            : null;
}
