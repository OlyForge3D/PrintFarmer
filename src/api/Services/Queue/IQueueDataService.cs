using Farm.Infrastructure.Domain;

namespace Farm.Web.Api.Services.Queue;

/// <summary>
/// Service that provides domain-specific query methods for print job queue management.
/// Wraps the basic IQueueRepository with specialized queries for queue operations.
/// </summary>
public interface IQueueDataService
{
    /// <summary>
    /// Get all printers that are available for print job assignment.
    /// </summary>
    /// <param name="ct">Cancellation token for the operation.</param>
    Task<List<Printer>> GetAvailablePrintersAsync(CancellationToken ct);

    /// <summary>
    /// Get all printers that are available and compatible with the specified model name or alias.
    /// Matches against both canonical model names and slicer-specific aliases (e.g., "COREONEL").
    /// </summary>
    /// <param name="modelNameOrAlias">The printer model name or slicer alias to match</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    Task<List<Printer>> GetCompatiblePrintersAsync(string modelNameOrAlias, CancellationToken ct);

    /// <summary>
    /// Get all print jobs assigned to a specific printer, ordered by priority and queue time.
    /// </summary>
    /// <param name="printerId">The unique identifier of the printer.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    Task<List<PrintJob>> GetPrintJobsForPrinterAsync(Guid printerId, CancellationToken ct);

    /// <summary>
    /// Get the currently printing or starting job for a printer.
    /// </summary>
    /// <param name="printerId">The unique identifier of the printer.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    Task<PrintJob?> GetCurrentJobForPrinterAsync(Guid printerId, CancellationToken ct);

    /// <summary>
    /// Get a gcode file by ID.
    /// </summary>
    /// <param name="id">The unique identifier of the gcode file.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    Task<GcodeFile?> GetGcodeFileAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Get a print job by ID with all related entities.
    /// </summary>
    /// <param name="id">The unique identifier of the print job.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    Task<PrintJob?> GetPrintJobByIdAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Count queued or assigned jobs for a specific printer.
    /// </summary>
    /// <param name="printerId">The unique identifier of the printer.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    Task<int> CountQueuedJobsForPrinterAsync(Guid printerId, CancellationToken ct);

    /// <summary>
    /// Get the next queue position for a printer's queue.
    /// </summary>
    /// <param name="printerId">The unique identifier of the printer.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    Task<int> GetNextQueuePositionAsync(Guid printerId, CancellationToken ct);

    /// <summary>
    /// Get all print jobs in the queue with all related entities.
    /// </summary>
    /// <param name="ct">Cancellation token for the operation.</param>
    Task<List<PrintJob>> GetAllPrintJobsAsync(CancellationToken ct);

    /// <summary>
    /// Get the next global queue position for unassigned jobs.
    /// </summary>
    /// <param name="ct">Cancellation token for the operation.</param>
    Task<int> GetNextGlobalQueuePositionAsync(CancellationToken ct);

    /// <summary>
    /// Count active jobs (queued, assigned, starting, or printing) using a specific gcode file.
    /// </summary>
    /// <param name="gcodeFileId">The unique identifier of the gcode file.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    Task<int> CountActiveJobsUsingGcodeAsync(Guid gcodeFileId, CancellationToken ct);
}
