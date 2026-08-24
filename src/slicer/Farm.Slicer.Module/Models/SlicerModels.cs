using System.Text.Json.Serialization;
using Farm.Slicer.Module.Dtos;
using Farm.Slicer.Module.Messaging;

namespace Farm.Slicer.Module.Models;

/// <summary>
/// Extended status enum for distributed slicing jobs.
/// Uses the existing SlicingJobStatus from Dtos and adds convenience extensions.
/// </summary>
public static class SlicingJobStatusExtensions
{
    /// <summary>
    /// Check if status indicates job is in progress.
    /// </summary>
    /// <param name="status">The slicing job status to check.</param>
    /// <returns><c>true</c> if the job is actively being sliced.</returns>
    public static bool IsInProgress(this SlicingJobStatus status)
    {
        return status == SlicingJobStatus.Slicing;
    }

    /// <summary>
    /// Check if status indicates job is complete.
    /// </summary>
    /// <param name="status">The slicing job status to check.</param>
    /// <returns><c>true</c> if the job has reached a terminal state.</returns>
    public static bool IsComplete(this SlicingJobStatus status)
    {
        return status is SlicingJobStatus.Completed or SlicingJobStatus.Error or SlicingJobStatus.Cancelled;
    }
}

/// <summary>
/// Priority level for slicing jobs.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SlicingJobPriority
{
    /// <summary>Low priority — processed after all higher-priority jobs.</summary>
    Low = 0,

    /// <summary>Normal (default) priority.</summary>
    Normal = 1,

    /// <summary>High priority — expedited processing.</summary>
    High = 2,

    /// <summary>Critical priority — processed immediately.</summary>
    Critical = 3,
}

/// <summary>
/// Supported slicer engine types.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SlicerEngineType
{
    /// <summary>OrcaSlicer engine.</summary>
    OrcaSlicer = 0,

    /// <summary>PrusaSlicer engine.</summary>
    PrusaSlicer = 1,

    /// <summary>SuperSlicer engine.</summary>
    SuperSlicer = 2,

    /// <summary>Cura engine.</summary>
    Cura = 3,
}

#pragma warning disable SA1402 // File may only contain a single type

/// <summary>
/// Request to submit a new slicing job with message envelope for idempotency.
/// </summary>
public class SlicingJobRequest
{
    public Guid UserId { get; set; }

    public Guid PrinterId { get; set; }

    public Uri ModelFileUrl { get; set; } = new("about:blank", UriKind.RelativeOrAbsolute);

    public string ModelFileName { get; set; } = string.Empty;

    public SlicerEngineType SlicerEngine { get; set; } = SlicerEngineType.OrcaSlicer;

    public SlicerProfileDto SlicerProfile { get; set; } = new();

    public SlicingJobPriority Priority { get; set; } = SlicingJobPriority.Normal;

    /// <summary>Optional: Plate index to slice from a multi-plate 3MF model.</summary>
    public int? PlateIndex { get; set; }

    public Dictionary<string, object> Metadata { get; } = [];

    /// <summary>
    /// Message envelope for idempotency and tracking.
    /// Optional — will be generated if not provided.
    /// </summary>
    public MessageEnvelope? Envelope { get; set; }

    /// <summary>
    /// Get or create message envelope for this request.
    /// </summary>
    /// <returns>Message envelope for idempotency.</returns>
    public MessageEnvelope GetOrCreateEnvelope()
    {
        if (Envelope != null)
        {
            return Envelope;
        }

        SlicingJobContent jobContent = SlicingJobContent.FromRequest(this);
        return MessageEnvelope.Create(jobContent, SlicerEngine, Priority);
    }
}

/// <summary>
/// Response after submitting a slicing job.
/// </summary>
public class SlicingJobResponse
{
    public Guid JobId { get; set; }

    public SlicingJobStatus Status { get; set; }

    public DateTime EstimatedCompletionTime { get; set; }

    public int QueuePosition { get; set; }

    public Uri SlicerWorkerUrl { get; set; } = new("about:blank", UriKind.RelativeOrAbsolute);
}

/// <summary>
/// Extended slicing job for distributed processing.
/// Builds on existing SlicingJobDto with additional distributed processing fields and envelope support.
/// </summary>
public class DistributedSlicingJob : SlicingJobDto
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Opaque identifier for the active worker claim incarnation.</summary>
    public Guid ClaimToken { get; set; }

    public Uri ModelFileUrl { get; set; } = new("about:blank", UriKind.RelativeOrAbsolute);

    public string ModelFileName { get; set; } = string.Empty;

    public SlicerEngineType EngineType { get; set; }

    /// <summary>
    /// Raw named-profile selection delivered by the API for worker-local resolution.
    /// </summary>
    public string? SlicerProfileJson { get; set; }

    /// <summary>
    /// Exact native slicer profile documents plus digests, delivered with the claim.
    /// </summary>
    public NativeSlicerProfiles? NativeProfiles { get; set; }

    /// <summary>SHA-256 of the effective native machine profile passed to the slicer.</summary>
    public string? MachineProfileSha256 { get; set; }

    /// <summary>SHA-256 of the effective native process profile passed to the slicer.</summary>
    public string? ProcessProfileSha256 { get; set; }

    /// <summary>SHA-256 of the effective native filament profile set passed to the slicer.</summary>
    public string? FilamentProfileSha256 { get; set; }

    /// <summary>SHA-256 (hex) of the stored model bytes, verified after download.</summary>
    public string? ModelSha256 { get; set; }

    /// <summary>Lease token that must accompany every mutation for this job.</summary>
    public Guid LeaseToken { get; set; }

    /// <summary>Fencing counter that must accompany every mutation for this job.</summary>
    public long LeaseFence { get; set; }

    public SlicingJobPriority Priority { get; set; } = SlicingJobPriority.Normal;

    // Extended distributed processing fields
    public DateTime? StartedAt { get; set; }

    public string? WorkerId { get; set; }

    public string? ErrorMessage { get; set; }

    public Uri? ResultFileUrl { get; set; }

    public long? InputFileSizeBytes { get; set; }

    public long? OutputFileSizeBytes { get; set; }

    public double EstimatedPrintTimeSeconds { get; set; }

    public double EstimatedFilamentUsageGrams { get; set; }

    public Dictionary<string, object> Metadata { get; } = [];

    public int RetryCount { get; set; }

    public DateTime? LastRetryAt { get; set; }

    public DateTime? ScheduledAt { get; set; } // Optional: when job becomes available for processing (for delayed retries)

    /// <summary>Optional: Plate index to slice from a multi-plate 3MF model.</summary>
    public int? PlateIndex { get; set; }

    /// <summary>
    /// JSON-serialized model transform (rotation/scale) from the UI workspace.
    /// </summary>
    public string? ModelTransformJson { get; set; }

    /// <summary>
    /// Multiple model file URLs for multi-model slice jobs.
    /// When populated, the worker downloads all listed models and passes them to the slicer CLI.
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
    /// Calibration method wire name (issue #1938, see <see cref="CalibrationMethods"/>), or
    /// <see langword="null"/> for an ordinary slice.
    /// </summary>
    public string? CalibrationMethod { get; set; }

    /// <summary>JSON-serialized numeric parameters for <see cref="CalibrationMethod"/>.</summary>
    public string? CalibrationParamsJson { get; set; }

    // Message envelope fields for idempotency
    public Guid CorrelationId { get; set; } = Guid.NewGuid();

    public string Checksum { get; set; } = string.Empty;

    public int Attempt { get; set; } = 1;

    public DateTime SubmittedAt { get; set; } = DateTime.UtcNow;

    public string EnvelopeVersion { get; set; } = MessageEnvelope.CurrentVersion;

    /// <summary>
    /// Create distributed job from request with envelope.
    /// </summary>
    /// <param name="request">Slicing job request containing job parameters.</param>
    /// <param name="envelope">Message envelope for idempotency and tracking.</param>
    /// <returns>Distributed slicing job.</returns>
    public static DistributedSlicingJob FromRequest(SlicingJobRequest request, MessageEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(envelope);

        DistributedSlicingJob job = new()
        {
            Id = envelope.JobId,
            UserId = request.UserId,
            PrinterId = request.PrinterId,
            ModelFileUrl = request.ModelFileUrl,
            ModelFileName = request.ModelFileName,
            EngineType = request.SlicerEngine,
            SlicerEngine = request.SlicerEngine.ToString(),
            Profile = request.SlicerProfile,
            Priority = request.Priority,
            PlateIndex = request.PlateIndex,
            Status = SlicingJobStatus.Queued,
            CreatedAt = DateTime.UtcNow,
            CorrelationId = envelope.CorrelationId,
            Checksum = envelope.Checksum,
            Attempt = envelope.Attempt,
            SubmittedAt = envelope.SubmittedAt,
            EnvelopeVersion = envelope.Version,
        };

        if (request.Metadata?.Count > 0)
        {
            foreach (KeyValuePair<string, object> kv in request.Metadata)
            {
                job.Metadata[kv.Key] = kv.Value;
            }
        }

        return job;
    }

    /// <summary>
    /// Get message envelope from job fields.
    /// </summary>
    /// <returns>Message envelope.</returns>
    public MessageEnvelope CreateEnvelope()
    {
        return new MessageEnvelope
        {
            JobId = Id,
            SlicerType = EngineType,
            Priority = Priority,
            Attempt = Attempt,
            CorrelationId = CorrelationId,
            Checksum = Checksum,
            SubmittedAt = SubmittedAt,
            Version = EnvelopeVersion,
        };
    }

    /// <summary>
    /// Map from base SlicingJobDto for compatibility.
    /// </summary>
    /// <param name="baseJob">The base job DTO to update from.</param>
    public void UpdateFromBase(SlicingJobDto baseJob)
    {
        ArgumentNullException.ThrowIfNull(baseJob);
        JobId = baseJob.JobId;
        Status = baseJob.Status;
        Progress = baseJob.Progress;
        EngineType = Enum.TryParse(baseJob.SlicerEngine, true, out SlicerEngineType engine) ? engine : SlicerEngineType.OrcaSlicer;
        PrinterId = baseJob.PrinterId;
        ModelFilePath = baseJob.ModelFilePath;
        GcodeFilePath = baseJob.GcodeFilePath;
        Profile = baseJob.Profile;
        CreatedAt = baseJob.CreatedAt;
        CompletedAt = baseJob.CompletedAt;
        EstimatedPrintTime = baseJob.EstimatedPrintTime;
        EstimatedFilamentUsed = baseJob.EstimatedFilamentUsed;
        LayerCount = baseJob.LayerCount;
    }
}

/// <summary>
/// Status response for a slicing job.
/// </summary>
public class SlicingJobStatusResponse
{
    public Guid JobId { get; set; }

    public SlicingJobStatus Status { get; set; }

    public int Progress { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? StartedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    public string? WorkerId { get; set; }

    public string? ErrorMessage { get; set; }

    public Uri? ResultFileUrl { get; set; }

    public double EstimatedPrintTimeSeconds { get; set; }

    public double EstimatedFilamentUsageGrams { get; set; }

    public int LayerCount { get; set; }

    public Dictionary<string, object> Metadata { get; } = [];

    // Retry & scheduling metadata
    public int RetryCount { get; set; }

    public DateTime? ScheduledAt { get; set; }
}

/// <summary>
/// Redacted, client-safe reason the requested model layout was dropped or altered during
/// slicing (issue #1800). This is a small, explicitly-modelled signal — never the raw worker
/// diagnostic text (compare <see cref="Contracts.SliceJobStatusResponse.ErrorDetail"/>, which is
/// intentionally admin-only and verbatim). <see langword="null"/> means the requested layout (if
/// any) was fully honoured.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LayoutDegradationReason
{
    /// <summary>
    /// The requested position/layout could not be embedded in a 3MF project (inputs were not
    /// all STL, the bed centre could not be determined, or the project failed to build), so
    /// OrcaSlicer auto-arranged the model(s) instead.
    /// </summary>
    LayoutNotEmbedded,

    /// <summary>
    /// The inputs were 3MF, which already carries its own placement and cannot be re-embedded,
    /// so the workspace's requested layout was dropped in favor of the placement already stored
    /// in the source file.
    /// </summary>
    SourcePlacementFallback,
}

/// <summary>
/// Redacted, client-safe classification of why a slice job failed (issue #1811), derived by the
/// worker from the slicer's own exit code. Like <see cref="LayoutDegradationReason"/> this is a
/// small, explicitly-modelled signal and never the raw worker diagnostic text — compare
/// <see cref="Contracts.SliceJobStatusResponse.ErrorDetail"/>, which stays admin-only and verbatim
/// because it can contain worker container paths, model filenames and CLI arguments.
/// </summary>
/// <remarks>
/// Redaction here is guaranteed structurally, not by remembering to sanitize: the only values that
/// can ever reach a caller are these enum members and the fixed English strings
/// <see cref="SliceFailureHints"/> maps them to. No job-derived text is carried on this channel, so
/// there is no path by which a filename or container path could leak through it.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SliceFailureReason
{
    /// <summary>
    /// The slicing engine rejected the model itself (OrcaSlicer <c>CLI_SLICING_ERROR</c>, -100).
    /// A generic catch-all on the engine's side: the model's geometry is usually valid, and one
    /// common trigger is an orientation the engine cannot slice.
    /// </summary>
    SlicingEngineRejectedModel,

    /// <summary>
    /// Nothing printable was found on the plate (<c>CLI_NO_SUITABLE_OBJECTS</c>, -50, or
    /// <c>CLI_NO_SUITABLE_OBJECTS_AFTER_SKIP</c>, -60).
    /// </summary>
    NoPrintableObjects,

    /// <summary>Part of the model lies outside the build volume (<c>CLI_OBJECTS_PARTLY_INSIDE</c>, -52).</summary>
    ModelOutsideBuildVolume,

    /// <summary>
    /// The selected process or filament is not compatible with the selected printer
    /// (<c>CLI_PROCESS_NOT_COMPATIBLE</c> -17, <c>CLI_FILAMENT_NOT_MATCH_BED_TYPE</c> -61,
    /// <c>CLI_FILAMENTS_DIFFERENT_TEMP</c> -62).
    /// </summary>
    ProfileNotCompatible,

    /// <summary>A slicing profile could not be read or contained invalid values.</summary>
    ProfileInvalid,

    /// <summary>The model file could not be found or parsed by the slicing engine.</summary>
    ModelFileUnreadable,

    /// <summary>The model exceeds the engine's complexity or memory limits.</summary>
    ModelTooComplex,

    /// <summary>Slicing exceeded the engine's time limit.</summary>
    SlicingTimedOut,

    /// <summary>Objects or toolpaths collide (sequential/by-layer printing or G-code conflicts).</summary>
    ToolpathConflict,

    /// <summary>
    /// The slicing engine failed for a reason this system does not classify. Farm admins can read
    /// <see cref="Contracts.SliceJobStatusResponse.ErrorDetail"/> for the verbatim diagnostic.
    /// </summary>
    SlicerFailed,
}

/// <summary>
/// Fixed, client-safe guidance for each <see cref="SliceFailureReason"/> (issue #1811). Every value
/// is a compile-time constant string: nothing job-derived is interpolated, which is what makes the
/// hint channel safe to show to a non-admin caller.
/// </summary>
public static class SliceFailureHints
{
    /// <summary>
    /// Names the existing "Auto-Orient" and "Lay Flat (F)" controls (the model tool rail buttons in
    /// the slicer workspace's <c>SlicerToolbar</c>, where the strings are the buttons' <c>title</c>
    /// tooltips). Those buttons act on the selected model, always show regardless of how many plates
    /// exist, and resolve the most common cause of
    /// <see cref="SliceFailureReason.SlicingEngineRejectedModel"/> (a model authored on its side,
    /// which the engine then cannot slice standing up) — verified in issue #1811 against the real
    /// OrcaSlicer 2.4.2 CLI, where every affected model sliced cleanly after auto-orienting.
    /// Deliberately phrased as a likely cause, not a diagnosis: -100 is a generic engine catch-all,
    /// so a confident "reorient your model" would misdirect callers whose job failed for an
    /// unrelated reason. See issue #1962: an earlier revision instead named a plate-level
    /// "Auto-orient plate" control (<c>PlateBedOverlay</c>) that only renders once a second plate is
    /// added, so a single-plate job like the one that reproduced #1962 never sees it, and wrongly
    /// blamed a plate lock that had nothing to do with the failure.
    /// </summary>
    public const string SlicingEngineRejectedModel =
        "The slicing engine could not slice this model. This most often happens when a model sits " +
        "in an orientation the engine cannot handle — select the model and try the \"Auto-Orient\" " +
        "or \"Lay Flat\" button in the model tools in the slicer workspace, then slice again. If it " +
        "still fails, ask a farm admin to check the job's error detail.";

    /// <summary>Guidance for <see cref="SliceFailureReason.NoPrintableObjects"/>.</summary>
    public const string NoPrintableObjects =
        "The plate had nothing printable on it. Check that the model is on the plate and fully " +
        "inside the build area, then slice again.";

    /// <summary>Guidance for <see cref="SliceFailureReason.ModelOutsideBuildVolume"/>.</summary>
    public const string ModelOutsideBuildVolume =
        "Part of the model is outside the printer's build volume. Move, rotate or scale it to fit " +
        "the plate, then slice again.";

    /// <summary>Guidance for <see cref="SliceFailureReason.ProfileNotCompatible"/>.</summary>
    public const string ProfileNotCompatible =
        "The selected process or filament profile is not compatible with the selected printer. " +
        "Pick a profile intended for this printer and slice again.";

    /// <summary>Guidance for <see cref="SliceFailureReason.ProfileInvalid"/>.</summary>
    public const string ProfileInvalid =
        "A slicing profile could not be read or contained invalid values. Pick a different profile, " +
        "or ask a farm admin to check the printer's profiles.";

    /// <summary>Guidance for <see cref="SliceFailureReason.ModelFileUnreadable"/>.</summary>
    public const string ModelFileUnreadable =
        "The slicing engine could not read this model file. Re-upload the model and try again.";

    /// <summary>Guidance for <see cref="SliceFailureReason.ModelTooComplex"/>.</summary>
    public const string ModelTooComplex =
        "The model is too large or too detailed for the slicing engine. Simplify or reduce its " +
        "geometry resolution, or use a larger layer height, then slice again.";

    /// <summary>Guidance for <see cref="SliceFailureReason.SlicingTimedOut"/>.</summary>
    public const string SlicingTimedOut =
        "Slicing took longer than the engine allows. Simplify the model or use a larger layer " +
        "height, then slice again.";

    /// <summary>Guidance for <see cref="SliceFailureReason.ToolpathConflict"/>.</summary>
    public const string ToolpathConflict =
        "The slicing engine found colliding objects or toolpaths. Move the models further apart on " +
        "the plate and slice again.";

    /// <summary>Guidance for <see cref="SliceFailureReason.SlicerFailed"/>.</summary>
    public const string SlicerFailed =
        "Slicing failed inside the slicing engine. Ask a farm admin to check the job's error detail.";

    /// <summary>
    /// Returns the fixed hint for <paramref name="reason"/>, or <see langword="null"/> when the
    /// value is not a defined member (so an unknown value persisted by an older or newer worker can
    /// never be echoed back to a caller as if it were guidance).
    /// </summary>
    /// <param name="reason">The classified failure reason.</param>
    /// <returns>A constant hint string, or <see langword="null"/>.</returns>
    public static string? For(SliceFailureReason reason) => reason switch
    {
        SliceFailureReason.SlicingEngineRejectedModel => SlicingEngineRejectedModel,
        SliceFailureReason.NoPrintableObjects => NoPrintableObjects,
        SliceFailureReason.ModelOutsideBuildVolume => ModelOutsideBuildVolume,
        SliceFailureReason.ProfileNotCompatible => ProfileNotCompatible,
        SliceFailureReason.ProfileInvalid => ProfileInvalid,
        SliceFailureReason.ModelFileUnreadable => ModelFileUnreadable,
        SliceFailureReason.ModelTooComplex => ModelTooComplex,
        SliceFailureReason.SlicingTimedOut => SlicingTimedOut,
        SliceFailureReason.ToolpathConflict => ToolpathConflict,
        SliceFailureReason.SlicerFailed => SlicerFailed,
        _ => null,
    };
}

/// <summary>
/// Result of a slicing operation.
/// </summary>
public class SlicingResult
{
    public bool Success { get; set; }

    public Uri? ResultFileUrl { get; set; }

    public string? Output { get; set; }

    public string? Error { get; set; }

    public double ProcessingTimeSeconds { get; set; }

    public long? OutputFileSizeBytes { get; set; }

    public double EstimatedPrintTimeSeconds { get; set; }

    public double EstimatedFilamentUsageGrams { get; set; }

    public int LayerCount { get; set; }

    /// <summary>
    /// Set when the requested model layout was dropped or altered because it could not be
    /// honoured (issue #1800). <see langword="null"/> when nothing was dropped.
    /// </summary>
    public LayoutDegradationReason? LayoutDegradation { get; set; }

    public Dictionary<string, string> Metadata { get; } = [];
}

/// <summary>
/// Health check response for slicer services.
/// </summary>
public class SlicerHealthCheckResponse
{
    public string Status { get; set; } = "unknown";

    public string? Version { get; set; }

    public int ActiveJobs { get; set; }

    public long QueueDepth { get; set; }

    public double CpuUsage { get; set; }

    public long AvailableMemoryMB { get; set; }

    public DateTime? LastJobCompletedAt { get; set; }

    public bool IsHealthy { get; set; }

    public Dictionary<string, object> Details { get; } = [];
}

/// <summary>
/// Configuration for a slicer service worker.
/// </summary>
public class SlicerWorkerConfiguration
{
    public string WorkerId { get; set; } = Environment.MachineName + "-" + Guid.NewGuid().ToString("N")[..8];

    public SlicerEngineType SlicerEngine { get; set; }

    public int MaxConcurrentJobs { get; set; } = Environment.ProcessorCount;

    public int MaxRetryCount { get; set; } = 3;

    public TimeSpan JobTimeout { get; set; } = TimeSpan.FromMinutes(60);

    public TimeSpan HealthCheckInterval { get; set; } = TimeSpan.FromSeconds(30);

    // Temp directory is now expected to be injected / set explicitly via WithTempDirectory or composition root.
    // Default falls back to current working directory /temp to avoid system global temp (macOS TCC prompts).
    public string TempDirectory { get; set; } = Path.Join(Directory.GetCurrentDirectory(), "temp");

    public long MaxFileSizeBytes { get; set; } = 100_000_000; // 100MB

    public double JitterPercent { get; set; } = 15.0; // Percent (+/-) applied to retry backoff range (e.g., 15 = +/-15%)

    public Dictionary<string, object> SlicerSpecificSettings { get; } = [];

    /// <summary>
    /// Creates a configuration with the specified temp directory.
    /// </summary>
    /// <param name="tempRoot">Root path for temporary files.</param>
    /// <param name="configure">Optional action to further configure the instance.</param>
    /// <returns>A new <see cref="SlicerWorkerConfiguration"/> instance.</returns>
    public static SlicerWorkerConfiguration WithTempDirectory(string tempRoot, Action<SlicerWorkerConfiguration>? configure = null)
    {
        SlicerWorkerConfiguration cfg = new()
        { TempDirectory = tempRoot };
        configure?.Invoke(cfg);
        return cfg;
    }
}

/// <summary>
/// Progress update for real-time slicing status.
/// </summary>
public class SlicingProgressUpdate
{
    public Guid JobId { get; set; }

    public int Progress { get; set; }

    public SlicingJobStatus Status { get; set; }

    public string? CurrentStep { get; set; }

    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    public Dictionary<string, object> AdditionalData { get; } = [];
}

/// <summary>
/// Metrics for monitoring slicer performance.
/// </summary>
public class SlicerMetrics
{
    public string WorkerId { get; set; } = string.Empty;

    public SlicerEngineType SlicerEngine { get; set; }

    public int TotalJobsProcessed { get; set; }

    public int SuccessfulJobs { get; set; }

    public int FailedJobs { get; set; }

    public double AverageProcessingTimeSeconds { get; set; }

    public double TotalProcessingTimeSeconds { get; set; }

    public long TotalBytesProcessed { get; set; }

    public DateTime StartTime { get; set; }

    public DateTime LastJobCompletedAt { get; set; }

    public double CpuUsagePercentage { get; set; }

    public long MemoryUsageMB { get; set; }
}

#pragma warning restore SA1402
