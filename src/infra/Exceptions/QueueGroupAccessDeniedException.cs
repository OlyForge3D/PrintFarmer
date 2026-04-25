namespace Farm.Infrastructure;

/// <summary>
/// Exception thrown when a user does not have permission to submit jobs to a printer group.
/// </summary>
public class QueueGroupAccessDeniedException : InvalidOperationException
{
    public Guid GroupId { get; }

    public Guid UserId { get; }

    public QueueGroupAccessDeniedException(Guid groupId, Guid userId)
        : base($"User {userId} does not have permission to submit jobs to printer group {groupId}.")
    {
        GroupId = groupId;
        UserId = userId;
    }

    public QueueGroupAccessDeniedException()
    {
    }

    public QueueGroupAccessDeniedException(string message) : base(message)
    {
    }

    public QueueGroupAccessDeniedException(string message, Exception inner) : base(message, inner)
    {
    }
}
