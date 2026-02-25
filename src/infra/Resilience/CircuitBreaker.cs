using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Farm.Infrastructure;

public class CircuitBreaker(int failureThreshold = 5, TimeSpan? timeout = null, TimeSpan? retryDelay = null, ILogger? logger = null)
{
    private readonly int _failureThreshold = failureThreshold;
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromMinutes(1);
    private readonly TimeSpan _retryDelay = retryDelay ?? TimeSpan.FromSeconds(30);
    private readonly ILogger _logger = logger ?? NullLogger<CircuitBreaker>.Instance;

    private readonly Lock _lock = new();
    private int _failureCount;
    private DateTime _lastFailureTime;
    private CircuitState _state = CircuitState.Closed;

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

    public async Task ExecuteAsync(Func<CancellationToken, Task> operation, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        _ = await ExecuteAsync<object?>(
            async token =>
            {
                await operation(token);
                return null;
            },
            ct);
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

            _logger.LogWarning("Circuit breaker {Name} recorded failure {FailureCount}/{FailureThreshold}: {Message}", Name, _failureCount, _failureThreshold, ex.Message);

            if (_failureCount >= _failureThreshold)
            {
                _state = CircuitState.Open;
                _logger.LogError("Circuit breaker {Name} is now OPEN after {FailureCount} consecutive failures", Name, _failureCount);
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
            _logger.LogInformation("Circuit breaker {Name} manually reset", Name);
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
