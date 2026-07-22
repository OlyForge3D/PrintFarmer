namespace Farm.Infrastructure.Exceptions;

/// <summary>
/// Thrown when a membership operation references a model identifier that does not resolve to an
/// existing model via the model query abstraction (maps to HTTP 404).
/// </summary>
public sealed class CollectionModelNotFoundException : Exception
{
    /// <summary>Identifier of the model that could not be found, when known.</summary>
    public Guid? ModelId { get; }

    /// <summary>Initializes a new instance of the <see cref="CollectionModelNotFoundException"/> class.</summary>
    public CollectionModelNotFoundException()
    {
    }

    /// <summary>Initializes a new instance with a message.</summary>
    public CollectionModelNotFoundException(string message) : base(message)
    {
    }

    /// <summary>Initializes a new instance with a message and inner exception.</summary>
    public CollectionModelNotFoundException(string message, Exception inner) : base(message, inner)
    {
    }

    /// <summary>Initializes a new instance for a specific model identifier.</summary>
    public CollectionModelNotFoundException(Guid modelId)
        : base($"Model '{modelId}' does not exist.")
    {
        ModelId = modelId;
    }
}
