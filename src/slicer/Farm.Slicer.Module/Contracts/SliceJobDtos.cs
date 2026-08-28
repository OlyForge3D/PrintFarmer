using System.ComponentModel.DataAnnotations;
using Farm.Slicer.Module.Models;

namespace Farm.Slicer.Module.Contracts;

/// <summary>
/// Request to submit a new slicing job.
/// </summary>
/// <remarks>
/// <see cref="SlicerEngine"/> is serialized as a validated string name (for example
/// <c>"OrcaSlicer"</c>). Legacy numeric bodies still bind for backward compatibility, but any value
/// outside the declared enum is rejected with <c>400</c> instead of being cast.
/// </remarks>
public class SubmitSliceJobRequest
{
    [Required]
    public Guid UserId { get; set; }

    public Guid? PrinterId { get; set; }

    /// <summary>
    /// Legacy caller-supplied model location. Ignored when <see cref="Model3DId"/> is supplied and
    /// never dereferenced as a worker or caller URL.
    /// </summary>
    public string ModelFileUrl { get; set; } = string.Empty;

    /// <summary>Stored model identity resolved through authorized slicer storage.</summary>
    public Guid? Model3DId { get; set; }

    public string ModelFileName { get; set; } = string.Empty;

    [Required]
    public SlicerEngineType SlicerEngine { get; set; } = SlicerEngineType.OrcaSlicer;

    /// <summary>
    /// Optional slicer engine version pin (issue #578). When set, the job is routed
    /// to a worker advertising the matching versioned capability tag
    /// (e.g. <c>orcaslicer:2.4.0</c>) and the version is persisted on the
    /// resulting <c>SliceJob</c>. When null/empty, the job carries the
    /// generic engine capability (<c>orcaslicer</c>) and any registered worker
    /// for that engine may claim it (backwards-compatible default).
    /// The server validates the value against the plugin registry and
    /// derives <see cref="RequiredCapabilitiesJson"/> — clients may not
    /// forge a capability tag.
    /// </summary>
    [MaxLength(32)]
    public string? SlicerEngineVersion { get; set; }

    public string? SlicerProfileJson { get; set; }

    public Guid? SlicerProfileId { get; set; }

    /// <summary>Machine profile whose exact native JSON is snapshotted onto the job.</summary>
    public Guid? MachineProfileId { get; set; }

    /// <summary>Process profile whose exact native JSON is snapshotted onto the job.</summary>
    public Guid? ProcessProfileId { get; set; }

    /// <summary>Filament profile whose exact native JSON is snapshotted onto the job.</summary>
    public Guid? FilamentProfileId { get; set; }

    /// <summary>Soft reference to the owning calibration project (idempotency scope).</summary>
    public Guid? CalibrationProjectId { get; set; }

    /// <summary>Soft reference to the calibration attempt that produced this job.</summary>
    public Guid? CalibrationAttemptId { get; set; }

    /// <summary>Soft reference to the durable calibration orchestration saga row.</summary>
    public Guid? CalibrationOrchestrationId { get; set; }

    /// <summary>Idempotency operation identifier supplied by the caller.</summary>
    public Guid? OperationId { get; set; }

    /// <summary>Correlation identifier, unique per owner and project when supplied.</summary>
    public Guid? CorrelationId { get; set; }

    /// <summary>Request checksum, unique per owner and project when supplied.</summary>
    [MaxLength(128)]
    public string? Checksum { get; set; }

    public string? RequiredCapabilitiesJson { get; set; }

    public int Priority { get; set; } = 1;

    /// <summary>
    /// JSON-serialized model transform (rotation/scale) from the UI workspace.
    /// Format: {"rotation":[rx,ry,rz],"scale":[sx,sy,sz]} (radians, Y-up).
    /// </summary>
    public string? ModelTransformJson { get; set; }

    /// <summary>
    /// Per-extruder filament profile names for multi-toolhead printers.
    /// Index corresponds to extruder index. Null or empty for single-toolhead printers.
    /// </summary>
    public List<string>? ExtruderFilamentProfileNames { get; set; }

    /// <summary>
    /// Multiple model file URLs for multi-model slice jobs.
    /// When provided, the worker downloads all listed models and slices them together.
    /// Falls back to <see cref="ModelFileUrl"/> for single-model jobs.
    /// </summary>
    public List<string>? ModelFileUrls { get; set; }

    /// <summary>
    /// Per-model transforms for multi-model slice jobs.
    /// Each entry corresponds positionally to a URL in <see cref="ModelFileUrls"/>.
    /// Format per entry: JSON string with rotation/scale/position arrays.
    /// </summary>
    public List<string?>? ModelFileTransforms { get; set; }

    /// <summary>
    /// Optional calibration mode (issue #1938). When set, the worker resolves the calibration
    /// model from its own bundled OrcaSlicer resources instead of downloading
    /// <see cref="ModelFileUrl"/>/<see cref="Model3DId"/>, so those fields may be omitted. This is
    /// an ordinary slice job — it never sets <see cref="CalibrationProjectId"/>,
    /// <see cref="CalibrationAttemptId"/> or <see cref="CalibrationOrchestrationId"/>, and remains
    /// eligible for send-to-printer like any other slice.
    /// </summary>
    public CalibrationRequest? Calibration { get; set; }
}

/// <summary>
/// Client-supplied calibration mode request (issue #1938).
/// </summary>
public class CalibrationRequest
{
    /// <summary>
    /// The calibration method wire name (see <see cref="Models.CalibrationMethods"/>), e.g.
    /// <c>"flow_rate_pass_1"</c>, <c>"flow_rate_pass_2"</c> or <c>"temperature_tower"</c>.
    /// </summary>
    [Required]
    public string Method { get; set; } = string.Empty;

    /// <summary>
    /// Optional numeric parameters for the method (for example the temperature tower's start
    /// temperature, per-band step and band height in millimeters). Unspecified keys fall back to
    /// the method's defaults.
    /// </summary>
    public Dictionary<string, double>? Params { get; set; }

    /// <summary>
    /// The firmware flavor an input shaping / resonance-compensation calibration (issue #2139,
    /// wire name <c>"input_shaping"</c>) targets — <c>"klipper"</c> or <c>"marlin"</c>
    /// (case-insensitive). Required for that method, since the calibration result is report-only
    /// and firmware-specific (Klipper's <c>[input_shaper]</c> vs. Marlin's <c>M593</c>); ignored
    /// for every other method.
    /// </summary>
    public string? FirmwareFlavor { get; set; }
}

/// <summary>
/// Response after submitting a slicing job.
/// </summary>
public class SubmitSliceJobResponse
{
    public Guid JobId { get; set; }

    public string Status { get; set; } = string.Empty;

    public DateTime QueuedAt { get; set; }

    public int? QueuePosition { get; set; }
}

/// <summary>
/// Response for getting job status.
/// </summary>
public class SliceJobStatusResponse
{
    public Guid Id { get; set; }

    public string Status { get; set; } = string.Empty;

    public int ProgressPercent { get; set; }

    public string? ProgressMessage { get; set; }

    public DateTime QueuedAt { get; set; }

    public DateTime? StartedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Real worker-side failure detail (e.g. OrcaSlicer exit code/stderr or the exact profile
    /// resolution error), populated only for farm admins. Never returned to non-admin callers,
    /// who only ever see the generic <see cref="ErrorMessage"/>.
    /// </summary>
    public string? ErrorDetail { get; set; }

    /// <summary>
    /// Redacted, client-safe reason the requested layout was dropped or altered during slicing
    /// (issue #1800). <see langword="null"/> means the layout was preserved as requested. Unlike
    /// <see cref="ErrorDetail"/>, this field is a small explicitly-modelled enum, not raw worker
    /// diagnostics, so it is safe to surface to every caller.
    /// </summary>
    public LayoutDegradationReason? LayoutDegradation { get; set; }

    /// <summary>
    /// Redacted, client-safe classification of why the job failed (issue #1811). Like
    /// <see cref="LayoutDegradation"/> — and unlike <see cref="ErrorDetail"/> — this is a small
    /// explicitly-modelled enum rather than raw worker diagnostics, so it is safe to surface to
    /// every caller. <see langword="null"/> when the job did not fail, or when it failed before a
    /// worker could classify it.
    /// </summary>
    public SliceFailureReason? FailureReason { get; set; }

    /// <summary>
    /// Fixed, client-safe guidance for <see cref="FailureReason"/>, from
    /// <see cref="SliceFailureHints"/>. Always a compile-time constant string with nothing
    /// job-derived interpolated into it, which is what lets a non-admin caller be told why their
    /// slice failed without exposing worker paths or filenames.
    /// </summary>
    public string? FailureHint { get; set; }

    public int? EstimatedPrintTimeSeconds { get; set; }

    public decimal? FilamentUsedGrams { get; set; }

    public Guid? WorkerId { get; set; }

    public string ModelFileName { get; set; } = string.Empty;

    /// <summary>Canonical engine name, serialized as a validated string.</summary>
    public SlicerEngineType SlicerEngine { get; set; }

    public string ArtifactsRoute { get; set; } = string.Empty;
}

/// <summary>
/// Internal claim response containing the inputs required by an authenticated slicer worker.
/// This contract is never returned from JWT user routes.
/// </summary>
public sealed class WorkerSliceJobResponse
{
    public Guid Id { get; set; }

    /// <summary>
    /// Opaque claim incarnation that must accompany every operation for this job.
    /// </summary>
    public Guid ClaimToken { get; set; }

    public Guid UserId { get; set; }

    public Guid? PrinterId { get; set; }

    public string Status { get; set; } = string.Empty;

    /// <summary>Authenticated API-relative route the worker must use to stream the model bytes.</summary>
    public string ModelFileUrl { get; set; } = string.Empty;

    public string ModelFileName { get; set; } = string.Empty;

    /// <summary>SHA-256 of the stored model bytes so the worker can verify what it downloaded.</summary>
    public string? ModelSha256 { get; set; }

    /// <summary>Canonical engine name, serialized as a validated string.</summary>
    public SlicerEngineType SlicerEngine { get; set; }

    /// <summary>
    /// Resolved engine version pin (issue #578). Null for legacy / unpinned jobs.
    /// </summary>
    public string? SlicerEngineVersion { get; set; }

    public string? SlicerProfileJson { get; set; }

    /// <summary>
    /// JSON-serialized model transform (rotation/scale) from the UI workspace.
    /// </summary>
    public string? ModelTransformJson { get; set; }

    /// <summary>
    /// Multiple model file URLs for multi-model slice jobs.
    /// When populated, the worker should download all listed models.
    /// Empty or null for single-model jobs (use <see cref="ModelFileUrl"/>).
    /// </summary>
    public List<string>? ModelFileUrls { get; set; }

    /// <summary>
    /// Per-model transforms for multi-model slice jobs.
    /// Each entry corresponds positionally to a URL in <see cref="ModelFileUrls"/>.
    /// </summary>
    public List<string?>? ModelFileTransforms { get; set; }

    /// <summary>Exact native upstream-Orca machine profile JSON.</summary>
    public string? MachineProfileJson { get; set; }

    /// <summary>Exact native upstream-Orca process profile JSON.</summary>
    public string? ProcessProfileJson { get; set; }

    /// <summary>Exact native upstream-Orca filament profile JSON.</summary>
    public string? FilamentProfileJson { get; set; }

    /// <summary>Expected SHA-256 of <see cref="MachineProfileJson"/>.</summary>
    public string? MachineProfileSha256 { get; set; }

    /// <summary>Expected SHA-256 of <see cref="ProcessProfileJson"/>.</summary>
    public string? ProcessProfileSha256 { get; set; }

    /// <summary>Expected SHA-256 of <see cref="FilamentProfileJson"/>.</summary>
    public string? FilamentProfileSha256 { get; set; }

    /// <summary>Pinned slicer distribution the worker must be running.</summary>
    public string? SlicerDistribution { get; set; }

    /// <summary>Pinned slicer version the worker must be running.</summary>
    public string? SlicerVersion { get; set; }

    /// <summary>Pinned slicer container digest the worker must be running, when configured.</summary>
    public string? SlicerContainerDigest { get; set; }

    /// <summary>Lease token that must be echoed on every subsequent mutation.</summary>
    public Guid LeaseToken { get; set; }

    /// <summary>Fencing counter that must be echoed on every subsequent mutation.</summary>
    public long LeaseFence { get; set; }

    /// <summary>Absolute UTC instant at which the lease lapses unless renewed.</summary>
    public DateTime? LeaseExpiresAtUtc { get; set; }

    public string? RequiredCapabilitiesJson { get; set; }

    public int Priority { get; set; }

    /// <summary>
    /// Calibration method wire name (issue #1938), or <see langword="null"/> for an ordinary
    /// slice. When set, the worker resolves the model from its own bundled OrcaSlicer resources
    /// instead of downloading <see cref="ModelFileUrl"/>.
    /// </summary>
    public string? CalibrationMethod { get; set; }

    /// <summary>JSON-serialized numeric parameters for <see cref="CalibrationMethod"/>.</summary>
    public string? CalibrationParamsJson { get; set; }
}

/// <summary>
/// Request to mark a slice job as completed and associate produced artifacts.
/// </summary>
public class CompleteSliceJobRequest
{
    [Required]
    public Guid PrimaryArtifactId { get; set; }

    public Guid[]? AdditionalArtifactIds { get; set; }

    public int? EstimatedPrintTimeSeconds { get; set; }

    public decimal? FilamentUsedGrams { get; set; }

    public string? LogText { get; set; }

    /// <summary>
    /// SHA-256 of the effective native machine profile the worker wrote. The API verifies a
    /// claim-delivered digest or records this value for a worker-resolved named selection.
    /// </summary>
    [MaxLength(64)]
    public string? MachineProfileSha256 { get; set; }

    /// <summary>
    /// SHA-256 of the effective native process profile the worker wrote. The API verifies a
    /// claim-delivered digest or records this value for a worker-resolved named selection.
    /// </summary>
    [MaxLength(64)]
    public string? ProcessProfileSha256 { get; set; }

    /// <summary>
    /// SHA-256 of the effective native filament profile set the worker wrote. The API verifies a
    /// claim-delivered digest or records this value for a worker-resolved named selection.
    /// </summary>
    [MaxLength(64)]
    public string? FilamentProfileSha256 { get; set; }

    /// <summary>
    /// Redacted, client-safe reason the requested layout was dropped or altered during slicing
    /// (issue #1800), computed by the worker from the <c>PlacementPlan</c> it resolved. Never the
    /// raw log message.
    /// </summary>
    public LayoutDegradationReason? LayoutDegradation { get; set; }
}

/// <summary>
/// Request to update in-flight job progress.
/// </summary>
public class SliceJobProgressUpdateRequest
{
    [Range(0, 100)]
    public int ProgressPercent { get; set; }

    [MaxLength(256)]
    public string? ProgressMessage { get; set; }
}

/// <summary>
/// Request from a worker that could not complete its claimed slice job.
/// The API deliberately replaces the supplied detail with a generic public error.
/// </summary>
/// <param name="ErrorMessage">Worker-local failure detail; never returned to API clients.</param>
/// <param name="FailureReason">
/// Redacted, client-safe classification of the failure (issue #1811). Optional so a worker that
/// could not classify the failure — or one built before this field existed — still reports the
/// failure itself. Unlike <paramref name="ErrorMessage"/> this value <em>is</em> returned to every
/// API client, which is safe because it is a closed enum carrying no job-derived text.
/// </param>
/// <remarks>
/// The length constraint is declared on the primary-constructor parameter. Declaring it with a
/// <c>property:</c> target makes MVC model validation throw, which previously turned every worker
/// failure report into a <c>500</c> and lost the failure entirely.
/// </remarks>
public sealed record FailSliceJobRequest(
    [MaxLength(1024)] string ErrorMessage,
    SliceFailureReason? FailureReason = null);

/// <summary>
/// Response after successful job completion.
/// </summary>
public class CompleteSliceJobResponse
{
    public Guid JobId { get; set; }

    public string Status { get; set; } = string.Empty;

    public DateTime? CompletedAt { get; set; }

    /// <summary>Canonical authenticated download route for the primary artifact.</summary>
    public string? ResultFileUrl { get; set; }

    public Guid[] ArtifactIds { get; set; } = Array.Empty<Guid>();

    public int? EstimatedPrintTimeSeconds { get; set; }

    public decimal? FilamentUsedGrams { get; set; }

    public Guid? LogArtifactId { get; set; }

    public int? ArtifactsCount { get; set; }

    public long? ArtifactsTotalBytes { get; set; }
}
