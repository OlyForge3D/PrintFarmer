namespace Farm.Slicer.Module.Services;

/// <summary>
/// Circuit breaker states for slicer worker health tracking.
/// Named WorkerCircuitState to avoid ambiguity with Farm.Infrastructure.CircuitState
/// (the generic resilience pattern used by printer connections).
/// </summary>
public enum WorkerCircuitState
{
    /// <summary>Normal operation; worker accepts jobs.</summary>
    Closed,

    /// <summary>Circuit tripped; worker temporarily disabled.</summary>
    Open,

    /// <summary>Testing if worker recovered.</summary>
    HalfOpen,
}
