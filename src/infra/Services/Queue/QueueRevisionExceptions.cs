namespace Farm.Infrastructure.Services.Queue;

/// <summary>
/// Raised when a caller-supplied <c>If-Match</c> revision no longer matches the persisted
/// row. Maps to HTTP <c>412 Precondition Failed</c> — the request was well-formed but the
/// resource moved on. Distinct from <see cref="QueueSemanticConflictException"/> (409),
/// which means the request conflicts with the resource's current semantics.
/// </summary>
public sealed class QueueRevisionConflictException : InvalidOperationException
{
    /// <summary>Current print-job row version, when the conflicted operation targeted a job.</summary>
    public byte[]? CurrentJobRowVersion { get; }

    /// <summary>Current printer dispatch-state row version, when available.</summary>
    public byte[]? CurrentDispatchStateRowVersion { get; }

    /// <summary>Initializes a new instance of the <see cref="QueueRevisionConflictException"/> class.</summary>
    public QueueRevisionConflictException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="QueueRevisionConflictException"/> class.</summary>
    /// <param name="message">Human-readable detail.</param>
    public QueueRevisionConflictException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a conflict carrying the authoritative revisions clients need to retry.
    /// </summary>
    public QueueRevisionConflictException(
        string message,
        byte[]? currentJobRowVersion,
        byte[]? currentDispatchStateRowVersion)
        : base(message)
    {
        CurrentJobRowVersion = currentJobRowVersion?.ToArray();
        CurrentDispatchStateRowVersion = currentDispatchStateRowVersion?.ToArray();
    }

    /// <summary>Initializes a new instance of the <see cref="QueueRevisionConflictException"/> class.</summary>
    /// <param name="message">Human-readable detail.</param>
    /// <param name="innerException">Underlying cause.</param>
    public QueueRevisionConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Raised when a required precondition header (<c>If-Match</c>, <c>Idempotency-Key</c>) is
/// absent. Maps to HTTP <c>428 Precondition Required</c>.
/// </summary>
public sealed class QueuePreconditionRequiredException : InvalidOperationException
{
    /// <summary>Initializes a new instance of the <see cref="QueuePreconditionRequiredException"/> class.</summary>
    public QueuePreconditionRequiredException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="QueuePreconditionRequiredException"/> class.</summary>
    /// <param name="message">Human-readable detail.</param>
    public QueuePreconditionRequiredException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="QueuePreconditionRequiredException"/> class.</summary>
    /// <param name="message">Human-readable detail.</param>
    /// <param name="innerException">Underlying cause.</param>
    public QueuePreconditionRequiredException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Raised when a request conflicts with the resource's current semantics (for example a
/// job that is already printing). Maps to HTTP <c>409 Conflict</c> — re-fetching and
/// retrying with a fresh ETag will NOT help; the caller must change the request.
/// </summary>
public sealed class QueueSemanticConflictException : InvalidOperationException
{
    /// <summary>Initializes a new instance of the <see cref="QueueSemanticConflictException"/> class.</summary>
    public QueueSemanticConflictException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="QueueSemanticConflictException"/> class.</summary>
    /// <param name="message">Human-readable detail.</param>
    public QueueSemanticConflictException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="QueueSemanticConflictException"/> class.</summary>
    /// <param name="message">Human-readable detail.</param>
    /// <param name="innerException">Underlying cause.</param>
    public QueueSemanticConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
