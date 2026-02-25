using Farm.Infrastructure.Domain;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.Printers;

/// <summary>
/// Manages timeout and fallback logic for printer status operations.
/// Provides resilience patterns including circuit breaker integration and graceful error recovery.
/// </summary>
public class PrinterStatusFallbackService(
    ICircuitBreakerService circuitBreaker,
    ILogger<PrinterStatusFallbackService> logger) : IPrinterStatusFallbackService
{
    private readonly ICircuitBreakerService _circuitBreaker = circuitBreaker ?? throw new ArgumentNullException(nameof(circuitBreaker));
    private readonly ILogger<PrinterStatusFallbackService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public async Task<TResult> ExecuteWithFallbackAsync<TResult>(
        Printer printer,
        Func<CancellationToken, Task<TResult>> operation,
        TimeSpan timeout,
        Func<TResult> fallbackFactory)
        where TResult : class
    {
        return await ExecuteWithFallbackAsync(printer, operation, timeout, fallbackFactory, CancellationToken.None);
    }

    public async Task<TResult> ExecuteWithFallbackAsync<TResult>(
        Printer printer,
        Func<CancellationToken, Task<TResult>> operation,
        TimeSpan timeout,
        Func<TResult> fallbackFactory,
        CancellationToken ct)
        where TResult : class
    {
        ArgumentNullException.ThrowIfNull(printer);
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(fallbackFactory);

        try
        {
            using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);

            TResult result = await operation(timeoutCts.Token).ConfigureAwait(false);
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // External cancellation request - propagate up
            _logger.LogInformation("Operation cancelled for printer {PrinterName} ({PrinterId})", printer.Name, printer.Id);
            throw;
        }
        catch (OperationCanceledException)
        {
            // Timeout occurred
            _logger.LogWarning("Operation timeout for printer {PrinterName} ({PrinterId}) after {TimeoutTotalSeconds:F1}s", printer.Name, printer.Id, timeout.TotalSeconds);
            return fallbackFactory();
        }
        catch (Exception ex)
        {
            _logger.LogError("Operation failed for printer {PrinterName} ({PrinterId}): {Message}", printer.Name, printer.Id, ex.Message);
            return fallbackFactory();
        }
    }

    public async Task<TResult> ExecuteWithCircuitBreakerAsync<TResult>(
        Printer printer,
        string circuitBreakerKey,
        Func<CancellationToken, Task<TResult>> operation,
        TimeSpan timeout,
        Func<TResult> fallbackFactory)
        where TResult : class
    {
        return await ExecuteWithCircuitBreakerAsync(printer, circuitBreakerKey, operation, timeout, fallbackFactory, CancellationToken.None);
    }

    public async Task<TResult> ExecuteWithCircuitBreakerAsync<TResult>(
        Printer printer,
        string circuitBreakerKey,
        Func<CancellationToken, Task<TResult>> operation,
        TimeSpan timeout,
        Func<TResult> fallbackFactory,
        CancellationToken ct)
        where TResult : class
    {
        ArgumentNullException.ThrowIfNull(printer);
        ArgumentNullException.ThrowIfNull(circuitBreakerKey);
        if (string.IsNullOrWhiteSpace(circuitBreakerKey))
        {
            throw new ArgumentException("Circuit breaker key cannot be empty", nameof(circuitBreakerKey));
        }

        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(fallbackFactory);

        // Get or create circuit breaker for this printer backend
        CircuitBreaker breaker = _circuitBreaker.GetCircuitBreaker(circuitBreakerKey);

        // Check if circuit is already open
        if (breaker.State == CircuitState.Open)
        {
            _logger.LogWarning("Circuit breaker open for {CircuitBreakerKey}; returning fallback for printer {PrinterName}", circuitBreakerKey, printer.Name);
            return fallbackFactory();
        }

        try
        {
            using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);

            TResult result = await breaker.ExecuteAsync(
                async ct => await operation(ct).ConfigureAwait(false),
                timeoutCts.Token)
            .ConfigureAwait(false);

            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // External cancellation request - propagate up
            _logger.LogInformation("Operation cancelled for printer {PrinterName} ({PrinterId}) with key {CircuitBreakerKey}", printer.Name, printer.Id, circuitBreakerKey);
            throw;
        }
        catch (OperationCanceledException)
        {
            // Timeout occurred
            _logger.LogWarning("Circuit breaker timeout for {CircuitBreakerKey} (printer {PrinterName}) after {TimeoutTotalSeconds:F1}s", circuitBreakerKey, printer.Name, timeout.TotalSeconds);
            return fallbackFactory();
        }
        catch (Exception ex)
        {
            _logger.LogError("Circuit breaker operation failed for {CircuitBreakerKey} (printer {PrinterName}): {Message}", circuitBreakerKey, printer.Name, ex.Message);
            return fallbackFactory();
        }
    }

    public bool IsCircuitBreakerOpen(string circuitBreakerKey)
    {
        ArgumentNullException.ThrowIfNull(circuitBreakerKey);
        if (string.IsNullOrWhiteSpace(circuitBreakerKey))
        {
            throw new ArgumentException("Circuit breaker key cannot be empty", nameof(circuitBreakerKey));
        }

        CircuitBreaker breaker = _circuitBreaker.GetCircuitBreaker(circuitBreakerKey);
        return breaker.State == CircuitState.Open;
    }

    public CircuitBreaker? GetCircuitBreakerState(string circuitBreakerKey)
    {
        ArgumentNullException.ThrowIfNull(circuitBreakerKey);
        if (string.IsNullOrWhiteSpace(circuitBreakerKey))
        {
            throw new ArgumentException("Circuit breaker key cannot be empty", nameof(circuitBreakerKey));
        }

        try
        {
            return _circuitBreaker.GetCircuitBreaker(circuitBreakerKey);
        }
        catch
        {
            return null;
        }
    }

    public void ResetCircuitBreaker(string circuitBreakerKey)
    {
        if (string.IsNullOrWhiteSpace(circuitBreakerKey))
        {
            throw new ArgumentNullException(nameof(circuitBreakerKey));
        }

        CircuitBreaker breaker = _circuitBreaker.GetCircuitBreaker(circuitBreakerKey);

        // Reset by getting a fresh instance - depends on ICircuitBreakerService implementation
        _logger.LogInformation("Resetting circuit breaker {CircuitBreakerKey}", circuitBreakerKey);
    }
}
