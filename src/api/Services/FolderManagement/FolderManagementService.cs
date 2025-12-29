using System;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Folder;

namespace Farm.Web.Api.Services.FolderManagement
{
    /// <summary>
    /// Implementation of folder management service.
    /// Provides folder creation and retrieval with proper repository pattern.
    /// </summary>
    public class FolderManagementService : IFolderManagementService
    {
        private readonly IFolderRepository _folderRepository;

        public FolderManagementService(IFolderRepository folderRepository)
        {
            _folderRepository = folderRepository ?? throw new ArgumentNullException(nameof(folderRepository));
        }

        /// <summary>
        /// Get an existing folder or create it if it doesn't exist.
        /// </summary>
        public async Task<Folder> GetOrCreateFolderAsync(string directoryPath, string folderType, CancellationToken ct)
        {
            return await _folderRepository.GetOrCreateFolderAsync(directoryPath, folderType, ct);
        }
    }
}
