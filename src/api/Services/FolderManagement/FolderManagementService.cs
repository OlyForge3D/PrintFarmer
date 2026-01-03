using System;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.UnitOfWork;

namespace Farm.Web.Api.Services.FolderManagement
{
    /// <summary>
    /// Implementation of folder management service.
    /// Provides folder creation and retrieval with proper repository pattern.
    /// </summary>
    public class FolderManagementService : IFolderManagementService
    {
        private readonly IUnitOfWork _unitOfWork;

        public FolderManagementService(IUnitOfWork unitOfWork)
        {
            _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
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
