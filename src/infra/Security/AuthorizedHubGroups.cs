namespace Farm.Infrastructure.Security;

/// <summary>
/// Canonical SignalR groups used to isolate authenticated farm, user, and resource events.
/// </summary>
public static class AuthorizedHubGroups
{
    public const string AuthenticatedUsers = "AuthenticatedUsers";
    public const string Farm = "Farm-default";
    public const string Administrators = "FarmAdministrators";
    public const string SlicingMonitors = "SlicingMonitors";
    public const string QueueReaders = "QueueReaders";

    public static string User(Guid userId) => $"User-{userId}";

    public static string SliceJob(Guid jobId) => $"Job-{jobId}";

    public static string Printer(Guid printerId) => $"Printer-{printerId}";

    /// <summary>
    /// Per-printer scope for maintenance alert and resolution events (issue #1966). Deliberately
    /// distinct from <see cref="Printer"/>, which scopes <c>PrinterHub</c> status/queue traffic —
    /// a connection subscribed to one hub's printer group is not implicitly subscribed to the
    /// other's.
    /// </summary>
    public static string MaintenancePrinter(Guid printerId) => $"maintenance-printer-{printerId}";

    public static string Project(Guid projectId) => $"Project-{projectId}";

    public static string CalibrationAttempt(Guid calibrationAttemptId) =>
        $"CalibrationAttempt-{calibrationAttemptId}";

    public static string QueueJob(Guid queueJobId) => $"QueueJob-{queueJobId}";
}
