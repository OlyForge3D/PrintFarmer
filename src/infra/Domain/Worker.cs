using System;

namespace Farm.Infrastructure.Domain;

/// <summary>
/// Represents a worker node in the distributed slicing system
/// </summary>
public class Worker
{
    /// <summary>
    /// Unique identifier for the worker
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Service ID assigned by the registry API
    /// </summary>
    public string ServiceId { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable name for the worker
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Endpoint URL for communicating with the worker
    /// </summary>
    public string EndpointUrl { get; set; } = string.Empty;

    /// <summary>
    /// JSON array of worker capabilities (e.g., ["orcaslicer", "prusaslicer", "fast-slicing"])
    /// </summary>
    public string CapabilitiesJson { get; set; } = "[]";

    /// <summary>
    /// Current worker status
    /// </summary>
    public string Status { get; set; } = WorkerStatus.Offline;

    /// <summary>
    /// Number of available job slots (calculated as TotalSlots - ActiveJobs)
    /// </summary>
    public int FreeSlots => Math.Max(0, TotalSlots - ActiveJobs);

    /// <summary>
    /// Total job capacity
    /// </summary>
    public int TotalSlots { get; set; }

    /// <summary>
    /// Number of jobs currently being processed
    /// </summary>
    public int ActiveJobs { get; set; }

    /// <summary>
    /// Number of successfully completed jobs
    /// </summary>
    public int CompletedJobs { get; set; }

    /// <summary>
    /// Number of failed jobs
    /// </summary>
    public int FailedJobs { get; set; }

    /// <summary>
    /// Average processing time in seconds (rolling average)
    /// </summary>
    public double? AverageProcessingTimeSeconds { get; set; }

    /// <summary>
    /// Last time the worker sent a heartbeat
    /// </summary>
    public DateTime? LastHeartbeat { get; set; }

    /// <summary>
    /// When the worker was first registered
    /// </summary>
    public DateTime RegisteredAt { get; set; }

    /// <summary>
    /// When the worker went online
    /// </summary>
    public DateTime? OnlineAt { get; set; }

    /// <summary>
    /// When the worker went offline
    /// </summary>
    public DateTime? OfflineAt { get; set; }

    /// <summary>
    /// API key for authenticating worker requests (if required)
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Worker version/build information
    /// </summary>
    public string? Version { get; set; }

    /// <summary>
    /// Additional metadata (JSON)
    /// </summary>
    public string? MetadataJson { get; set; }

    /// <summary>
    /// When this record was created
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// When this record was last updated
    /// </summary>
    public DateTime UpdatedAt { get; set; }

    /// <summary>
    /// Whether the worker is manually disabled by an admin
    /// </summary>
    public bool IsDisabled { get; set; }

    /// <summary>
    /// Reason for disabling (if applicable)
    /// </summary>
    public string? DisabledReason { get; set; }

    /// <summary>
    /// Total number of artifacts (gcode, previews, logs) produced by this worker. Incremented on job completion.
    /// </summary>
    public int ArtifactsProduced { get; set; }

    /// <summary>
    /// Aggregate bytes written for produced artifacts (for capacity planning & monitoring).
    /// </summary>
    public long ArtifactBytesProduced { get; set; }
}

/// <summary>
/// Worker status constants
/// </summary>
public static class WorkerStatus
{
    /// <summary>
    /// Worker is offline (no recent heartbeat)
    /// </summary>
    public const string Offline = "Offline";

    /// <summary>
    /// Worker is online and available for jobs
    /// </summary>
    public const string Online = "Online";

    /// <summary>
    /// Worker is online but all slots are in use
    /// </summary>
    public const string Busy = "Busy";

    /// <summary>
    /// Worker is in an error state
    /// </summary>
    public const string Error = "Error";

    /// <summary>
    /// Worker is shutting down gracefully
    /// </summary>
    public const string Draining = "Draining";
}
