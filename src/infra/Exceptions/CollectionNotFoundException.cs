namespace Farm.Infrastructure.Exceptions;

/// <summary>
/// Thrown when a requested model collection does not exist (maps to HTTP 404).
/// </summary>
public sealed class CollectionNotFoundException : Exception
{
    /// <summary>Identifier of the collection that could not be found, when known.</summary>
    public Guid? CollectionId { get; }

    /// <summary>Initializes a new instance of the <see cref="CollectionNotFoundException"/> class.</summary>
    public CollectionNotFoundException()
    {
    }

    /// <summary>Initializes a new instance with a message.</summary>
    public CollectionNotFoundException(string message) : base(message)
    {
    }

    /// <summary>Initializes a new instance with a message and inner exception.</summary>
    public CollectionNotFoundException(string message, Exception inner) : base(message, inner)
    {
    }

    /// <summary>Initializes a new instance for a specific collection identifier.</summary>
    public CollectionNotFoundException(Guid collectionId)
        : base($"Collection '{collectionId}' was not found.")
    {
        CollectionId = collectionId;
    }
}
