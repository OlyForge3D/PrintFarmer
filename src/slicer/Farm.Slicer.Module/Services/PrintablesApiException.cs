namespace Farm.Slicer.Module.Services;

/// <summary>
/// Raised when the Printables GraphQL API returns an error response, the model is not found,
/// or the API is unreachable.
/// </summary>
public sealed class PrintablesApiException : Exception
{
    /// <summary>Initialises a new instance with a default message.</summary>
    public PrintablesApiException()
    {
    }

    /// <summary>Initialises a new instance with the specified message.</summary>
    /// <param name="message">Explanatory message.</param>
    public PrintablesApiException(string message)
        : base(message)
    {
    }

    /// <summary>Initialises a new instance with the specified message and inner exception.</summary>
    /// <param name="message">Explanatory message.</param>
    /// <param name="inner">The exception that is the cause of this exception.</param>
    public PrintablesApiException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
