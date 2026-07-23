using Farm.Infrastructure.Dtos;

namespace Farm.Infrastructure.Exceptions;

/// <summary>
/// Thrown when a sync apply batch cannot be committed because one or more operations conflict
/// with the current server state (stale base revision, mismatched concurrency token, or a
/// concurrent write detected at save time). Carries the full conflict set and the current
/// server revision. The batch is atomic: when this is thrown, nothing was persisted. Mapped
/// to HTTP 409 by the API layer.
/// </summary>
public sealed class SyncConflictException : Exception
{
    /// <summary>The conflicting operations, each with safe server and submitted versions.</summary>
    public IReadOnlyList<SyncConflictDto> Conflicts { get; } = [];

    /// <summary>The current server revision (global head) the client should re-pull from.</summary>
    public long ServerRevision { get; }

    public SyncConflictException()
        : base("One or more operations conflict with the current server state")
    {
    }

    public SyncConflictException(string message) : base(message)
    {
    }

    public SyncConflictException(string message, Exception inner) : base(message, inner)
    {
    }

    public SyncConflictException(IReadOnlyList<SyncConflictDto> conflicts, long serverRevision)
        : base("One or more operations conflict with the current server state")
    {
        Conflicts = conflicts ?? [];
        ServerRevision = serverRevision;
    }
}
