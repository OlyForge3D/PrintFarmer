namespace Farm.Slicer.Module.Services;

/// <summary>
/// Indicates that the caller already owns a custom profile with the requested name (#3192).
/// Only raised after a caller-scoped check, so it never reveals another user's profile names.
/// </summary>
public sealed class ProfileNameConflictException : Exception
{
    /// <summary>Stable machine-readable error code returned with the 409 response.</summary>
    public const string Code = "profile_name_conflict";

    /// <summary>Creates an empty conflict exception.</summary>
    public ProfileNameConflictException()
    {
    }

    /// <summary>Creates a conflict with a descriptive message.</summary>
    public ProfileNameConflictException(string message)
        : base(message)
    {
    }

    /// <summary>Creates a conflict with an underlying persistence error.</summary>
    public ProfileNameConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
