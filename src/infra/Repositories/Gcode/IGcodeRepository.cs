using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Repositories.Gcode
{
    public interface IGcodeRepository
    {
        Task<List<GcodeFile>> QueryLibraryAsync(string? search, string? material, double? nozzleDiameter, Guid? targetPrinterId, CancellationToken ct);
        Task<GcodeFile?> GetByIdWithIncludesAsync(Guid id, CancellationToken ct);
        Task<GcodeFile?> FindByHashAsync(string hash, CancellationToken ct);
        // New: lookup by full absolute file path stored in DB (for harvest correlation)
        Task<GcodeFile?> GetByFullPathAsync(string fullPath, CancellationToken ct);
        // New: get latest harvest operation Id for a given printer if any
        Task<Guid?> GetLatestHarvestOperationIdForPrinterAsync(Guid printerId, CancellationToken ct);
        Task AddAsync(GcodeFile file, CancellationToken ct);
        Task RemoveAsync(GcodeFile file, CancellationToken ct);
        Task SaveChangesAsync(CancellationToken ct);
    }
}
