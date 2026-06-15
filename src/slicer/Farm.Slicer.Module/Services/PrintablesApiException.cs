namespace Farm.Slicer.Module.Services;

/// <summary>
/// Raised when the Printables GraphQL API returns an error response, the model is not found,
/// or the API is unreachable.
/// </summary>
public sealed class PrintablesApiException : Exception
{
    /// <summary>
    /// Gets a value indicating whether this error is transient and safe to retry.
    /// </summary>
    public bool IsTransient { get; }

    /// <summary>Initialises a new instance with a default message.</summary>
    public PrintablesApiException()
    {
    }

    /// <summary>Initialises a new instance with the specified message.</summary>
    /// <param name="message">Explanatory message.</param>
    public PrintablesApiException(string message)
        : this(message, isTransient: false)
    {
    }

    /// <summary>Initialises a new instance with the specified message.</summary>
    /// <param name="message">Explanatory message.</param>
    /// <param name="isTransient">True when this error is safe to retry.</param>
    public PrintablesApiException(string message, bool isTransient)
        : base(message)
    {
        IsTransient = isTransient;
    }

    /// <summary>Initialises a new instance with the specified message and inner exception.</summary>
    /// <param name="message">Explanatory message.</param>
    /// <param name="inner">The exception that is the cause of this exception.</param>
    public PrintablesApiException(string message, Exception inner)
        : this(message, inner, isTransient: false)
    {
    }

    /// <summary>Initialises a new instance with the specified message and inner exception.</summary>
    /// <param name="message">Explanatory message.</param>
    /// <param name="inner">The exception that is the cause of this exception.</param>
    /// <param name="isTransient">True when this error is safe to retry.</param>
    public PrintablesApiException(string message, Exception inner, bool isTransient)
        : base(message, inner)
    {
        IsTransient = isTransient;
    }
}
