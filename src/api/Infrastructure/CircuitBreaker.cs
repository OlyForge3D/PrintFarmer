using System.Collections.Concurrent;

namespace Farm.Web.Api.Infrastructure;

/// <summary>
/// Circuit breaker implementation for external service calls
/// Prevents cascading failures by temporarily blocking requests to failing services
/// </summary>
public class CircuitBreaker
{
    private readonly int _failureThreshold;
    private readonly TimeSpan _timeout;
    private readonly TimeSpan _retryDelay;
    private readonly ILogger<CircuitBreaker> _logger;

    private int _failureCount;
    private DateTime _lastFailureTime;
    private CircuitState _state = CircuitState.Closed;
    private readonly object _lock = new();

    public CircuitBreaker(int failureThreshold = 5, TimeSpan? timeout = null, TimeSpan? retryDelay = null, ILogger<CircuitBreaker>? logger = null)
    {
        _failureThreshold = failureThreshold;
        _timeout = timeout ?? TimeSpan.FromMinutes(1);
        _retryDelay = retryDelay ?? TimeSpan.FromSeconds(30);
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<CircuitBreaker>.Instance;
    }

    public string Name { get; set; } = "CircuitBreaker";
    public CircuitState State => _state;
    public int FailureCount => _failureCount;

    /// <summary>
    /// Executes a function with circuit breaker protection
    /// </summary>
    public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (_state == CircuitState.Open)
        {
            if (DateTime.UtcNow - _lastFailureTime < _retryDelay)
            {
                _logger.LogWarning("Circuit breaker {Name} is OPEN. Request blocked", Name);
                throw new CircuitBreakerOpenException($"Circuit breaker {Name} is open");
            }

            lock (_lock)
            {
                if (DateTime.UtcNow - _lastFailureTime >= _retryDelay)
                {
                    _state = CircuitState.HalfOpen;
                    _logger.LogInformation("Circuit breaker {Name} moved to HALF-OPEN", Name);
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

    /// <summary>
    /// Executes an action with circuit breaker protection
    /// </summary>
    public async Task ExecuteAsync(Func<CancellationToken, Task> operation, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await ExecuteAsync<object?>(async token =>
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
                _logger.LogInformation("Circuit breaker {Name} reset after successful operation", Name);
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

            _logger.LogWarning(ex, "Circuit breaker {Name} recorded failure {FailureCount}/{Threshold}",
                Name, _failureCount, _failureThreshold);

            if (_failureCount >= _failureThreshold)
            {
                _state = CircuitState.Open;
                _logger.LogError("Circuit breaker {Name} is now OPEN after {FailureCount} consecutive failures",
                    Name, _failureCount);
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

    /// <summary>
    /// Resets the circuit breaker to closed state
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
            _failureCount = 0;
            _state = CircuitState.Closed;
            _logger.LogInformation("Circuit breaker {Name} manually reset", Name);
        }
    }

    /// <summary>
    /// Gets current circuit breaker metrics
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1024:Use properties where appropriate", Justification = "Intentional method semantics; avoids API break and allows future parameters.")]
    public CircuitBreakerMetrics GetMetrics()
    {
        return new CircuitBreakerMetrics
        {
            Name = Name,
            State = _state,
            FailureCount = _failureCount,
            LastFailureTime = _lastFailureTime,
            FailureThreshold = _failureThreshold
        };
    }
}

/// <summary>
/// Circuit breaker states
/// </summary>
public enum CircuitState
{
    Closed,   // Normal operation
    Open,     // Blocking requests
    HalfOpen  // Testing if service recovered
}

/// <summary>
/// Exception thrown when circuit breaker is open
/// </summary>
public class CircuitBreakerOpenException : Exception
{
    public CircuitBreakerOpenException(string message) : base(message) { }
    public CircuitBreakerOpenException(string message, Exception innerException) : base(message, innerException) { }
    public CircuitBreakerOpenException()
    {
    }
}

/// <summary>
/// Circuit breaker metrics for monitoring
/// </summary>
public record CircuitBreakerMetrics
{
    public string Name { get; init; } = string.Empty;
    public CircuitState State { get; init; }
    public int FailureCount { get; init; }
    public DateTime LastFailureTime { get; init; }
    public int FailureThreshold { get; init; }
}

/// <summary>
/// Service for managing multiple circuit breakers
/// </summary>
public class CircuitBreakerService : ICircuitBreakerService
{
    private readonly ConcurrentDictionary<string, CircuitBreaker> _circuitBreakers = new();
    private readonly ILogger<CircuitBreakerService> _logger;

    public CircuitBreakerService(ILogger<CircuitBreakerService> logger)
    {
        _logger = logger;
    }

    public CircuitBreaker GetCircuitBreaker(string name, int? failureThreshold = null, TimeSpan? timeout = null, TimeSpan? retryDelay = null)
    {
        return _circuitBreakers.GetOrAdd(name, key =>
        {
            CircuitBreaker cb = new(
                failureThreshold ?? 5,
                timeout ?? TimeSpan.FromMinutes(1),
                retryDelay ?? TimeSpan.FromSeconds(30),
                null); // Pass null for logger since we can't convert types
            cb.Name = key;
            return cb;
        });
    }

    public IEnumerable<CircuitBreakerMetrics> GetAllMetrics()
    {
        return _circuitBreakers.Values.Select(cb => cb.GetMetrics());
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

/// <summary>
/// Interface for circuit breaker service
/// </summary>
public interface ICircuitBreakerService
{
    CircuitBreaker GetCircuitBreaker(string name, int? failureThreshold = null, TimeSpan? timeout = null, TimeSpan? retryDelay = null);
    IEnumerable<CircuitBreakerMetrics> GetAllMetrics();
    void ResetAll();
}
