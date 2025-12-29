using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;

namespace Farm.Web.Api.Services.FolderManagement
{
    /// <summary>
    /// Service for managing folder entities in the database.
    /// Provides shared folder creation and retrieval functionality used by multiple file management services.
    /// This prevents cross-service dependencies between GcodeFilesService and Model3dFileService.
    /// </summary>
    public interface IFolderManagementService
    {
        /// <summary>
        /// Get an existing folder or create it if it doesn't exist.
        /// </summary>
        /// <param name="directoryPath">Normalized directory path (e.g., "/" or "/subdir")</param>
        /// <param name="folderType">Type of folder: "gcode", "models", etc.</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>Existing or newly created Folder entity</returns>
        Task<Folder> GetOrCreateFolderAsync(string directoryPath, string folderType, CancellationToken ct);
    }
}
