namespace Farm.Infrastructure.Exceptions;

/// <summary>
/// Thrown when a tag update is attempted with a stale expected revision (optimistic-concurrency
/// conflict). The client's base revision no longer matches the stored revision, so the write is
/// rejected rather than silently clobbering a concurrent change. Mapped to HTTP 409 by the API
/// layer (#844).
/// </summary>
public sealed class TagConcurrencyException : Exception
{
    /// <summary>The tag identifier, when known.</summary>
    public Guid? TagId { get; }

    /// <summary>The revision the caller expected, when known.</summary>
    public long? ExpectedRevision { get; }

    /// <summary>The revision currently stored, when known.</summary>
    public long? ActualRevision { get; }

    public TagConcurrencyException()
    {
    }

    public TagConcurrencyException(string message) : base(message)
    {
    }

    public TagConcurrencyException(string message, Exception inner) : base(message, inner)
    {
    }

    public TagConcurrencyException(Guid tagId, long expectedRevision, long actualRevision)
        : base($"Tag {tagId} was modified concurrently (expected revision {expectedRevision}, actual {actualRevision})")
    {
        TagId = tagId;
        ExpectedRevision = expectedRevision;
        ActualRevision = actualRevision;
    }
}
