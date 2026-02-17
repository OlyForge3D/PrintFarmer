namespace Farm.Slicer.Module.Services.Configuration;

/// <summary>
/// Configuration for per-worker circuit breaker behavior.
/// </summary>
public class CircuitBreakerSettings
{
    /// <summary>Number of failures within <see cref="WindowSeconds"/> to open the circuit.</summary>
    public int FailureThreshold { get; set; } = 3;

    /// <summary>Time window in seconds to count failures.</summary>
    public int WindowSeconds { get; set; } = 300;

    /// <summary>Cooldown period in seconds before transitioning to half-open.</summary>
    public int CooldownSeconds { get; set; } = 60;

    /// <summary>Number of consecutive successes needed to close the circuit from half-open.</summary>
    public int SuccessThresholdToClose { get; set; } = 2;
}
