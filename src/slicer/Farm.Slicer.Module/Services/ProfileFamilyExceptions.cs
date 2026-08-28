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

/// <summary>Indicates that a rendered profile's content hash collides with an existing profile.</summary>
public sealed class ProfileFamilyHashConflictException : Exception
{
    /// <summary>Creates an empty hash-conflict exception.</summary>
    public ProfileFamilyHashConflictException()
    {
    }

    /// <summary>Creates a hash conflict with a descriptive message.</summary>
    public ProfileFamilyHashConflictException(string message)
        : base(message)
    {
    }

    /// <summary>Creates a hash conflict with an underlying persistence error.</summary>
    public ProfileFamilyHashConflictException(string message, Exception innerException)
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

/// <summary>
/// Indicates that a family cannot be deleted because its OrcaSlicer alias is the <em>last</em> profile
/// coverage for a catalog model that a registered printer uses. Distinct from
/// <see cref="ProfileFamilyInUseException"/> (a direct variant binding): here the binding is indirect —
/// removing the alias would make <c>GET /api/slicer/profiles/machine/for-model/{modelId}</c> return
/// <c>404 no_profiles_for_model</c> for every printer of that model. The remediation differs (re-point
/// the printer or force the delete), so it carries its own <c>{code}</c>.
/// </summary>
public sealed class ProfileFamilyLastCoverageException : Exception
{
    /// <summary>Creates an empty last-coverage exception.</summary>
    public ProfileFamilyLastCoverageException()
    {
    }

    /// <summary>Creates a last-coverage exception with a descriptive message naming the printer.</summary>
    public ProfileFamilyLastCoverageException(string message)
        : base(message)
    {
    }

    /// <summary>Creates a last-coverage exception with an underlying error.</summary>
    public ProfileFamilyLastCoverageException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Indicates that a mutating family operation lost an optimistic-concurrency race: the row was modified
/// by a concurrent request between load and persist. Mapped to <c>409</c> so the caller can retry rather
/// than seeing a raw <c>500</c>.
/// </summary>
public sealed class ProfileFamilyConcurrencyException : Exception
{
    /// <summary>Creates an empty concurrency exception.</summary>
    public ProfileFamilyConcurrencyException()
    {
    }

    /// <summary>Creates a concurrency exception with a descriptive message.</summary>
    public ProfileFamilyConcurrencyException(string message)
        : base(message)
    {
    }

    /// <summary>Creates a concurrency exception with an underlying error.</summary>
    public ProfileFamilyConcurrencyException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Indicates that a render lost the race with a concurrent delete: the family row was removed between the
/// worker install and the authoritative persist. The partially installed bundle is rolled back before this
/// is thrown, so nothing is stranded on the worker. Mapped to <c>409</c>.
/// </summary>
public sealed class ProfileFamilyConcurrentlyDeletedException : Exception
{
    /// <summary>Creates an empty concurrently-deleted exception.</summary>
    public ProfileFamilyConcurrentlyDeletedException()
    {
    }

    /// <summary>Creates a concurrently-deleted exception with a descriptive message.</summary>
    public ProfileFamilyConcurrentlyDeletedException(string message)
        : base(message)
    {
    }

    /// <summary>Creates a concurrently-deleted exception with an underlying error.</summary>
    public ProfileFamilyConcurrentlyDeletedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Indicates that compensation after a profile-family concurrency conflict could not restore or remove
/// every derived worker artifact. Mapped to a stable service-unavailable response rather than hidden.
/// </summary>
public sealed class ProfileFamilyCleanupException : Exception
{
    /// <summary>Creates an empty cleanup exception.</summary>
    public ProfileFamilyCleanupException()
    {
    }

    /// <summary>Creates a cleanup exception with a descriptive message.</summary>
    public ProfileFamilyCleanupException(string message)
        : base(message)
    {
    }

    /// <summary>Creates a cleanup exception with an underlying error.</summary>
    public ProfileFamilyCleanupException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
