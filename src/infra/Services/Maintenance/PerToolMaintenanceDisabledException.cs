namespace Farm.Infrastructure.Services.Maintenance;

/// <summary>
/// Raised when a caller attempts to mutate a toolhead-scoped maintenance resource while
/// per-tool maintenance is disabled.
/// </summary>
public sealed class PerToolMaintenanceDisabledException : InvalidOperationException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PerToolMaintenanceDisabledException"/> class.
    /// </summary>
    public PerToolMaintenanceDisabledException()
        : base("Per-tool maintenance is disabled.")
    {
    }

    /// <summary>
    /// Initializes a new instance with a caller-provided message.
    /// </summary>
    /// <param name="message">The error message.</param>
    public PerToolMaintenanceDisabledException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance with a caller-provided message and underlying cause.
    /// </summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The exception that caused this failure.</param>
    public PerToolMaintenanceDisabledException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
