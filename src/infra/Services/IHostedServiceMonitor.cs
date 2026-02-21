namespace Farm.Infrastructure.Services;

/// <summary>
/// Optional interface for background service status monitoring.
/// When registered, hosted services report their lifecycle events through this monitor.
/// When not registered (e.g., standalone deployment), services continue without monitoring.
/// </summary>
public interface IHostedServiceMonitor
{
    /// <summary>Register a background service with the monitor.</summary>
    void Register(string serviceId, string displayName, string? description = null, string? category = null, string? icon = null, int? intervalSeconds = null);

    /// <summary>Report that a service has started running.</summary>
    void ReportStarted(string serviceId);

    /// <summary>Report that a service has stopped.</summary>
    void ReportStopped(string serviceId);

    /// <summary>Report that a service is enabled/disabled.</summary>
    void ReportEnabled(string serviceId, bool enabled);

    /// <summary>Report a successful run completion.</summary>
    void ReportSuccess(string serviceId, int? nextIntervalSeconds = null);

    /// <summary>Report a failed run.</summary>
    void ReportError(string serviceId, string errorMessage);
}
