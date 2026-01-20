using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Repositories.Harvest;

public interface IHarvestRepository
{
    // GcodeHarvestOperation operations
    Task<GcodeHarvestOperation?> GetOperationByIdAsync(Guid operationId, CancellationToken ct = default);

    Task<GcodeHarvestOperation?> GetOperationByIdTrackedAsync(Guid operationId, CancellationToken ct = default);

    Task<GcodeHarvestOperation?> GetOperationWithPrinterAsync(Guid operationId, CancellationToken ct = default);

    Task<GcodeHarvestOperation?> GetActiveOperationForPrinterAsync(Guid printerId, CancellationToken ct = default);

    Task<List<GcodeHarvestOperation>> GetOperationsAsync(Guid? printerId, GcodeHarvestStatus? status, int limit, int offset, CancellationToken ct = default);

    Task<List<GcodeHarvestOperation>> GetRecentOperationsForPrinterAsync(Guid printerId, int count, CancellationToken ct = default);

    Task<List<GcodeHarvestOperation>> GetActiveOperationsAsync(CancellationToken ct = default);

    Task<List<GcodeHarvestOperation>> GetRunningOperationsWithFilesFoundAsync(CancellationToken ct = default);

    Task AddOperationAsync(GcodeHarvestOperation operation, CancellationToken ct = default);

    Task UpdateOperationAsync(GcodeHarvestOperation operation, CancellationToken ct = default);

    // HarvestDiscoveredFile operations
    Task<HarvestDiscoveredFile?> GetDiscoveredFileByIdAsync(Guid fileId, Guid operationId, CancellationToken ct = default);

    Task<List<HarvestDiscoveredFile>> GetDiscoveredFilesAsync(Guid operationId, CancellationToken ct = default);

    Task<HarvestDiscoveredFile[]> GetDiscoveredFilesByIdsAsync(List<Guid> fileIds, CancellationToken ct = default);

    Task<int> GetDiscoveredFilesCountAsync(Guid operationId, CancellationToken ct = default);

    Task<int> GetDiscoveredFilesCountWithSearchAsync(Guid operationId, string search, CancellationToken ct = default);

    Task<List<HarvestDiscoveredFile>> GetDiscoveredFilesPagedAsync(Guid operationId, int page, int pageSize, string? search, CancellationToken ct = default);

    Task<bool> DiscoveredFileExistsByNameAsync(Guid operationId, string fileName, CancellationToken ct = default);

    Task<HarvestDiscoveredFile?> GetDiscoveredFileByOperationAndFileNameAsync(Guid operationId, string fileName, CancellationToken ct = default);

    Task AddDiscoveredFileAsync(HarvestDiscoveredFile file, CancellationToken ct = default);

    Task UpdateDiscoveredFileAsync(HarvestDiscoveredFile file, CancellationToken ct = default);

    Task DeleteDiscoveredFileAsync(HarvestDiscoveredFile file, CancellationToken ct = default);

    Task DeleteDiscoveredFilesByOperationAsync(Guid operationId, CancellationToken ct = default);

    // Harvest file mapping operations
    Task CreateFileImportMappingAsync(HarvestDiscoveredFile discoveredFile, GcodeFile gcodeFile, CancellationToken ct = default);

    // Combined operations
    Task SaveChangesAsync(CancellationToken ct = default);
}
