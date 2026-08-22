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
    /// Whether the worker is currently disabled, whether by an administrator or automatically.
    /// </summary>
    /// <remarks>
    /// Read <see cref="DisableSource"/> to tell which. A disabled worker keeps heartbeating but
    /// is excluded from job assignment.
    /// </remarks>
    public bool IsDisabled { get; set; }

    /// <summary>
    /// Human-readable reason for disabling (if applicable). Display only — never branch on it.
    /// </summary>
    /// <remarks>
    /// This is free text: an administrator supplies it verbatim, so it can be made to look like
    /// anything, including any value an automatic disabler writes. <see cref="DisableSource"/>
    /// is the trustworthy discriminator.
    /// </remarks>
    public string? DisabledReason { get; set; }

    /// <summary>
    /// What disabled this worker, used to decide whether the disable may be lifted automatically.
    /// </summary>
    /// <remarks>
    /// The distinction matters because the sources must be treated very differently: an automatic
    /// disable is lifted when the worker comes back, whereas an administrator's ban must survive a
    /// restart, a redeploy, and a long outage. See <see cref="WorkerDisableSource"/>.
    /// </remarks>
    public WorkerDisableSource DisableSource { get; set; } = WorkerDisableSource.None;

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
/// What caused a <see cref="Worker"/> to be disabled.
/// </summary>
/// <remarks>
/// This exists because the reason text cannot safely carry this meaning. An earlier design
/// inferred "an administrator did this" from the reason string — anything that was neither
/// blank nor the deregistration literal was assumed to be an administrator's. That is wrong in
/// both directions: the circuit breaker writes its own descriptive reason and was therefore
/// misread as an administrator, which exempted circuit-broken workers from cleanup forever; and
/// an administrator's reason is unvalidated free text, so typing the deregistration literal
/// made a real ban clearable by the next registration. An explicit column makes the
/// classification total: a new automatic disabler has to name itself here, rather than silently
/// defaulting to the most privileged interpretation.
/// </remarks>
public enum WorkerDisableSource
{
    /// <summary>
    /// The worker is not disabled, or was disabled before this column existed and could not be
    /// attributed. Treated as automatic, which matches the behaviour before the column existed.
    /// </summary>
    None = 0,

    /// <summary>
    /// An administrator deliberately disabled this worker. The only value that survives a
    /// restart, a redeploy, and the stale-worker sweep.
    /// </summary>
    Administrator = 1,

    /// <summary>
    /// Deregistration disabled the worker as it shut down. Lifted when the worker registers again.
    /// </summary>
    Deregistration = 2,

    /// <summary>
    /// The circuit breaker disabled the worker after repeated job failures. Automatic, so it is
    /// lifted on re-registration and does not exempt the worker from the stale sweep.
    /// </summary>
    CircuitBreaker = 3,
}

/// <summary>
/// Well-known <see cref="Worker.DisabledReason"/> texts, and the rule for telling an automatic
/// lifecycle disable apart from an administrator's deliberate one.
/// </summary>
/// <remarks>
/// The reason strings here are for display and logging only. Classification goes through
/// <see cref="IsAdministrativeDisable"/>, which reads <see cref="Worker.DisableSource"/> and
/// never the text — see <see cref="WorkerDisableSource"/> for why the text is untrustworthy.
/// </remarks>
public static class WorkerDisableReasons
{
    /// <summary>
    /// Recorded when deregistration disables a worker, rather than an administrator.
    /// </summary>
    public const string Deregistered = "Slicer service deregistered";

    /// <summary>
    /// Builds the reason recorded when the circuit breaker disables a worker.
    /// </summary>
    /// <param name="failureCount">Number of failures observed in the window.</param>
    /// <param name="windowSeconds">Length of the observation window, in seconds.</param>
    /// <returns>The reason text to store.</returns>
    public static string CircuitBreaker(int failureCount, int windowSeconds) =>
        $"Circuit breaker: {failureCount} failures in {windowSeconds}s";

    /// <summary>
    /// Whether a worker's disabled state was applied deliberately by an administrator, and so
    /// must be preserved across restarts and excluded from automatic cleanup.
    /// </summary>
    /// <param name="worker">The worker to classify.</param>
    /// <returns><see langword="true"/> when an administrator disabled this worker.</returns>
    public static bool IsAdministrativeDisable(Worker worker)
    {
        ArgumentNullException.ThrowIfNull(worker);

        return worker.IsDisabled && worker.DisableSource == WorkerDisableSource.Administrator;
    }
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

    public const string Offline = "Offline";
    public const string Online = "Online";
    public const string Busy = "Busy";
    public const string Error = "Error";
    public const string Draining = "Draining";
}
