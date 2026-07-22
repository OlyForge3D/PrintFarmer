namespace Farm.Infrastructure.Exceptions;

/// <summary>
/// Thrown when the caller is neither the owner of a collection nor an administrator and therefore
/// may not perform the requested operation (maps to HTTP 403).
/// </summary>
public sealed class CollectionAccessDeniedException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="CollectionAccessDeniedException"/> class.</summary>
    public CollectionAccessDeniedException()
        : base("You do not have permission to access this collection.")
    {
    }

    /// <summary>Initializes a new instance with a message.</summary>
    public CollectionAccessDeniedException(string message) : base(message)
    {
    }

    /// <summary>Initializes a new instance with a message and inner exception.</summary>
    public CollectionAccessDeniedException(string message, Exception inner) : base(message, inner)
    {
    }
}
