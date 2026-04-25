namespace Farm.Infrastructure.Domain;

/// <summary>
/// Defines how a printer should start dispatched print jobs.
/// </summary>
public enum StartBehavior
{
    /// <summary>
    /// Operator must manually start each print job.
    /// </summary>
    Manual = 0,

    /// <summary>
    /// Print jobs start automatically once dispatched.
    /// </summary>
    AutoStart = 1,

    /// <summary>
    /// Printer waits for operator confirmation before starting dispatched jobs.
    /// </summary>
    WaitForConfirmation = 2,
}
