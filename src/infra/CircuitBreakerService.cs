using System.Collections.Concurrent;
using Farm.Infrastructure.Telemetry;

namespace Farm.Infrastructure;

public class CircuitBreakerService(IUnifiedLoggingService logger) : ICircuitBreakerService
{
    private readonly ConcurrentDictionary<string, CircuitBreaker> _circuitBreakers = new();
    private readonly IUnifiedLoggingService _logger = logger;

    public CircuitBreaker GetCircuitBreaker(string name, int? failureThreshold = null, TimeSpan? timeout = null, TimeSpan? retryDelay = null)
    {
        return _circuitBreakers.GetOrAdd(name, key =>
        {
            CircuitBreaker cb = new(
                failureThreshold ?? 5,
                timeout ?? TimeSpan.FromMinutes(1),
                retryDelay ?? TimeSpan.FromSeconds(30),
                _logger);
            cb.Name = key;
            return cb;
        });
    }

    public IEnumerable<CircuitBreakerMetrics> GetAllMetrics()
    {
        return _circuitBreakers.Values.Select(cb => cb.Metrics);
    }

    public void ResetAll()
    {
        foreach (CircuitBreaker cb in _circuitBreakers.Values)
        {
            cb.Reset();
        }

        _logger.LogInformation("All circuit breakers reset");
    }
}
