using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.SystemLogs;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace Farm.Infrastructure.Logging;

/// <summary>
/// ILoggerProvider that writes all application logs to the SystemLog database table.
/// Uses a background queue to batch writes asynchronously.
/// Automatically captures X-Correlation-Id from HTTP context for tracing.
/// </summary>
public class SystemLogLoggerProvider : ILoggerProvider
{
    private readonly IServiceProvider _serviceProvider;
    private readonly LogLevel _minimumLevel;
    private readonly BlockingCollection<SystemLog> _logQueue;
    private readonly CancellationTokenSource _cts;
    private readonly Task _processingTask;

    public SystemLogLoggerProvider(IServiceProvider serviceProvider, LogLevel minimumLevel = LogLevel.Information)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _minimumLevel = minimumLevel;
        _logQueue = new BlockingCollection<SystemLog>(1000);
        _cts = new CancellationTokenSource();

        // Don't start the processing task immediately - wait for service provider to be fully configured
        _processingTask = ProcessLogsAsync(_cts.Token);
    }

    public ILogger CreateLogger(string categoryName)
    {
        IHttpContextAccessor? httpContextAccessor = _serviceProvider.GetService<IHttpContextAccessor>();
        return new SystemLogLogger(categoryName, _logQueue, _minimumLevel, httpContextAccessor);
    }

    /// <summary>
    /// Processes queued logs and writes them to the database in batches.
    /// </summary>
    private async Task ProcessLogsAsync(CancellationToken ct)
    {
        var batch = new List<SystemLog>(capacity: 50);

        // Add a small delay at the start to let the application fully initialize
        await Task.Delay(2000, ct).ConfigureAwait(false);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Try to get a log from the queue with a timeout
                if (_logQueue.TryTake(out SystemLog? log, 1000, ct))
                {
                    batch.Add(log);

                    // Process batch when we have 50 items or timeout
                    if (batch.Count >= 50)
                    {
                        await WriteBatchAsync(batch, ct);
                        batch.Clear();
                    }
                }
                else if (batch.Count > 0)
                {
                    // Timeout occurred, write any pending logs
                    await WriteBatchAsync(batch, ct);
                    batch.Clear();
                }
            }

            // Flush remaining logs on shutdown
            if (batch.Count > 0)
            {
                await WriteBatchAsync(batch, CancellationToken.None);
            }

            // Drain any remaining items in queue
            while (_logQueue.TryTake(out SystemLog? log, 100))
            {
                batch.Add(log);
                if (batch.Count >= 50)
                {
                    await WriteBatchAsync(batch, CancellationToken.None);
                    batch.Clear();
                }
            }

            if (batch.Count > 0)
            {
                await WriteBatchAsync(batch, CancellationToken.None);
            }
        }
        catch
        {
            // Silently fail - don't let logging errors break the application
        }
    }

    /// <summary>
    /// Writes a batch of logs to the database.
    /// </summary>
    private async Task WriteBatchAsync(List<SystemLog> batch, CancellationToken ct)
    {
        try
        {
            // Create a scope to get the repository
            using AsyncServiceScope scope = _serviceProvider.CreateAsyncScope();
            ISystemLogRepository? repository = scope.ServiceProvider.GetService<ISystemLogRepository>();

            if (repository == null)
            {
                return; // Repository not available yet
            }

            foreach (SystemLog log in batch)
            {
                try
                {
                    await repository.AddAsync(log, ct);
                }
                catch
                {
                    // Individual log write failed, continue with others
                }
            }
        }
        catch
        {
            // Silently fail - don't let logging errors break the application
        }
    }

    /// <summary>
    /// Disposes the logger provider, stopping the background processing task and cleaning up resources.
    /// </summary>
    public void Dispose()
    {
        try
        {
            _logQueue.CompleteAdding();
            _cts.Cancel();

            // Wait for processing to complete with timeout
            try
            {
#pragma warning disable VSTHRD002 // Synchronous waiting is unavoidable in Dispose - we must complete resource cleanup
                // Give the processing task a brief window to complete gracefully
                _processingTask?.Wait(TimeSpan.FromMilliseconds(500));
#pragma warning restore VSTHRD002
            }
            catch (OperationCanceledException)
            {
                // Expected when cancellation is requested
            }
            catch
            {
                // Ignore timeout or other wait exceptions
            }
        }
        catch
        {
            // Ignore disposal errors
        }
        finally
        {
            _logQueue.Dispose();
            _cts.Dispose();

            // Tasks don't need explicit disposal in modern .NET
            // Disposing incomplete tasks throws InvalidOperationException in .NET 10+
        }
    }
}

/// <summary>
/// Logger that queues log messages for batch processing.
/// Extracts correlation ID from HTTP context for distributed tracing.
/// </summary>
internal class SystemLogLogger(string categoryName, BlockingCollection<SystemLog> logQueue, LogLevel minimumLevel, IHttpContextAccessor? httpContextAccessor = null) : ILogger
{
    private readonly string _categoryName = categoryName;
    private readonly BlockingCollection<SystemLog> _logQueue = logQueue;
    private readonly LogLevel _minimumLevel = minimumLevel;
    private readonly IHttpContextAccessor? _httpContextAccessor = httpContextAccessor;

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
    {
        return null; // Scopes not used for this implementation
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        return logLevel >= _minimumLevel;
    }

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        try
        {
            string message = formatter(state, exception);
            string? exceptionText = exception?.ToString();

            // Try to extract correlation ID from HTTP context
            string? correlationId = GetCorrelationIdFromContext();

            var log = new SystemLog
            {
                Timestamp = DateTime.UtcNow,
                Level = logLevel.ToString(),
                Message = message,
                Exception = exceptionText,
                Source = _categoryName,
                CorrelationId = correlationId
            };

            // Non-blocking add to queue
            _logQueue.TryAdd(log, 100);
        }
        catch
        {
            // Silently fail - don't let logging errors break the application
        }
    }

    private string? GetCorrelationIdFromContext()
    {
        try
        {
            HttpContext? httpContext = _httpContextAccessor?.HttpContext;
            if (httpContext != null)
            {
                // Check for X-Correlation-Id header (sent by frontend)
                if (httpContext.Request.Headers.TryGetValue("X-Correlation-Id", out StringValues correlationId))
                {
                    return correlationId.ToString();
                }

                // Fallback to X-Request-Id if available
                if (httpContext.Request.Headers.TryGetValue("X-Request-Id", out StringValues requestId))
                {
                    return requestId.ToString();
                }
            }
        }
        catch
        {
            // Silently fail - correlation ID is nice to have but not critical
        }

        return null;
    }
}
