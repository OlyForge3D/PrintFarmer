namespace Farm.Infrastructure.Exceptions;

/// <summary>
/// Thrown when a collection membership operation references one or more model ids that do
/// not exist according to the model query abstraction
/// (<see cref="Farm.Infrastructure.Services.IModel3DQueryProvider"/>).
/// Mapped to HTTP 400 by the API layer.
/// </summary>
public sealed class CollectionModelValidationException : Exception
{
    /// <summary>The model ids that failed existence validation.</summary>
    public IReadOnlyList<Guid> InvalidModelIds { get; } = [];

    public CollectionModelValidationException()
    {
    }

    public CollectionModelValidationException(string message) : base(message)
    {
    }

    public CollectionModelValidationException(string message, Exception inner) : base(message, inner)
    {
    }

    public CollectionModelValidationException(IReadOnlyList<Guid> invalidModelIds)
        : base($"One or more model ids do not exist: {string.Join(", ", invalidModelIds)}")
    {
        InvalidModelIds = invalidModelIds;
    }
}
