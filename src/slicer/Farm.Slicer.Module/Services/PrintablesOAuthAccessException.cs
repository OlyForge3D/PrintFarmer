namespace Farm.Slicer.Module.Services;

/// <summary>
/// Base class for Printables OAuth access failures that need explicit HTTP mapping.
/// </summary>
public abstract class PrintablesOAuthAccessException : Exception
{
    protected PrintablesOAuthAccessException()
    {
    }

    protected PrintablesOAuthAccessException(string message)
        : base(message)
    {
    }

    protected PrintablesOAuthAccessException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Raised when the user's Printables account is not linked or linked credentials are no longer valid.
/// </summary>
public sealed class PrintablesOAuthNotLinkedException : PrintablesOAuthAccessException
{
    public PrintablesOAuthNotLinkedException()
    {
    }

    public PrintablesOAuthNotLinkedException(string message)
        : base(message)
    {
    }

    public PrintablesOAuthNotLinkedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Raised when Printables OAuth operations are temporarily unavailable due to transient upstream conditions.
/// </summary>
public sealed class PrintablesOAuthTemporarilyUnavailableException : PrintablesOAuthAccessException
{
    public PrintablesOAuthTemporarilyUnavailableException()
    {
    }

    public PrintablesOAuthTemporarilyUnavailableException(string message)
        : base(message)
    {
    }

    public PrintablesOAuthTemporarilyUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
