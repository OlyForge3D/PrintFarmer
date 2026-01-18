using System.Text.Json.Serialization;
using Farm.Infrastructure.Slicer.Messaging;

namespace Farm.Infrastructure;

/// <summary>
/// Extended status enum for distributed slicing jobs
/// Uses the existing SlicingJobStatus from Models.cs and adds Processing state
/// </summary>
public static class SlicingJobStatusExtensions
{
    /// <summary>
    /// Check if status indicates job is in progress
    /// </summary>
    public static bool IsInProgress(this SlicingJobStatus status)
    {
        return status == SlicingJobStatus.Slicing;
    }

    /// <summary>
    /// Check if status indicates job is complete
    /// </summary>
    public static bool IsComplete(this SlicingJobStatus status)
    {
        return status is SlicingJobStatus.Completed or SlicingJobStatus.Error or SlicingJobStatus.Cancelled;
    }
}

/// <summary>
/// Priority level for slicing jobs
/// </summary>
// Single JsonConverter attribute (duplicate removed)
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SlicingJobPriority
{
    Low = 0,
    Normal = 1,
    High = 2,
    Critical = 3
}

/// <summary>
/// Supported slicer engine types
/// </summary>
// Single JsonConverter attribute (duplicate removed)
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SlicerEngineType
{
    OrcaSlicer = 0,
    PrusaSlicer = 1,
    SuperSlicer = 2,
    Cura = 3
}

/// <summary>
/// Request to submit a new slicing job with message envelope for idempotency
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

    public Dictionary<string, object> Metadata { get; } = [];

    /// <summary>
    /// Message envelope for idempotency and tracking
    /// Optional - will be generated if not provided
    /// </summary>
    public MessageEnvelope? Envelope { get; set; }

    /// <summary>
    /// Get or create message envelope for this request
    /// </summary>
    /// <returns>Message envelope for idempotency</returns>
    public MessageEnvelope GetOrCreateEnvelope()
    {
        if (Envelope != null)
        {
            return Envelope;
        }

        SlicingJobContent jobContent = Slicer.Messaging.SlicingJobContent.FromRequest(this);
        return Slicer.Messaging.MessageEnvelope.Create(jobContent, SlicerEngine, Priority);
    }
}

/// <summary>
/// Response after submitting a slicing job
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
/// Extended slicing job for distributed processing 
/// Builds on existing SlicingJobDto with additional distributed processing fields and envelope support
/// </summary>
public class DistributedSlicingJob : SlicingJobDto
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }

    public Uri ModelFileUrl { get; set; } = new("about:blank", UriKind.RelativeOrAbsolute);

    public string ModelFileName { get; set; } = string.Empty;

    public SlicerEngineType EngineType { get; set; }

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

    public int RetryCount { get; set; } // default 0

    public DateTime? LastRetryAt { get; set; }

    public DateTime? ScheduledAt { get; set; } // Optional: when job becomes available for processing (for delayed retries)

    // Message envelope fields for idempotency
    public Guid CorrelationId { get; set; } = Guid.NewGuid();

    public string Checksum { get; set; } = string.Empty;

    public int Attempt { get; set; } = 1;

    public DateTime SubmittedAt { get; set; } = DateTime.UtcNow;

    public string EnvelopeVersion { get; set; } = Slicer.Messaging.MessageEnvelope.CurrentVersion;

    /// <summary>
    /// Create distributed job from request with envelope
    /// </summary>
    /// <param name="request">Slicing job request</param>
    /// <param name="envelope">Message envelope</param>
    /// <returns>Distributed slicing job</returns>
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
            Status = SlicingJobStatus.Queued,
            CreatedAt = DateTime.UtcNow,
            CorrelationId = envelope.CorrelationId,
            Checksum = envelope.Checksum,
            Attempt = envelope.Attempt,
            SubmittedAt = envelope.SubmittedAt,
            EnvelopeVersion = envelope.Version
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
    /// Get message envelope from job fields
    /// </summary>
    /// <returns>Message envelope</returns>
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
            Version = EnvelopeVersion
        };
    }

    // Map from base SlicingJobDto for compatibility
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
/// Status response for a slicing job
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
/// Result of a slicing operation
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

    public Dictionary<string, string> Metadata { get; } = [];
}

/// <summary>
/// Health check response for slicer services
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
/// Configuration for a slicer service worker
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
    public string TempDirectory { get; set; } = Path.Combine(Directory.GetCurrentDirectory(), "temp");

    public long MaxFileSizeBytes { get; set; } = 100_000_000; // 100MB

    public double JitterPercent { get; set; } = 15.0; // Percent (+/-) applied to retry backoff range (e.g., 15 = +/-15%)

    public Dictionary<string, object> SlicerSpecificSettings { get; } = [];

    public static SlicerWorkerConfiguration WithTempDirectory(string tempRoot, Action<SlicerWorkerConfiguration>? configure = null)
    {
        SlicerWorkerConfiguration cfg = new()
        { TempDirectory = tempRoot };
        configure?.Invoke(cfg);
        return cfg;
    }
}

/// <summary>
/// Progress update for real-time slicing status
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
/// Metrics for monitoring slicer performance
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
