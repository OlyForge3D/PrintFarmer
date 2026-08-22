using System.Text.Json.Serialization;

namespace Farm.Slicer.Module.Domain;

/// <summary>
/// Represents a worker node in the distributed slicing system.
/// </summary>
public class Worker
{
    public Guid Id { get; set; }

    /// <summary>
    /// Service ID assigned by the registry API.
    /// </summary>
    public string ServiceId { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable name for the worker.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Endpoint URL for communicating with the worker.
    /// </summary>
    [JsonIgnore]
    public string EndpointUrl { get; set; } = string.Empty;

    /// <summary>
    /// JSON array of worker capabilities (e.g., ["orcaslicer", "prusaslicer", "fast-slicing"]).
    /// </summary>
    [JsonIgnore]
    public string CapabilitiesJson { get; set; } = "[]";

    /// <summary>
    /// Current worker status.
    /// </summary>
    public string Status { get; set; } = WorkerStatus.Offline;

    /// <summary>
    /// Number of available job slots (calculated as TotalSlots - ActiveJobs).
    /// </summary>
    public int FreeSlots => Math.Max(0, TotalSlots - ActiveJobs);

    /// <summary>
    /// Total job capacity.
    /// </summary>
    public int TotalSlots { get; set; }

    /// <summary>
    /// Number of jobs currently being processed.
    /// </summary>
    public int ActiveJobs { get; set; }

    /// <summary>
    /// Number of successfully completed jobs.
    /// </summary>
    public int CompletedJobs { get; set; }

    /// <summary>
    /// Number of failed jobs.
    /// </summary>
    public int FailedJobs { get; set; }

    /// <summary>
    /// Average processing time in seconds (rolling average).
    /// </summary>
    public double? AverageProcessingTimeSeconds { get; set; }

    /// <summary>
    /// Last time the worker sent a heartbeat.
    /// </summary>
    public DateTime? LastHeartbeat { get; set; }

    /// <summary>
    /// When the worker was first registered.
    /// </summary>
    public DateTime RegisteredAt { get; set; }

    /// <summary>
    /// When the worker went online.
    /// </summary>
    public DateTime? OnlineAt { get; set; }

    /// <summary>
    /// When the worker went offline.
    /// </summary>
    public DateTime? OfflineAt { get; set; }

    /// <summary>
    /// API key for authenticating worker requests (if required).
    /// </summary>
    [JsonIgnore]
    public string? ApiKey { get; set; }

    /// <summary>
    /// Worker version/build information.
    /// </summary>
    public string? Version { get; set; }

    /// <summary>
    /// Additional metadata (JSON).
    /// </summary>
    [JsonIgnore]
    public string? MetadataJson { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    /// <summary>
    /// Whether the worker is manually disabled by an admin.
    /// </summary>
    public bool IsDisabled { get; set; }

    /// <summary>
    /// Reason for disabling (if applicable).
    /// </summary>
    public string? DisabledReason { get; set; }

    /// <summary>
    /// Total number of artifacts produced by this worker.
    /// </summary>
    public int ArtifactsProduced { get; set; }

    /// <summary>
    /// Aggregate bytes written for produced artifacts (for capacity planning and monitoring).
    /// </summary>
    public long ArtifactBytesProduced { get; set; }
}

/// <summary>
/// Worker status constants.
/// </summary>
public static class WorkerStatus
{
    /// <summary>
    /// Maximum age of a heartbeat before a worker is excluded from live dispatch reads.
    /// </summary>
    public const int OnlineFreshnessSeconds = 60;

    /// <summary>
    /// Maximum age of a <c>SlicerService</c> row's <c>LastSeen</c> heartbeat before it
    /// stops counting as "configured" for <c>GET /api/slicers/engines</c> (issue #1812).
    /// A worker removed from a deployment (container deleted, feature flag disabled,
    /// etc.) never explicitly deregisters, so its row is otherwise immortal and keeps a
    /// dead version reported as "configured but offline" forever. Seven days is far
    /// longer than <see cref="OnlineFreshnessSeconds"/> so a worker that is merely
    /// restarting, mid-deploy, or down over a long weekend is never mistaken for
    /// reaped — only a row that has not heartbeated in a week, which realistically
    /// means the worker is gone, ages out of the "configured" set. This is a read-time
    /// filter only: rows are never deleted, so the fresh-install/legacy fallback (zero
    /// rows exist) and the "engine has zero configured versions" whole-group fallback
    /// in <c>SlicersController.ListEnginesAsync</c> both stay intact untouched.
    /// </summary>
    public const int ConfiguredFreshnessSeconds = 7 * 24 * 60 * 60;

    /// <summary>
    /// Maximum age of a worker's <c>LastHeartbeat</c> before it is no longer considered a
    /// live incumbent for InstanceId-conflict purposes (issue #1860), regardless of its
    /// current <c>Status</c>. Matches <c>WorkerHealthMonitorService</c>'s own stale-heartbeat
    /// timeout. That monitor's sweep (via <c>IWorkerRepository.GetStaleWorkersAsync</c>)
    /// only reclassifies stale <c>Online</c> workers to <c>Offline</c> — a worker that crashes
    /// while <c>Busy</c>, <c>Draining</c>, or <c>Error</c> is never swept by that monitor and
    /// would otherwise be stuck non-Offline (and therefore un-reclaimable by a legitimate
    /// redeploy) until the much longer stale-worker cleanup job runs, by default up to 24h
    /// later. Gating the InstanceId-conflict check on heartbeat freshness — not Status alone
    /// — means any worker whose heartbeat has actually gone stale is immediately reclaimable
    /// by a genuine redeploy, no matter what non-Offline status it was last reported in,
    /// while a worker that is still heartbeating recently remains protected from squatting.
    /// </summary>
    public const int LiveHeartbeatTimeoutSeconds = 120;

    public const string Offline = "Offline";
    public const string Online = "Online";
    public const string Busy = "Busy";
    public const string Error = "Error";
    public const string Draining = "Draining";
}
