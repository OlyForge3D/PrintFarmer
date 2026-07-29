namespace Farm.Infrastructure.Services.Queue;

/// <summary>
/// Raised when a calibration queue request reuses an idempotency key with a different payload hash.
/// </summary>
public sealed class QueueJobIdempotencyConflictException : InvalidOperationException
{
    public QueueJobIdempotencyConflictException()
    {
    }

    public QueueJobIdempotencyConflictException(string message)
        : base(message)
    {
    }

    public QueueJobIdempotencyConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
