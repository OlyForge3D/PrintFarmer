using System;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.UnitOfWork;
using Farm.Infrastructure.Telemetry;

namespace Farm.Web.Api.Services.FolderManagement
{
    /// <summary>
    /// Service for managing folder hierarchies and directory structures for 3D model files and GCode files.
    /// </summary>
    /// <remarks>
    /// This service provides folder organization capabilities including:
    /// - Creating folder hierarchies for organizing 3D models and GCode files
    /// - Retrieving existing folder structures or creating them on demand
    /// - Managing folder types (gcode, models, etc.) for proper categorization
    /// - Supporting path-based folder organization for file system structure
    /// - Maintaining referential integrity through repository pattern with atomic transactions
    /// 
    /// All operations use IUnitOfWork to ensure consistency across folder and file operations
    /// through shared DbContext, preventing foreign key constraint violations.
    /// </remarks>
    public class FolderManagementService : IFolderManagementService
    {
        private readonly IUnitOfWork _unitOfWork;
        private readonly IUnifiedLoggingService _logger;

        /// <summary>
        /// Initializes a new instance of the FolderManagementService with required dependencies.
        /// </summary>
        /// <param name="unitOfWork">Unit of Work providing coordinated access to all repositories with shared DbContext</param>
        /// <param name="logger">Unified logging service for operation tracking and debugging</param>
        /// <exception cref="ArgumentNullException">Thrown when any required dependency is null</exception>
        public FolderManagementService(IUnitOfWork unitOfWork, IUnifiedLoggingService logger)
        {
            _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Get an existing folder or create it if it doesn't exist.
        /// </summary>
        public async Task<FolderNode> GetOrCreateFolderAsync(string directoryPath, string folderType, CancellationToken ct)
        {
            return await _unitOfWork.Folders.GetOrCreateFolderAsync(directoryPath, folderType, ct);
        }
    }
}
