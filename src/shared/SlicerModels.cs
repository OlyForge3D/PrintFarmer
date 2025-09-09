using System.Text.Json.Serialization;

namespace Farm.Web.Shared;

/// <summary>
/// Extended status enum for distributed slicing jobs
/// Uses the existing SlicingJobStatus from Models.cs and adds Processing state
/// </summary>
public static class SlicingJobStatusExtensions 
{
    /// <summary>
    /// Convert legacy status to new processing status
    /// </summary>
    public static SlicingJobStatus ToProcessingStatus(this SlicingJobStatus status)
    {
        return status switch
        {
            SlicingJobStatus.Slicing => SlicingJobStatus.Slicing, // Processing equivalent
            _ => status
        };
    }
    
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
    public string ModelFileUrl { get; set; } = string.Empty;
    public string ModelFileName { get; set; } = string.Empty;
    public SlicerEngineType SlicerEngine { get; set; } = SlicerEngineType.OrcaSlicer;
    public SlicerProfileDto SlicerProfile { get; set; } = new();
    public SlicingJobPriority Priority { get; set; } = SlicingJobPriority.Normal;
    public Dictionary<string, object> Metadata { get; set; } = [];

    /// <summary>
    /// Message envelope for idempotency and tracking
    /// Optional - will be generated if not provided
    /// </summary>
    public Slicer.Messaging.MessageEnvelope? Envelope { get; set; }

    /// <summary>
    /// Get or create message envelope for this request
    /// </summary>
    /// <returns>Message envelope for idempotency</returns>
    public Slicer.Messaging.MessageEnvelope GetOrCreateEnvelope()
    {
        if (Envelope != null) return Envelope;

        var jobContent = Slicer.Messaging.SlicingJobContent.FromRequest(this);
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
    public string SlicerWorkerUrl { get; set; } = string.Empty;
}

/// <summary>
/// Extended slicing job for distributed processing 
/// Builds on existing SlicingJobDto with additional distributed processing fields and envelope support
/// </summary>
public class DistributedSlicingJob : SlicingJobDto
{
    public new Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string ModelFileUrl { get; set; } = string.Empty;
    public string ModelFileName { get; set; } = string.Empty;
    public SlicerEngineType SlicerEngine { get; set; }
    public SlicingJobPriority Priority { get; set; } = SlicingJobPriority.Normal;
    
    // Extended distributed processing fields
    public DateTime? StartedAt { get; set; }
    public string? WorkerId { get; set; }
    public string? ErrorMessage { get; set; }
    public string? ResultFileUrl { get; set; }
    public long? InputFileSizeBytes { get; set; }
    public long? OutputFileSizeBytes { get; set; }
    public double EstimatedPrintTimeSeconds { get; set; }
    public double EstimatedFilamentUsageGrams { get; set; }
    public Dictionary<string, object> Metadata { get; set; } = [];
    public int RetryCount { get; set; } = 0;
    public DateTime? LastRetryAt { get; set; }

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
    public static DistributedSlicingJob FromRequest(SlicingJobRequest request, Slicer.Messaging.MessageEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(envelope);

        return new DistributedSlicingJob
        {
            Id = envelope.JobId,
            UserId = request.UserId,
            PrinterId = request.PrinterId,
            ModelFileUrl = request.ModelFileUrl,
            ModelFileName = request.ModelFileName,
            SlicerEngine = request.SlicerEngine,
            Profile = request.SlicerProfile,
            Priority = request.Priority,
            Status = SlicingJobStatus.Queued,
            CreatedAt = DateTime.UtcNow,
            Metadata = request.Metadata,
            CorrelationId = envelope.CorrelationId,
            Checksum = envelope.Checksum,
            Attempt = envelope.Attempt,
            SubmittedAt = envelope.SubmittedAt,
            EnvelopeVersion = envelope.Version
        };
    }

    /// <summary>
    /// Get message envelope from job fields
    /// </summary>
    /// <returns>Message envelope</returns>
    public Slicer.Messaging.MessageEnvelope GetEnvelope()
    {
        return new Slicer.Messaging.MessageEnvelope
        {
            JobId = Id,
            SlicerType = SlicerEngine,
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
        JobId = baseJob.JobId;
        Status = baseJob.Status;
        Progress = baseJob.Progress;
        SlicerEngine = Enum.TryParse<SlicerEngineType>(baseJob.SlicerEngine, true, out var engine) ? engine : SlicerEngineType.OrcaSlicer;
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

    // Convert to base SlicingJobDto for legacy compatibility  
    public SlicingJobDto ToBase()
    {
        return new SlicingJobDto
        {
            JobId = JobId,
            Status = Status,
            Progress = Progress,
            Message = ErrorMessage,
            SlicerEngine = SlicerEngine.ToString(),
            PrinterId = PrinterId,
            ModelFilePath = ModelFileUrl, // Legacy field mapping
            GcodeFilePath = ResultFileUrl,
            Profile = Profile,
            CreatedAt = CreatedAt,
            CompletedAt = CompletedAt,
            EstimatedPrintTime = (int?)EstimatedPrintTimeSeconds,
            EstimatedFilamentUsed = EstimatedFilamentUsageGrams,
            LayerCount = LayerCount
        };
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
    public string? ResultFileUrl { get; set; }
    public double EstimatedPrintTimeSeconds { get; set; }
    public double EstimatedFilamentUsageGrams { get; set; }
    public int LayerCount { get; set; }
    public Dictionary<string, object> Metadata { get; set; } = [];
}

/// <summary>
/// Result of a slicing operation
/// </summary>
public class SlicingResult
{
    public bool Success { get; set; }
    public string? ResultFileUrl { get; set; }
    public string? Output { get; set; }
    public string? Error { get; set; }
    public double ProcessingTimeSeconds { get; set; }
    public long? OutputFileSizeBytes { get; set; }
    public double EstimatedPrintTimeSeconds { get; set; }
    public double EstimatedFilamentUsageGrams { get; set; }
    public int LayerCount { get; set; }
    public Dictionary<string, string> Metadata { get; set; } = [];
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
    public Dictionary<string, object> Details { get; set; } = [];
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
    public Dictionary<string, object> SlicerSpecificSettings { get; set; } = [];

    public static SlicerWorkerConfiguration WithTempDirectory(string tempRoot, Action<SlicerWorkerConfiguration>? configure = null)
    {
        var cfg = new SlicerWorkerConfiguration { TempDirectory = tempRoot };
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
    public Dictionary<string, object> AdditionalData { get; set; } = [];
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