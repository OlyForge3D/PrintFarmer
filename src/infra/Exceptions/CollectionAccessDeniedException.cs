namespace Farm.Infrastructure.Exceptions;

/// <summary>
/// Thrown when the calling user is neither the owner nor an administrator and therefore
/// may not perform the requested operation on a
/// <see cref="Farm.Infrastructure.Domain.ModelCollection"/>. Mapped to HTTP 403 by the API layer.
/// </summary>
public sealed class CollectionAccessDeniedException : Exception
{
    public CollectionAccessDeniedException()
        : base("You do not have permission to access this collection")
    {
    }

    public CollectionAccessDeniedException(string message) : base(message)
    {
    }

    public CollectionAccessDeniedException(string message, Exception inner) : base(message, inner)
    {
    }
}
