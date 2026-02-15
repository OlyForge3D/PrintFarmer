namespace Farm.Slicer.Module.Services;

/// <summary>
/// Circuit breaker states for slicer worker health tracking.
/// </summary>
public enum CircuitState
{
    /// <summary>Normal operation; worker accepts jobs.</summary>
    Closed,

    /// <summary>Circuit tripped; worker temporarily disabled.</summary>
    Open,

    /// <summary>Testing if worker recovered.</summary>
    HalfOpen,
}
