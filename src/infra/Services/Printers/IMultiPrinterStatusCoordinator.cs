using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Telemetry;

namespace Farm.Infrastructure.Services.Printers
{
    /// <summary>
    /// Coordinates parallel execution of printer status operations across multiple printers.
    /// Manages Task.WhenAll orchestration, per-printer error handling, and result aggregation.
    /// Isolates multi-printer coordination logic from business rules.
    /// </summary>
    public interface IMultiPrinterStatusCoordinator
    {
        /// <summary>
        /// Executes parallel operations for multiple printers and aggregates results.
        /// Handles per-printer timeouts, exceptions, and fallback values gracefully.
        /// </summary>
        /// <typeparam name="TResult">The result type for each printer operation</typeparam>
        /// <param name="printers">Collection of printers to process</param>
        /// <param name="operation">Async operation to execute per printer</param>
        /// <param name="onError">Error handler called when a printer operation fails (receives printer and exception)</param>
        /// <returns>Array of results in the same order as input printers</returns>
        Task<TResult?[]> ExecuteParallelAsync<TResult>(
            IEnumerable<Printer> printers,
            Func<Printer, CancellationToken, Task<TResult>> operation,
            Action<Printer, Exception> onError)
            where TResult : class;

        /// <summary>
        /// Executes parallel operations with cancellation support.
        /// </summary>
        /// <typeparam name="TResult">The result type for each printer operation</typeparam>
        /// <param name="printers">Collection of printers to process</param>
        /// <param name="operation">Async operation to execute per printer</param>
        /// <param name="onError">Error handler for failures</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>Array of results in the same order as input printers</returns>
        Task<TResult?[]> ExecuteParallelAsync<TResult>(
            IEnumerable<Printer> printers,
            Func<Printer, CancellationToken, Task<TResult>> operation,
            Action<Printer, Exception> onError,
            CancellationToken ct)
            where TResult : class;

        /// <summary>
        /// Executes parallel operations with timeout protection for each printer.
        /// </summary>
        /// <typeparam name="TResult">The result type for each printer operation</typeparam>
        /// <param name="printers">Collection of printers to process</param>
        /// <param name="operation">Async operation to execute per printer</param>
        /// <param name="timeout">Timeout duration per printer</param>
        /// <param name="onTimeout">Timeout handler called when a printer operation times out</param>
        /// <param name="onError">Error handler for other failures</param>
        /// <returns>Array of results in the same order as input printers</returns>
        Task<TResult?[]> ExecuteParallelWithTimeoutAsync<TResult>(
            IEnumerable<Printer> printers,
            Func<Printer, CancellationToken, Task<TResult>> operation,
            TimeSpan timeout,
            Action<Printer> onTimeout,
            Action<Printer, Exception> onError)
            where TResult : class;

        /// <summary>
        /// Executes parallel operations with timeout protection and cancellation support.
        /// </summary>
        /// <typeparam name="TResult">The result type for each printer operation</typeparam>
        /// <param name="printers">Collection of printers to process</param>
        /// <param name="operation">Async operation to execute per printer</param>
        /// <param name="timeout">Timeout duration per printer</param>
        /// <param name="onTimeout">Timeout handler called when a printer operation times out</param>
        /// <param name="onError">Error handler for other failures</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>Array of results in the same order as input printers</returns>
        Task<TResult?[]> ExecuteParallelWithTimeoutAsync<TResult>(
            IEnumerable<Printer> printers,
            Func<Printer, CancellationToken, Task<TResult>> operation,
            TimeSpan timeout,
            Action<Printer> onTimeout,
            Action<Printer, Exception> onError,
            CancellationToken ct)
            where TResult : class;
    }
}
