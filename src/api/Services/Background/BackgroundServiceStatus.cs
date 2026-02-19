using System.Collections.Concurrent;

namespace Farm.Web.Api.Services.Background;

/// <summary>
/// Status information for a background service
/// </summary>
public record BackgroundServiceStatus
{
    /// <summary>
    /// Unique identifier for the service (e.g., "PrintStatsSyncService")
    /// </summary>
    public required string ServiceId { get; init; }

    /// <summary>
    /// Human-readable display name (e.g., "Print Statistics Sync")
    /// </summary>
    public required string DisplayName { get; init; }

    /// <summary>
    /// Service description
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Category/group for the service (e.g., "Maintenance", "Slicing", "Printers")
    /// </summary>
    public string? Category { get; init; }

    /// <summary>
    /// Icon identifier for UI display
    /// </summary>
    public string? Icon { get; init; }

    /// <summary>
    /// Whether the service is currently running
    /// </summary>
    public bool IsRunning { get; init; }

    /// <summary>
    /// Whether the service is enabled in settings
    /// </summary>
    public bool IsEnabled { get; init; }

    /// <summary>
    /// When the service last completed a successful iteration
    /// </summary>
    public DateTime? LastRunTime { get; init; }

    /// <summary>
    /// Next scheduled run time (if applicable)
    /// </summary>
    public DateTime? NextRunTime { get; init; }

    /// <summary>
    /// Last error message if the service encountered an error
    /// </summary>
    public string? LastError { get; init; }

    /// <summary>
    /// When the last error occurred
    /// </summary>
    public DateTime? LastErrorTime { get; init; }

    /// <summary>
    /// Count of successful runs since startup
    /// </summary>
    public long SuccessfulRuns { get; init; }

    /// <summary>
    /// Count of failed runs since startup
    /// </summary>
    public long FailedRuns { get; init; }

    /// <summary>
    /// Interval in seconds between runs (if periodic)
    /// </summary>
    public int? IntervalSeconds { get; init; }
}

/// <summary>
/// Internal state for tracking a background service
/// </summary>
public class BackgroundServiceState
{
    public required string ServiceId { get; init; }

    public required string DisplayName { get; init; }

    public string? Description { get; init; }

    public string? Category { get; init; }

    public string? Icon { get; init; }

    public bool IsRunning { get; set; }

    public bool IsEnabled { get; set; } = true;

    public DateTime? LastRunTime { get; set; }

    public DateTime? NextRunTime { get; set; }

    public string? LastError { get; set; }

    public DateTime? LastErrorTime { get; set; }

    public long SuccessfulRuns { get; set; }

    public long FailedRuns { get; set; }

    public int? IntervalSeconds { get; set; }

    public BackgroundServiceStatus ToStatus() => new()
    {
        ServiceId = ServiceId,
        DisplayName = DisplayName,
        Description = Description,
        Category = Category,
        Icon = Icon,
        IsRunning = IsRunning,
        IsEnabled = IsEnabled,
        LastRunTime = LastRunTime,
        NextRunTime = NextRunTime,
        LastError = LastError,
        LastErrorTime = LastErrorTime,
        SuccessfulRuns = SuccessfulRuns,
        FailedRuns = FailedRuns,
        IntervalSeconds = IntervalSeconds
    };
}

/// <summary>
/// Service for monitoring background service status.
/// Background services can report their status to this monitor.
/// </summary>
public interface IBackgroundServiceMonitor
{
    /// <summary>
    /// Register a background service with the monitor
    /// </summary>
    void Register(string serviceId, string displayName, string? description = null, string? category = null, string? icon = null, int? intervalSeconds = null);

    /// <summary>
    /// Report that a service has started running
    /// </summary>
    void ReportStarted(string serviceId);

    /// <summary>
    /// Report that a service has stopped
    /// </summary>
    void ReportStopped(string serviceId);

    /// <summary>
    /// Report that a service is enabled/disabled
    /// </summary>
    void ReportEnabled(string serviceId, bool enabled);

    /// <summary>
    /// Report a successful run completion
    /// </summary>
    void ReportSuccess(string serviceId, int? nextIntervalSeconds = null);

    /// <summary>
    /// Report a failed run
    /// </summary>
    void ReportError(string serviceId, string errorMessage);

    /// <summary>
    /// Get status of all registered services
    /// </summary>
    IReadOnlyList<BackgroundServiceStatus> GetAllStatuses();

    /// <summary>
    /// Get status of a specific service
    /// </summary>
    BackgroundServiceStatus? GetStatus(string serviceId);
}

/// <summary>
/// In-memory implementation of background service monitoring.
/// Implements both the API-local <see cref="IBackgroundServiceMonitor"/> and the
/// infrastructure <see cref="Farm.Infrastructure.Services.IHostedServiceMonitor"/>
/// so that module-hosted services report to the same monitor.
/// </summary>
public class BackgroundServiceMonitor : IBackgroundServiceMonitor, Farm.Infrastructure.Services.IHostedServiceMonitor
{
    private readonly ConcurrentDictionary<string, BackgroundServiceState> _services = new();

    public void Register(string serviceId, string displayName, string? description = null, string? category = null, string? icon = null, int? intervalSeconds = null)
    {
        _services.TryAdd(serviceId, new BackgroundServiceState
        {
            ServiceId = serviceId,
            DisplayName = displayName,
            Description = description,
            Category = category,
            Icon = icon,
            IntervalSeconds = intervalSeconds
        });
    }

    public void ReportStarted(string serviceId)
    {
        if (_services.TryGetValue(serviceId, out BackgroundServiceState? state))
        {
            state.IsRunning = true;
        }
    }

    public void ReportStopped(string serviceId)
    {
        if (_services.TryGetValue(serviceId, out BackgroundServiceState? state))
        {
            state.IsRunning = false;
        }
    }

    public void ReportEnabled(string serviceId, bool enabled)
    {
        if (_services.TryGetValue(serviceId, out BackgroundServiceState? state))
        {
            state.IsEnabled = enabled;
        }
    }

    public void ReportSuccess(string serviceId, int? nextIntervalSeconds = null)
    {
        if (_services.TryGetValue(serviceId, out BackgroundServiceState? state))
        {
            state.LastRunTime = DateTime.UtcNow;
            state.SuccessfulRuns++;
            state.LastError = null;

            if (nextIntervalSeconds.HasValue)
            {
                state.IntervalSeconds = nextIntervalSeconds.Value;
                state.NextRunTime = DateTime.UtcNow.AddSeconds(nextIntervalSeconds.Value);
            }
        }
    }

    public void ReportError(string serviceId, string errorMessage)
    {
        if (_services.TryGetValue(serviceId, out BackgroundServiceState? state))
        {
            state.LastErrorTime = DateTime.UtcNow;
            state.LastError = errorMessage;
            state.FailedRuns++;
        }
    }

    public IReadOnlyList<BackgroundServiceStatus> GetAllStatuses()
    {
        return _services.Values.Select(s => s.ToStatus()).ToList();
    }

    public BackgroundServiceStatus? GetStatus(string serviceId)
    {
        return _services.TryGetValue(serviceId, out BackgroundServiceState? state) ? state.ToStatus() : null;
    }
}
