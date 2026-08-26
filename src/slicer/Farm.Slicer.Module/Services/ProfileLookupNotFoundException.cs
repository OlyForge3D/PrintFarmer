namespace Farm.Slicer.Module.Services;

/// <summary>
/// Indicates that a profile lookup completed successfully but no machine profiles matched.
/// </summary>
public sealed class ProfileLookupNotFoundException : Exception
{
    /// <summary>Creates an empty lookup exception.</summary>
    public ProfileLookupNotFoundException()
    {
    }

    /// <summary>Creates a lookup exception without a stable reason code.</summary>
    public ProfileLookupNotFoundException(string message)
        : base(message)
    {
    }

    /// <summary>Creates a lookup exception with an underlying error.</summary>
    public ProfileLookupNotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates a lookup exception with its stable API reason code.</summary>
    public ProfileLookupNotFoundException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    /// <summary>Gets the stable API reason code.</summary>
    public string Code { get; } = string.Empty;
}
