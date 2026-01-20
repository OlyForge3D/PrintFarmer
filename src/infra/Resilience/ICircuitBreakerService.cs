namespace Farm.Infrastructure;

public interface ICircuitBreakerService
{
    CircuitBreaker GetCircuitBreaker(string name, int? failureThreshold = null, TimeSpan? timeout = null, TimeSpan? retryDelay = null);

    IEnumerable<CircuitBreakerMetrics> GetAllMetrics();

    void ResetAll();
}
