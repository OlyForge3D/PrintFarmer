using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Services.Printers;

/// <summary>
/// Manages timeout and fallback logic for printer status operations.
/// Encapsulates circuit breaker management, timeout handling, and error recovery patterns.
/// Separates resilience concerns from status retrieval logic.
/// </summary>
public interface IPrinterStatusFallbackService
{
    /// <summary>
    /// Executes a printer operation with timeout and fallback handling.
    /// Includes circuit breaker management and graceful error recovery.
    /// </summary>
    /// <typeparam name="TResult">The result type of the operation</typeparam>
    /// <param name="printer">The printer to get status for</param>
    /// <param name="operation">The async operation to execute (e.g., status retrieval)</param>
    /// <param name="timeout">The timeout duration for the operation</param>
    /// <param name="fallbackFactory">Factory to create fallback result on timeout/error</param>
    /// <returns>Either the operation result or fallback result on error</returns>
    Task<TResult> ExecuteWithFallbackAsync<TResult>(
        Printer printer,
        Func<CancellationToken, Task<TResult>> operation,
        TimeSpan timeout,
        Func<TResult> fallbackFactory)
        where TResult : class;

    /// <summary>
    /// Executes a printer operation with timeout and fallback handling, supporting cancellation.
    /// Includes circuit breaker management and graceful error recovery.
    /// </summary>
    /// <typeparam name="TResult">The result type of the operation</typeparam>
    /// <param name="printer">The printer to get status for</param>
    /// <param name="operation">The async operation to execute (e.g., status retrieval)</param>
    /// <param name="timeout">The timeout duration for the operation</param>
    /// <param name="fallbackFactory">Factory to create fallback result on timeout/error</param>
    /// <param name="ct">Cancellation token for external cancellation support</param>
    /// <returns>Either the operation result or fallback result on error</returns>
    Task<TResult> ExecuteWithFallbackAsync<TResult>(
        Printer printer,
        Func<CancellationToken, Task<TResult>> operation,
        TimeSpan timeout,
        Func<TResult> fallbackFactory,
        CancellationToken ct)
        where TResult : class;

    /// <summary>
    /// Executes a printer operation with circuit breaker and timeout handling.
    /// Uses the circuit breaker to manage reliability across multiple calls.
    /// </summary>
    /// <typeparam name="TResult">The result type of the operation</typeparam>
    /// <param name="printer">The printer to get status for</param>
    /// <param name="circuitBreakerKey">Unique key for circuit breaker state (e.g., "moonraker-{printerId}")</param>
    /// <param name="operation">The async operation to execute</param>
    /// <param name="timeout">The timeout duration for the operation</param>
    /// <param name="fallbackFactory">Factory to create fallback result on circuit breaker open/timeout/error</param>
    /// <returns>Either the operation result or fallback result on error/circuit breaker open</returns>
    Task<TResult> ExecuteWithCircuitBreakerAsync<TResult>(
        Printer printer,
        string circuitBreakerKey,
        Func<CancellationToken, Task<TResult>> operation,
        TimeSpan timeout,
        Func<TResult> fallbackFactory)
        where TResult : class;

    /// <summary>
    /// Executes a printer operation with circuit breaker and timeout handling, supporting cancellation.
    /// Uses the circuit breaker to manage reliability across multiple calls.
    /// </summary>
    /// <typeparam name="TResult">The result type of the operation</typeparam>
    /// <param name="printer">The printer to get status for</param>
    /// <param name="circuitBreakerKey">Unique key for circuit breaker state (e.g., "moonraker-{printerId}")</param>
    /// <param name="operation">The async operation to execute</param>
    /// <param name="timeout">The timeout duration for the operation</param>
    /// <param name="fallbackFactory">Factory to create fallback result on circuit breaker open/timeout/error</param>
    /// <param name="ct">Cancellation token for external cancellation support</param>
    /// <returns>Either the operation result or fallback result on error/circuit breaker open</returns>
    Task<TResult> ExecuteWithCircuitBreakerAsync<TResult>(
        Printer printer,
        string circuitBreakerKey,
        Func<CancellationToken, Task<TResult>> operation,
        TimeSpan timeout,
        Func<TResult> fallbackFactory,
        CancellationToken ct)
        where TResult : class;

    /// <summary>
    /// Checks if a circuit breaker is open for a given printer backend.
    /// </summary>
    /// <param name="circuitBreakerKey">The circuit breaker key</param>
    /// <returns>True if circuit breaker is open; otherwise false</returns>
    bool IsCircuitBreakerOpen(string circuitBreakerKey);

    /// <summary>
    /// Gets the circuit breaker state for monitoring and debugging.
    /// </summary>
    /// <param name="circuitBreakerKey">The circuit breaker key</param>
    /// <returns>CircuitBreaker state object, or null if not found</returns>
    CircuitBreaker? GetCircuitBreakerState(string circuitBreakerKey);

    /// <summary>
    /// Resets a circuit breaker to its initial state.
    /// Useful for recovery after issues are resolved.
    /// </summary>
    /// <param name="circuitBreakerKey">The circuit breaker key to reset</param>
    void ResetCircuitBreaker(string circuitBreakerKey);
}

/// <summary>
/// Simple circuit breaker state management.
/// Tracks open/closed state and failure counts.
/// </summary>
public class CircuitBreakerState
{
    public bool IsOpen { get; set; }

    public int FailureCount { get; set; }

    public DateTime? LastFailureTime { get; set; }
}
