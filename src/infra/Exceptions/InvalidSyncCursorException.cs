namespace Farm.Infrastructure.Exceptions;

/// <summary>
/// Thrown when a sync pull cursor cannot be decoded or fails validation (malformed,
/// truncated, wrong version, or out-of-range). The cursor is an opaque, server-issued
/// token; any value the server did not produce is rejected. Mapped to HTTP 400 by the
/// API layer so a client can never page from an unvalidated position.
/// </summary>
public sealed class InvalidSyncCursorException : Exception
{
    public InvalidSyncCursorException()
        : base("The supplied sync cursor is invalid")
    {
    }

    public InvalidSyncCursorException(string message) : base(message)
    {
    }

    public InvalidSyncCursorException(string message, Exception inner) : base(message, inner)
    {
    }
}
