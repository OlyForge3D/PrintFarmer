namespace Farm.Infrastructure;

/// <summary>
/// Service for managing circuit breakers to prevent cascading failures.
/// Implements the circuit breaker pattern for resilient external service calls.
/// </summary>
public interface ICircuitBreakerService
{
    /// <summary>
    /// Gets or creates a circuit breaker with the specified name and configuration.
    /// </summary>
    /// <param name="name">Unique identifier for the circuit breaker.</param>
    /// <param name="failureThreshold">Number of failures before opening the circuit.</param>
    /// <param name="timeout">Duration to wait before attempting to close the circuit.</param>
    /// <param name="retryDelay">Delay between retry attempts when circuit is half-open.</param>
    /// <returns>The circuit breaker instance.</returns>
    CircuitBreaker GetCircuitBreaker(string name, int? failureThreshold = null, TimeSpan? timeout = null, TimeSpan? retryDelay = null);

    /// <summary>
    /// Gets metrics for all registered circuit breakers.
    /// </summary>
    /// <returns>Collection of circuit breaker metrics including failure counts and states.</returns>
    IEnumerable<CircuitBreakerMetrics> GetAllMetrics();

    /// <summary>
    /// Resets all circuit breakers to their initial closed state.
    /// </summary>
    void ResetAll();
}
