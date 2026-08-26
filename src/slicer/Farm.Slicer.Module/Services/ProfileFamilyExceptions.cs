namespace Farm.Slicer.Module.Services;

/// <summary>Indicates that a requested family name is already globally reserved.</summary>
public sealed class ProfileFamilyConflictException : Exception
{
    /// <summary>Creates an empty conflict exception.</summary>
    public ProfileFamilyConflictException()
    {
    }

    /// <summary>Creates a conflict with a descriptive message.</summary>
    public ProfileFamilyConflictException(string message)
        : base(message)
    {
    }

    /// <summary>Creates a conflict with an underlying persistence error.</summary>
    public ProfileFamilyConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Indicates that a pinned source model or preset cannot be resolved safely.</summary>
public sealed class ProfileFamilySourceException : Exception
{
    /// <summary>Creates an empty source-resolution exception.</summary>
    public ProfileFamilySourceException()
    {
    }

    /// <summary>Creates a source-resolution exception with a descriptive message.</summary>
    public ProfileFamilySourceException(string message)
        : base(message)
    {
    }

    /// <summary>Creates a source-resolution exception with an underlying error.</summary>
    public ProfileFamilySourceException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Indicates that a requested custom profile family does not exist.</summary>
public sealed class ProfileFamilyNotFoundException : Exception
{
    /// <summary>Creates an empty not-found exception.</summary>
    public ProfileFamilyNotFoundException()
    {
    }

    /// <summary>Creates a not-found exception with a descriptive message.</summary>
    public ProfileFamilyNotFoundException(string message)
        : base(message)
    {
    }

    /// <summary>Creates a not-found exception with an underlying error.</summary>
    public ProfileFamilyNotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Indicates that a family cannot be deleted because a live reference still holds it
/// (a registered printer's template profile, or a non-terminal slice job).
/// </summary>
public sealed class ProfileFamilyInUseException : Exception
{
    /// <summary>Creates an empty in-use exception.</summary>
    public ProfileFamilyInUseException()
    {
    }

    /// <summary>Creates an in-use exception with a descriptive message naming the holder.</summary>
    public ProfileFamilyInUseException(string message)
        : base(message)
    {
    }

    /// <summary>Creates an in-use exception with an underlying error.</summary>
    public ProfileFamilyInUseException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
