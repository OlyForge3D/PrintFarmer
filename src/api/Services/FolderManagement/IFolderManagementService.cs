using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;

namespace Farm.Web.Api.Services.FolderManagement
{
    /// <summary>
    /// Service for managing folder entities in the database.
    /// Provides shared folder creation and retrieval functionality used by multiple file management services.
    /// This prevents cross-service dependencies between GcodeFilesService and Model3DFileService.
    /// </summary>
    public interface IFolderManagementService
    {
        /// <summary>
        /// Get an existing folder or create it if it doesn't exist.
        /// </summary>
        /// <param name="directoryPath">Normalized directory path (e.g., "/" or "/subdir")</param>
        /// <param name="folderType">Type of folder: "gcode", "models", etc.</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>Existing or newly created FolderNode entity</returns>
        Task<FolderNode> GetOrCreateFolderAsync(string directoryPath, string folderType, CancellationToken ct);

        /// <summary>
        /// Get all folder paths recursively for a given folder type (flat list).
        /// Used by file services to build folder hierarchies for UI tree views.
        /// </summary>
        /// <param name="folderType">Type of folder: "gcode", "models", etc.</param>
        /// <param name="parentPath">Optional parent path to limit results (e.g., "/" for root)</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>Sorted list of all folder paths recursively under parent</returns>
        Task<List<string>> GetAllFolderPathsRecursiveAsync(string folderType, string? parentPath = "/", CancellationToken ct = default);
    }
}
