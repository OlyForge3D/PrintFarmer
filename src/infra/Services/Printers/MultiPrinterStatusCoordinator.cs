using Farm.Infrastructure.Domain;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.Printers;

/// <summary>
/// Coordinates parallel execution of printer status operations.
/// Handles Task.WhenAll orchestration, per-printer error handling, and timeout management.
/// </summary>
public class MultiPrinterStatusCoordinator : IMultiPrinterStatusCoordinator
{
    private readonly ILogger<MultiPrinterStatusCoordinator> _logger;

    public MultiPrinterStatusCoordinator(ILogger<MultiPrinterStatusCoordinator> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public async Task<TResult?[]> ExecuteParallelAsync<TResult>(
        IEnumerable<Printer> printers,
        Func<Printer, CancellationToken, Task<TResult>> operation,
        Action<Printer, Exception> onError)
        where TResult : class
    {
        return await ExecuteParallelAsync(printers, operation, onError, CancellationToken.None);
    }

    public async Task<TResult?[]> ExecuteParallelAsync<TResult>(
        IEnumerable<Printer> printers,
        Func<Printer, CancellationToken, Task<TResult>> operation,
        Action<Printer, Exception> onError,
        CancellationToken ct)
        where TResult : class
    {
        ArgumentNullException.ThrowIfNull(printers);
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(onError);

        List<Printer> printerList = printers as List<Printer> ?? printers.ToList();
        if (printerList.Count == 0)
        {
            return Array.Empty<TResult>();
        }

        IEnumerable<Task<TResult?>> tasks = printerList.Select(async p =>
        {
            try
            {
                return await operation(p, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError("Parallel operation failed for printer {PName} ({PId}): {Message}", p.Name, p.Id, ex.Message);
                onError(p, ex);
                return null;
            }
        });

        TResult?[] results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return results;
    }

    public async Task<TResult?[]> ExecuteParallelWithTimeoutAsync<TResult>(
        IEnumerable<Printer> printers,
        Func<Printer, CancellationToken, Task<TResult>> operation,
        TimeSpan timeout,
        Action<Printer> onTimeout,
        Action<Printer, Exception> onError)
        where TResult : class
    {
        return await ExecuteParallelWithTimeoutAsync(printers, operation, timeout, onTimeout, onError, CancellationToken.None);
    }

    public async Task<TResult?[]> ExecuteParallelWithTimeoutAsync<TResult>(
        IEnumerable<Printer> printers,
        Func<Printer, CancellationToken, Task<TResult>> operation,
        TimeSpan timeout,
        Action<Printer> onTimeout,
        Action<Printer, Exception> onError,
        CancellationToken ct)
        where TResult : class
    {
        ArgumentNullException.ThrowIfNull(printers);
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(onTimeout);
        ArgumentNullException.ThrowIfNull(onError);

        List<Printer> printerList = printers as List<Printer> ?? printers.ToList();
        if (printerList.Count == 0)
        {
            return Array.Empty<TResult>();
        }

        // Create a timeout CTS linked to the provided cancellation token
        using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        IEnumerable<Task<TResult?>> tasks = printerList.Select(async p =>
        {
            try
            {
                return await operation(p, timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeoutCts.Token.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                // This is a timeout (not external cancellation)
                _logger.LogWarning("Timeout occurred for printer {PName} ({PId}) after {TimeoutTotalSeconds:F1}s", p.Name, p.Id, timeout.TotalSeconds);
                onTimeout(p);
                return null;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // External cancellation
                _logger.LogInformation("Operation cancelled for printer {PName} ({PId})", p.Name, p.Id);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError("Parallel operation failed for printer {PName} ({PId}): {Message}", p.Name, p.Id, ex.Message);
                onError(p, ex);
                return null;
            }
        });

        TResult?[] results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return results;
    }
}
