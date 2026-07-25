namespace Farm.Slicer.Module.Services;

/// <summary>
/// Raised when an uploaded artifact fails a declared-content check.
/// </summary>
/// <remarks>
/// The <see cref="Code"/> is a stable machine-readable reason returned to the producing worker so it
/// can distinguish a retryable transport fault from a genuine integrity failure.
/// </remarks>
public sealed class ArtifactValidationException : Exception
{
    /// <summary>Stable reason code for an artifact whose declared digest did not match.</summary>
    public const string HashMismatch = "artifact_hash_mismatch";

    /// <summary>Stable reason code for an artifact whose declared size did not match.</summary>
    public const string SizeMismatch = "artifact_size_mismatch";

    /// <summary>Stable reason code for an artifact whose MIME type is not accepted for its kind.</summary>
    public const string UnsupportedMediaType = "artifact_media_type_rejected";

    /// <summary>Stable reason code for an artifact kind outside the configured allowlist.</summary>
    public const string UnsupportedKind = "artifact_kind_rejected";

    /// <summary>Initializes a new instance with the default reason code.</summary>
    public ArtifactValidationException()
        : this(HashMismatch, "The uploaded artifact failed validation.")
    {
    }

    /// <summary>Initializes a new instance with a message and the default reason code.</summary>
    /// <param name="message">Non-sensitive description of the failure.</param>
    public ArtifactValidationException(string message)
        : this(HashMismatch, message)
    {
    }

    /// <summary>Initializes a new instance with a message and inner exception.</summary>
    /// <param name="message">Non-sensitive description of the failure.</param>
    /// <param name="innerException">The underlying failure.</param>
    public ArtifactValidationException(string message, Exception innerException)
        : base(message, innerException) => Code = HashMismatch;

    /// <summary>Initializes a new instance with an explicit reason code.</summary>
    /// <param name="code">Stable machine-readable reason code.</param>
    /// <param name="message">Non-sensitive description of the failure.</param>
    public ArtifactValidationException(string code, string message)
        : base(message) => Code = code;

    /// <summary>Stable machine-readable reason code.</summary>
    public string Code { get; }
}
