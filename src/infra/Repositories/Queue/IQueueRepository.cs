using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Repositories.Queue;

/// <summary>
/// Repository for print job queue and related entity persistence and retrieval.
/// Provides both basic CRUD operations and specialized queries for queue management.
/// </summary>
public interface IQueueRepository
{
    /// <summary>
    /// Retrieves all print jobs in the queue database without any filtering.
    /// </summary>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>List of all print jobs regardless of status</returns>
    Task<List<PrintJob>> GetAllAsync(CancellationToken ct);

    /// <summary>
    /// Finds a single print job by its unique identifier.
    /// </summary>
    /// <param name="id">The unique identifier of the print job to find</param>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>The print job if found; otherwise null</returns>
    Task<PrintJob?> FindByIdAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Adds a new print job to the queue and persists it immediately.
    /// </summary>
    /// <param name="item">The print job to add</param>
    /// <param name="ct">Cancellation token for async operation</param>
    Task AddAsync(PrintJob item, CancellationToken ct);

    /// <summary>
    /// Removes a print job from the queue and persists the deletion immediately.
    /// </summary>
    /// <param name="item">The print job to remove</param>
    /// <param name="ct">Cancellation token for async operation</param>
    Task RemoveAsync(PrintJob item, CancellationToken ct);

    /// <summary>
    /// Persists all pending changes to the database in a single atomic transaction.
    /// </summary>
    /// <param name="ct">Cancellation token for async operation</param>
    Task SaveChangesAsync(CancellationToken ct);

    // Specialized queue query methods

    /// <summary>
    /// Get all printers that are available for print job assignment.
    /// </summary>
    /// <param name="ct">Cancellation token for async operation</param>
    Task<List<Printer>> GetAvailablePrintersAsync(CancellationToken ct);

    /// <summary>
    /// Get all printers that are available and compatible with the specified model name or alias.
    /// Matches against both canonical model names and slicer-specific aliases (e.g., "COREONEL").
    /// </summary>
    /// <param name="modelNameOrAlias">The printer model name or slicer alias to match</param>
    /// <param name="ct">Cancellation token for async operation</param>
    Task<List<Printer>> GetCompatiblePrintersAsync(string modelNameOrAlias, CancellationToken ct);

    /// <summary>
    /// Get all print jobs assigned to a specific printer, ordered by priority and queue time.
    /// </summary>
    /// <param name="printerId">The printer ID to get jobs for</param>
    /// <param name="ct">Cancellation token for async operation</param>
    Task<List<PrintJob>> GetPrintJobsForPrinterAsync(Guid printerId, CancellationToken ct);

    /// <summary>
    /// Get the currently printing or starting job for a printer.
    /// </summary>
    /// <param name="printerId">The printer ID to get the current job for</param>
    /// <param name="ct">Cancellation token for async operation</param>
    Task<PrintJob?> GetCurrentJobForPrinterAsync(Guid printerId, CancellationToken ct);

    /// <summary>
    /// Get a gcode file by ID.
    /// </summary>
    /// <param name="id">The gcode file ID</param>
    /// <param name="ct">Cancellation token for async operation</param>
    Task<GcodeFile?> GetGcodeFileAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Get a print job by ID with all related entities.
    /// </summary>
    /// <param name="id">The print job ID</param>
    /// <param name="ct">Cancellation token for async operation</param>
    Task<PrintJob?> GetPrintJobByIdAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Count queued or assigned jobs for a specific printer.
    /// </summary>
    /// <param name="printerId">The printer ID to count jobs for</param>
    /// <param name="ct">Cancellation token for async operation</param>
    Task<int> CountQueuedJobsForPrinterAsync(Guid printerId, CancellationToken ct);

    /// <summary>
    /// Get the next queue position for a printer's queue.
    /// </summary>
    /// <param name="printerId">The printer ID to get the next position for</param>
    /// <param name="ct">Cancellation token for async operation</param>
    Task<int> GetNextQueuePositionAsync(Guid printerId, CancellationToken ct);

    /// <summary>
    /// Get the next global queue position for unassigned jobs.
    /// </summary>
    /// <param name="ct">Cancellation token for async operation</param>
    Task<int> GetNextGlobalQueuePositionAsync(CancellationToken ct);

    /// <summary>
    /// Count active jobs (queued, assigned, starting, or printing) using a specific gcode file.
    /// </summary>
    /// <param name="gcodeFileId">The gcode file ID to check</param>
    /// <param name="ct">Cancellation token for async operation</param>
    Task<int> CountActiveJobsUsingGcodeAsync(Guid gcodeFileId, CancellationToken ct);
}
