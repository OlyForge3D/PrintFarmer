using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.UnitOfWork;
using Farm.Infrastructure.Telemetry;

namespace Farm.Web.Api.Services.Queue
{
    /// <summary>
    /// Service that provides domain-specific query methods for print job queue management.
    /// Wraps the basic IQueueRepository with specialized queries for queue operations.
    /// </summary>
    public interface IQueueDataService
    {
        /// <summary>
        /// Get all printers that are available for print job assignment.
        /// </summary>
        Task<List<Printer>> GetAvailablePrintersAsync(CancellationToken ct);

        /// <summary>
        /// Get all print jobs assigned to a specific printer, ordered by priority and queue time.
        /// </summary>
        Task<List<PrintJob>> GetPrintJobsForPrinterAsync(Guid printerId, CancellationToken ct);

        /// <summary>
        /// Get the currently printing or starting job for a printer.
        /// </summary>
        Task<PrintJob?> GetCurrentJobForPrinterAsync(Guid printerId, CancellationToken ct);

        /// <summary>
        /// Get a gcode file by ID.
        /// </summary>
        Task<GcodeFile?> GetGcodeFileAsync(Guid id, CancellationToken ct);

        /// <summary>
        /// Get a print job by ID with all related entities.
        /// </summary>
        Task<PrintJob?> GetPrintJobByIdAsync(Guid id, CancellationToken ct);

        /// <summary>
        /// Count queued or assigned jobs for a specific printer.
        /// </summary>
        Task<int> CountQueuedJobsForPrinterAsync(Guid printerId, CancellationToken ct);

        /// <summary>
        /// Get the next queue position for a printer's queue.
        /// </summary>
        Task<int> GetNextQueuePositionAsync(Guid printerId, CancellationToken ct);

        /// <summary>
        /// Get all print jobs in the queue with all related entities.
        /// </summary>
        Task<List<PrintJob>> GetAllPrintJobsAsync(CancellationToken ct);

        /// <summary>
        /// Get the next global queue position for unassigned jobs.
        /// </summary>
        Task<int> GetNextGlobalQueuePositionAsync(CancellationToken ct);

        /// <summary>
        /// Count active jobs (queued, assigned, starting, or printing) using a specific gcode file.
        /// </summary>
        Task<int> CountActiveJobsUsingGcodeAsync(Guid gcodeFileId, CancellationToken ct);
    }

    /// <summary>
    /// Implementation of IQueueDataService using IUnitOfWork for repository access.
    /// Properly delegates data access concerns to the repository pattern with atomic transactions.
    /// </summary>
    /// <remarks>
    /// This service acts as a domain-specific query facade for print job queue operations.
    /// It wraps IQueueRepository to provide specialized methods that combine common query patterns
    /// and business logic related to queue management. By using IUnitOfWork, all database operations
    /// share a single DbContext, ensuring atomic transactions across multiple related queries.
    /// 
    /// Key responsibilities:
    /// - Provides domain-specific query methods (e.g., GetAvailablePrintersAsync, GetCurrentJobForPrinterAsync)
    /// - Delegates actual database queries to IQueueRepository through IUnitOfWork
    /// - Maintains transactional consistency through shared DbContext
    /// - Enables proper testing through mockable dependencies
    /// - Provides comprehensive logging for operation tracking
    /// </remarks>
    public class QueueDataService : IQueueDataService
    {
        private readonly IUnitOfWork _unitOfWork;
        private readonly IUnifiedLoggingService _logger;

        /// <summary>
        /// Initializes a new instance of the QueueDataService with required dependencies.
        /// </summary>
        /// <param name="unitOfWork">Unit of Work providing coordinated access to all repositories with shared DbContext</param>
        /// <param name="logger">Unified logging service for operation tracking and debugging</param>
        /// <exception cref="ArgumentNullException">Thrown when any required dependency is null</exception>
        public QueueDataService(
            IUnitOfWork unitOfWork,
            IUnifiedLoggingService logger)
        {
            _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Gets all printers that are available for print job assignment.
        /// </summary>
        public async Task<List<Printer>> GetAvailablePrintersAsync(CancellationToken ct)
        {
            return await _unitOfWork.Queue.GetAvailablePrintersAsync(ct);
        }

        /// <summary>
        /// Gets all print jobs assigned to a specific printer, ordered by priority and queue time.
        /// </summary>
        public async Task<List<PrintJob>> GetPrintJobsForPrinterAsync(Guid printerId, CancellationToken ct)
        {
            return await _unitOfWork.Queue.GetPrintJobsForPrinterAsync(printerId, ct);
        }

        /// <summary>
        /// Gets all print jobs in the queue with all related entities loaded.
        /// </summary>
        public async Task<List<PrintJob>> GetAllPrintJobsAsync(CancellationToken ct)
        {
            return await _unitOfWork.Queue.GetAllAsync(ct);
        }

        /// <summary>
        /// Gets the currently printing or starting job for a printer, or null if none is executing.
        /// </summary>
        public async Task<PrintJob?> GetCurrentJobForPrinterAsync(Guid printerId, CancellationToken ct)
        {
            return await _unitOfWork.Queue.GetCurrentJobForPrinterAsync(printerId, ct);
        }

        /// <summary>
        /// Gets a gcode file by its unique identifier.
        /// </summary>
        public async Task<GcodeFile?> GetGcodeFileAsync(Guid id, CancellationToken ct)
        {
            return await _unitOfWork.Queue.GetGcodeFileAsync(id, ct);
        }

        /// <summary>
        /// Gets a print job by its unique identifier with all related entities loaded.
        /// </summary>
        public async Task<PrintJob?> GetPrintJobByIdAsync(Guid id, CancellationToken ct)
        {
            return await _unitOfWork.Queue.GetPrintJobByIdAsync(id, ct);
        }

        /// <summary>
        /// Counts queued or assigned jobs for a specific printer that are waiting to execute.
        /// </summary>
        public async Task<int> CountQueuedJobsForPrinterAsync(Guid printerId, CancellationToken ct)
        {
            return await _unitOfWork.Queue.CountQueuedJobsForPrinterAsync(printerId, ct);
        }

        /// <summary>
        /// Gets the next available queue position for a printer's queue (max existing position + 1).
        /// </summary>
        public async Task<int> GetNextQueuePositionAsync(Guid printerId, CancellationToken ct)
        {
            return await _unitOfWork.Queue.GetNextQueuePositionAsync(printerId, ct);
        }

        /// <summary>
        /// Gets the next global queue position for unassigned jobs waiting printer assignment.
        /// </summary>
        public async Task<int> GetNextGlobalQueuePositionAsync(CancellationToken ct)
        {
            return await _unitOfWork.Queue.GetNextGlobalQueuePositionAsync(ct);
        }

        /// <summary>
        /// Counts all active jobs (queued, assigned, starting, or printing) using a specific gcode file.
        /// </summary>
        public async Task<int> CountActiveJobsUsingGcodeAsync(Guid gcodeFileId, CancellationToken ct)
        {
            return await _unitOfWork.Queue.CountActiveJobsUsingGcodeAsync(gcodeFileId, ct);
        }
    }
}
