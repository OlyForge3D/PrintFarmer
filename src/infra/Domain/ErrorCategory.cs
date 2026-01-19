namespace Farm.Infrastructure.Domain;

/// <summary>
/// Error category classification for retry logic
/// </summary>
public enum ErrorCategory
{
    /// <summary>
    /// Unknown error category - needs manual investigation (default)
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// Network timeouts, printer offline, temporary printer errors - should retry
    /// </summary>
    Recoverable = 1,

    /// <summary>
    /// Invalid gcode file, unsupported printer, hardware failure - don't retry
    /// </summary>
    Permanent = 2
}
