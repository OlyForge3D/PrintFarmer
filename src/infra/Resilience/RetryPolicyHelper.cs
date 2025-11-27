using Farm.Infrastructure.Telemetry;

namespace Farm.Infrastructure.Resilience;

/// <summary>
/// Helper for implementing retry policies with exponential backoff
/// </summary>
public static class RetryPolicyHelper
{
    /// <summary>
    /// Execute an asynchronous operation with retry logic and exponential backoff
    /// </summary>
    /// <typeparam name="T">Return type of the operation</typeparam>
    /// <param name="operation">Function to execute with retry</param>
    /// <param name="maxRetries">Maximum number of retry attempts (default: 3)</param>
    /// <param name="initialDelayMs">Initial delay in milliseconds (default: 500)</param>
    /// <param name="logger">Optional logger to log retry attempts (IUnifiedLoggingService)</param>
    /// <param name="operationName">Name of the operation for logging</param>
    /// <returns>Result of the operation</returns>
    /// <exception cref="InvalidOperationException">Thrown when all retry attempts fail</exception>
    public static async Task<T> ExecuteWithRetryAsync<T>(
        Func<Task<T>> operation,
        int maxRetries = 3,
        int initialDelayMs = 500,
        IUnifiedLoggingService? logger = null,
        string operationName = "operation")
    {
        ArgumentNullException.ThrowIfNull(operation);

        int retryCount = 0;
        Exception? lastException = null;

        while (retryCount <= maxRetries)
        {
            try
            {
                if (retryCount > 0)
                {
                    // Calculate exponential backoff delay
                    int delay = initialDelayMs * (int)Math.Pow(2, retryCount - 1);
                    logger?.LogInformation($"Retry {retryCount}/{maxRetries} for {operationName} after {delay}ms");
                    await Task.Delay(delay);
                }

                // Attempt operation
                return await operation();
            }
            catch (Exception ex)
            {
                lastException = ex;
                retryCount++;

                if (retryCount <= maxRetries)
                {
                    logger?.LogWarning(ex, $"{operationName} failed (attempt {retryCount}/{maxRetries}): {ex.Message}");
                }
                else
                {
                    logger?.LogError(ex, $"{operationName} failed after {maxRetries} attempts: {ex.Message}");
                }
            }
        }

        throw new InvalidOperationException($"{operationName} failed after {maxRetries} attempts", lastException);
    }

    /// <summary>
    /// Execute an asynchronous operation with retry logic and exponential backoff that returns no result
    /// </summary>
    /// <param name="operation">Action to execute with retry</param>
    /// <param name="maxRetries">Maximum number of retry attempts (default: 3)</param>
    /// <param name="initialDelayMs">Initial delay in milliseconds (default: 500)</param>
    /// <param name="logger">Optional logger to log retry attempts (IUnifiedLoggingService)</param>
    /// <param name="operationName">Name of the operation for logging</param>
    /// <exception cref="InvalidOperationException">Thrown when all retry attempts fail</exception>
    public static async Task ExecuteWithRetryAsync(
        Func<Task> operation,
        int maxRetries = 3,
        int initialDelayMs = 500,
        IUnifiedLoggingService? logger = null,
        string operationName = "operation")
    {
        ArgumentNullException.ThrowIfNull(operation);

        int retryCount = 0;
        Exception? lastException = null;

        while (retryCount <= maxRetries)
        {
            try
            {
                if (retryCount > 0)
                {
                    // Calculate exponential backoff delay
                    int delay = initialDelayMs * (int)Math.Pow(2, retryCount - 1);
                    logger?.LogInformation($"Retry {retryCount}/{maxRetries} for {operationName} after {delay}ms");
                    await Task.Delay(delay);
                }

                // Attempt operation
                await operation();
                return;
            }
            catch (Exception ex)
            {
                lastException = ex;
                retryCount++;

                if (retryCount <= maxRetries)
                {
                    logger?.LogWarning(ex, $"{operationName} failed (attempt {retryCount}/{maxRetries}): {ex.Message}");
                }
                else
                {
                    logger?.LogError(ex, $"{operationName} failed after {maxRetries} attempts: {ex.Message}");
                }
            }
        }

        throw new InvalidOperationException($"{operationName} failed after {maxRetries} attempts", lastException);
    }
}
