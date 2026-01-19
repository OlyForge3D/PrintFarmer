namespace Farm.Web.Api.Services.Workers;

public class CircuitBreakerSettings
{
    /// <summary>
    /// Number of failures within WindowSeconds to open circuit
    /// </summary>
    public int FailureThreshold { get; set; } = 3;

    /// <summary>
    /// Time window in seconds to count failures
    /// </summary>
    public int WindowSeconds { get; set; } = 300; // 5 minutes

    /// <summary>
    /// Cooldown period in seconds before transitioning to half-open
    /// </summary>
    public int CooldownSeconds { get; set; } = 60; // 1 minute

    /// <summary>
    /// Number of consecutive successes needed to close circuit from half-open
    /// </summary>
    public int SuccessThresholdToClose { get; set; } = 2;
}
