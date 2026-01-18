using System.Collections.Concurrent;
using Farm.Infrastructure.Telemetry;

namespace Farm.Infrastructure;

public class CircuitBreaker(int failureThreshold = 5, TimeSpan? timeout = null, TimeSpan? retryDelay = null, IUnifiedLoggingService? logger = null)
{
    private readonly int _failureThreshold = failureThreshold;
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromMinutes(1);
    private readonly TimeSpan _retryDelay = retryDelay ?? TimeSpan.FromSeconds(30);
    private readonly IUnifiedLoggingService _logger = logger ?? new NullLoggingService();

    private int _failureCount;
    private DateTime _lastFailureTime;
    private CircuitState _state = CircuitState.Closed;
    private readonly object _lock = new();

    public string Name { get; set; } = "CircuitBreaker";
    public CircuitState State => _state;
    public int FailureCount => _failureCount;

    public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (_state == CircuitState.Open)
        {
            if (DateTime.UtcNow - _lastFailureTime < _retryDelay)
            {
                _logger.LogWarning($"Circuit breaker {Name} is OPEN. Request blocked");
                throw new CircuitBreakerOpenException($"Circuit breaker {Name} is open");
            }

            lock (_lock)
            {
                if (DateTime.UtcNow - _lastFailureTime >= _retryDelay)
                {
                    _state = CircuitState.HalfOpen;
                    _logger.LogInformation($"Circuit breaker {Name} moved to HALF-OPEN");
                }
            }
        }

        try
        {
            using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_timeout);

            T? result = await operation(timeoutCts.Token);

            OnSuccess();
            return result;
        }
        catch (Exception ex) when (IsTransientFailure(ex))
        {
            OnFailure(ex);
            throw;
        }
    }

    public async Task ExecuteAsync(Func<CancellationToken, Task> operation, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        _ = await ExecuteAsync<object?>(async token =>
        {
            await operation(token);
            return null;
        }, ct);
    }

    private void OnSuccess()
    {
        lock (_lock)
        {
            if (_state == CircuitState.HalfOpen || _failureCount > 0)
            {
                _logger.LogInformation($"Circuit breaker {Name} reset after successful operation");
            }

            _failureCount = 0;
            _state = CircuitState.Closed;
        }
    }

    private void OnFailure(Exception ex)
    {
        lock (_lock)
        {
            _failureCount++;
            _lastFailureTime = DateTime.UtcNow;

            _logger.LogWarning($"Circuit breaker {Name} recorded failure {_failureCount}/{_failureThreshold}: {ex.Message}");

            if (_failureCount >= _failureThreshold)
            {
                _state = CircuitState.Open;
                _logger.LogError($"Circuit breaker {Name} is now OPEN after {_failureCount} consecutive failures");
            }
        }
    }

    private static bool IsTransientFailure(Exception ex) => ex switch
    {
        TimeoutException => true,
        HttpRequestException => true,
        OperationCanceledException when ex.InnerException is TimeoutException => true,
        TaskCanceledException => true,
        _ => false
    };

    public void Reset()
    {
        lock (_lock)
        {
            _failureCount = 0;
            _state = CircuitState.Closed;
            _logger.LogInformation($"Circuit breaker {Name} manually reset");
        }
    }

    public CircuitBreakerMetrics Metrics => new()
    {
        Name = Name,
        State = _state,
        FailureCount = _failureCount,
        LastFailureTime = _lastFailureTime,
        FailureThreshold = _failureThreshold
    };
}

public enum CircuitState
{
    Closed,
    Open,
    HalfOpen
}

public class CircuitBreakerOpenException : Exception
{
    public CircuitBreakerOpenException(string message) : base(message) { }
    public CircuitBreakerOpenException(string message, Exception innerException) : base(message, innerException) { }
    public CircuitBreakerOpenException() { }
}

public record CircuitBreakerMetrics
{
    public string Name { get; init; } = string.Empty;
    public CircuitState State { get; init; }
    public int FailureCount { get; init; }
    public DateTime LastFailureTime { get; init; }
    public int FailureThreshold { get; init; }
}

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

public interface ICircuitBreakerService
{
    CircuitBreaker GetCircuitBreaker(string name, int? failureThreshold = null, TimeSpan? timeout = null, TimeSpan? retryDelay = null);
    IEnumerable<CircuitBreakerMetrics> GetAllMetrics();
    void ResetAll();
}
