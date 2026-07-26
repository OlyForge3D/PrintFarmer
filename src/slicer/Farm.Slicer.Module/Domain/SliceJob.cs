using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Farm.Slicer.Module.Domain;

/// <summary>
/// Represents a slicing job request that will be processed by a slicer worker.
/// </summary>
public class SliceJob
{
    public const int MinimumLeaseDurationSeconds = 30;
    public const int MaximumLeaseDurationSeconds = 3600;

    [Key]
    public Guid Id { get; set; }

    /// <summary>
    /// User who requested this slicing job (soft ref — no FK constraint).
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// Target printer for this sliced output (soft ref — no FK constraint).
    /// </summary>
    public Guid? PrinterId { get; set; }

    /// <summary>
    /// URL or path to the 3D model file to slice (STL, OBJ, 3MF, etc.)
    /// </summary>
    [Required]
    [MaxLength(2048)]
    [JsonIgnore]
    public string ModelFileUrl { get; set; } = string.Empty;

    /// <summary>
    /// Original filename of the model.
    /// </summary>
    [MaxLength(512)]
    public string ModelFileName { get; set; } = string.Empty;

    /// <summary>
    /// Legacy numeric slicer engine discriminator retained for rows created before the
    /// canonical string contract. New jobs also populate <see cref="SlicerEngineName"/>,
    /// which is authoritative whenever it is present.
    /// </summary>
    public int SlicerEngine { get; set; }

    /// <summary>
    /// Optional engine-version pin (e.g. "2.4.0", "2.3.1"). NULL = "any version"
    /// (back-compat with legacy single-engine deployments). When non-NULL the
    /// submit-side pins the job's <see cref="RequiredCapabilitiesJson"/> to the
    /// version-only capability tag (e.g. <c>["orcaslicer:2.4.0"]</c>) so only a
    /// worker of that exact engine version can claim the job. Resolved to the
    /// registry's latest library version at submit time; never at claim time.
    /// </summary>
    [MaxLength(32)]
    public string? SlicerEngineVersion { get; set; }

    /// <summary>
    /// Canonical validated engine name (for example <c>OrcaSlicer</c>). Authoritative when set;
    /// <see langword="null"/> only for jobs persisted before the canonical contract existed.
    /// </summary>
    [MaxLength(32)]
    public string? SlicerEngineName { get; set; }

    /// <summary>Stored model identity the worker must resolve bytes through (no caller URL dereference).</summary>
    public Guid? Model3DId { get; set; }

    /// <summary>SHA-256 (hex) of the stored model bytes captured at submission for provenance.</summary>
    [MaxLength(64)]
    public string? ModelSha256 { get; set; }

    /// <summary>
    /// Serialized slicer profile/settings (JSON).
    /// </summary>
    [JsonIgnore]
    public string? SlicerProfileJson { get; set; }

    /// <summary>
    /// Optional reference to a stored ProcessProfile entity used for this job.
    /// When provided, SlicerProfileJson is populated from the profile's RawJson snapshot at submit time
    /// to ensure immutability if the profile later changes.
    /// </summary>
    public Guid? SlicerProfileId { get; set; }

    [JsonIgnore]
    public ProcessProfile? SlicerProfile { get; set; }

    /// <summary>
    /// Required capabilities for this job (JSON array).
    /// Workers must match these capabilities to claim the job.
    /// </summary>
    [JsonIgnore]
    public string? RequiredCapabilitiesJson { get; set; }

    /// <summary>
    /// Job status: Queued, Processing, Completed, Failed, Cancelled.
    /// </summary>
    [Required]
    [MaxLength(50)]
    public string Status { get; set; } = "Queued";

    /// <summary>
    /// Job priority: Low=0, Normal=1, High=2, Critical=3.
    /// </summary>
    public int Priority { get; set; } = 1;

    /// <summary>
    /// When the job was submitted to the queue.
    /// </summary>
    public DateTime QueuedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Correlation ID for idempotent job submission / message envelope tracking.
    /// </summary>
    public Guid? CorrelationId { get; set; }

    /// <summary>
    /// Optional checksum/hash for idempotency and deduplication.
    /// </summary>
    [MaxLength(128)]
    [JsonIgnore]
    public string? Checksum { get; set; }

    /// <summary>
    /// When a worker started processing this job.
    /// </summary>
    public DateTime? StartedAt { get; set; }

    /// <summary>
    /// When the job finished (successfully or with error).
    /// </summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// URL to the resulting G-code file (populated on success).
    /// </summary>
    [MaxLength(2048)]
    [JsonIgnore]
    public string? ResultFileUrl { get; set; }

    /// <summary>
    /// Error message if job failed.
    /// </summary>
    [JsonIgnore]
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Current progress percentage (0-100).
    /// </summary>
    public int ProgressPercent { get; set; }

    /// <summary>
    /// Current progress message.
    /// </summary>
    [MaxLength(512)]
    public string? ProgressMessage { get; set; }

    /// <summary>
    /// Estimated print time in seconds (populated from G-code metadata).
    /// </summary>
    public int? EstimatedPrintTimeSeconds { get; set; }

    /// <summary>
    /// Estimated filament usage in grams (populated from G-code metadata).
    /// </summary>
    public decimal? FilamentUsedGrams { get; set; }

    /// <summary>
    /// ID of the worker that processed/is processing this job.
    /// </summary>
    public Guid? WorkerId { get; set; }

    /// <summary>
    /// When the job was claimed by a worker (pull model).
    /// </summary>
    public DateTime? ClaimedAt { get; set; }

    /// <summary>
    /// Opaque identifier for the current claim incarnation.
    /// A new value is generated every time a worker claims or reclaims the job.
    /// </summary>
    public Guid? ClaimToken { get; set; }

    /// <summary>
    /// When the job lease expires (pull model with timeout).
    /// </summary>
    public DateTime? LeaseExpiresAt { get; set; }

    /// <summary>
    /// Opaque lease token issued on a successful atomic claim. Workers must echo it on every
    /// mutation; a mismatch means the lease was lost and the mutation is rejected.
    /// </summary>
    [JsonIgnore]
    public Guid? LeaseToken { get; set; }

    /// <summary>
    /// Monotonic fencing counter incremented on every successful claim. Guards against a
    /// resumed worker mutating a job that has since been re-claimed by another worker.
    /// </summary>
    public long LeaseFence { get; set; }

    /// <summary>Soft reference to the calibration project that owns this job (no FK constraint).</summary>
    public Guid? CalibrationProjectId { get; set; }

    /// <summary>
    /// Non-null owner-scoped idempotency partition. Equals <see cref="CalibrationProjectId"/> when a
    /// project owns the job and <see cref="Guid.Empty"/> otherwise.
    /// </summary>
    /// <remarks>
    /// SQLite and PostgreSQL treat <see langword="null"/> as distinct inside a unique index, so a
    /// nullable project column would silently void the correlation/checksum constraint for every
    /// non-calibration job. This column keeps the scope non-null so the constraint always applies.
    /// </remarks>
    [JsonIgnore]
    public Guid IdempotencyScopeId { get; set; }

    /// <summary>Soft reference to the calibration attempt that produced this job.</summary>
    public Guid? CalibrationAttemptId { get; set; }

    /// <summary>Soft reference to the durable calibration orchestration saga row.</summary>
    public Guid? CalibrationOrchestrationId { get; set; }

    /// <summary>Idempotency operation identifier supplied by the submitting caller.</summary>
    public Guid? OperationId { get; set; }

    /// <summary>Exact native upstream-Orca machine profile JSON delivered to the worker.</summary>
    [JsonIgnore]
    public string? MachineProfileJson { get; set; }

    /// <summary>Exact native upstream-Orca process profile JSON delivered to the worker.</summary>
    [JsonIgnore]
    public string? ProcessProfileJson { get; set; }

    /// <summary>Exact native upstream-Orca filament profile JSON delivered to the worker.</summary>
    [JsonIgnore]
    public string? FilamentProfileJson { get; set; }

    /// <summary>SHA-256 (hex) of <see cref="MachineProfileJson"/>.</summary>
    [MaxLength(64)]
    public string? MachineProfileSha256 { get; set; }

    /// <summary>SHA-256 (hex) of <see cref="ProcessProfileJson"/>.</summary>
    [MaxLength(64)]
    public string? ProcessProfileSha256 { get; set; }

    /// <summary>SHA-256 (hex) of <see cref="FilamentProfileJson"/>.</summary>
    [MaxLength(64)]
    public string? FilamentProfileSha256 { get; set; }

    /// <summary>Resolved machine profile identity.</summary>
    public Guid? MachineProfileId { get; set; }

    /// <summary>Resolved process profile identity.</summary>
    public Guid? ProcessProfileId { get; set; }

    /// <summary>Resolved filament profile identity.</summary>
    public Guid? FilamentProfileId { get; set; }

    /// <summary>Slicer distribution the job was pinned to (for example <c>upstream</c>).</summary>
    [MaxLength(64)]
    public string? SlicerDistribution { get; set; }

    /// <summary>Pinned slicer version the job requires (for example <c>2.3.1</c>).</summary>
    [MaxLength(64)]
    public string? SlicerVersion { get; set; }

    /// <summary>Pinned slicer container digest the job requires, when the deployment supplies one.</summary>
    [MaxLength(128)]
    public string? SlicerContainerDigest { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Comma-separated list of artifact IDs associated with this job at completion.
    /// </summary>
    public string? ArtifactIdsCsv { get; set; }

    /// <summary>
    /// Total number of artifacts produced for this job.
    /// </summary>
    public int? ArtifactsCount { get; set; }

    /// <summary>
    /// Aggregate size in bytes of all artifacts for this job.
    /// </summary>
    public long? ArtifactsTotalBytes { get; set; }

    /// <summary>
    /// Number of times this job has been retried after timing out or failing.
    /// </summary>
    public int RetryCount { get; set; }

    /// <summary>
    /// JSON-serialized model transform (rotation/scale) from the UI workspace.
    /// Format: {"rotation":[rx,ry,rz],"scale":[sx,sy,sz]} (radians, Y-up).
    /// </summary>
    public string? ModelTransformJson { get; set; }

    /// <summary>
    /// JSON array of per-extruder filament profile names for multi-toolhead printers.
    /// Stored as e.g. ["Generic PLA @System","Generic PETG @System"].
    /// Null for single-extruder jobs.
    /// </summary>
    public string? ExtruderFilamentProfileNamesJson { get; set; }

    /// <summary>
    /// JSON array of model file URLs for multi-model slice jobs.
    /// When populated, the worker downloads all listed models and passes them to the slicer CLI.
    /// Null or empty for single-model jobs (falls back to <see cref="ModelFileUrl"/>).
    /// </summary>
    public string? ModelFileUrlsJson { get; set; }

    /// <summary>
    /// JSON array of per-model transform strings for multi-model slice jobs.
    /// Each entry corresponds positionally to a URL in <see cref="ModelFileUrlsJson"/>.
    /// Format per entry: {"rotation":[rx,ry,rz],"scale":[sx,sy,sz],"position":[px,py,pz]}.
    /// Null for single-model jobs (falls back to <see cref="ModelTransformJson"/>).
    /// </summary>
    public string? ModelFileTransformsJson { get; set; }
}

/// <summary>
/// Job status constants.
/// </summary>
public static class SliceJobStatus
{
    public const string Queued = "Queued";
    public const string Processing = "Processing";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
    public const string Cancelled = "Cancelled";
}
