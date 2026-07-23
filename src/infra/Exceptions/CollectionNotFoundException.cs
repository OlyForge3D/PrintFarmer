namespace Farm.Infrastructure.Exceptions;

/// <summary>
/// Thrown when a requested <see cref="Farm.Infrastructure.Domain.ModelCollection"/>
/// does not exist. Mapped to HTTP 404 by the API layer.
/// </summary>
public sealed class CollectionNotFoundException : Exception
{
    /// <summary>The identifier that could not be found, when known.</summary>
    public Guid? CollectionId { get; }

    public CollectionNotFoundException()
    {
    }

    public CollectionNotFoundException(string message) : base(message)
    {
    }

    public CollectionNotFoundException(string message, Exception inner) : base(message, inner)
    {
    }

    public CollectionNotFoundException(Guid collectionId)
        : base($"Collection {collectionId} was not found")
    {
        CollectionId = collectionId;
    }
}
