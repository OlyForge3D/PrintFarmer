namespace Farm.Infrastructure.Security;

/// <summary>
/// Canonical SignalR groups used to isolate authenticated farm, user, and resource events.
/// </summary>
public static class AuthorizedHubGroups
{
    public const string Farm = "Farm-default";
    public const string Administrators = "FarmAdministrators";
    public const string SlicingMonitors = "SlicingMonitors";
    public const string QueueReaders = "QueueReaders";

    public static string User(Guid userId) => $"User-{userId}";

    public static string SliceJob(Guid jobId) => $"Job-{jobId}";

    public static string Printer(Guid printerId) => $"Printer-{printerId}";

    public static string Project(Guid projectId) => $"Project-{projectId}";

    public static string CalibrationAttempt(Guid calibrationAttemptId) =>
        $"CalibrationAttempt-{calibrationAttemptId}";

    public static string QueueJob(Guid queueJobId) => $"QueueJob-{queueJobId}";
}
