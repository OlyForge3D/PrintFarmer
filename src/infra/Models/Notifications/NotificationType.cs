namespace Farm.Infrastructure.Domain.Notifications;

public enum NotificationType
{
    JobStarted,
    JobCompleted,
    JobFailed,
    JobPaused,
    JobResumed,
    QueueAlert,
    SystemAlert,

    /// <summary>A catalog PrinterModel template has been updated and the printer configuration may be out of date.</summary>
    CatalogUpdateAvailable
}
