namespace Farm.Infrastructure.Domain.Notifications;

public enum NotificationType
{
    JobStarted,
    JobCompleted,
    JobFailed,
    JobPaused,
    JobResumed,
    QueueAlert,
    SystemAlert
}
