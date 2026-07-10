using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Repositories.PartsInventory;

/// <summary>Repository for the printed-part SKU catalog.</summary>
public interface IPartInventoryRepository
{
    Task<List<PartInventory>> GetAllAsync(bool includeInactive, CancellationToken ct = default);

    Task<PartInventory?> GetByIdAsync(Guid id, CancellationToken ct = default);

    Task<PartInventory?> GetBySkuAsync(string sku, CancellationToken ct = default);

    Task<List<PartInventory>> GetReorderCandidatesAsync(CancellationToken ct = default);

    Task AddAsync(PartInventory entity, CancellationToken ct = default);

    Task<int> SaveChangesAsync(CancellationToken ct = default);
}

/// <summary>Repository for printed-part storage bins.</summary>
public interface IBinRepository
{
    Task<List<Bin>> GetAllAsync(bool includeInactive, CancellationToken ct = default);

    Task<Bin?> GetByIdAsync(Guid id, CancellationToken ct = default);

    Task<Bin?> GetByCodeAsync(string code, CancellationToken ct = default);

    Task AddAsync(Bin entity, CancellationToken ct = default);

    Task<int> SaveChangesAsync(CancellationToken ct = default);
}

/// <summary>Repository for the immutable printed-part adjustment ledger.</summary>
public interface IPartInventoryAdjustmentRepository
{
    Task<List<PartInventoryAdjustment>> GetForPartAsync(Guid partInventoryId, int limit, CancellationToken ct = default);

    Task<PartInventoryAdjustment?> GetByOperationKeyAsync(string operationKey, CancellationToken ct = default);

    Task<List<PartInventoryAdjustment>> GetByOperationKeyAllAsync(string operationKey, CancellationToken ct = default);
}

/// <summary>Repository for job-output → SKU mappings.</summary>
public interface IPartOutputMappingRepository
{
    Task<List<PartOutputMapping>> GetForPartAsync(Guid partInventoryId, CancellationToken ct = default);

    Task<List<PartOutputMapping>> GetForGcodeFileAsync(Guid gcodeFileId, CancellationToken ct = default);

    Task<List<PartOutputMapping>> GetForProjectFileAsync(Guid projectFileId, CancellationToken ct = default);

    Task<PartOutputMapping?> GetByIdAsync(Guid id, CancellationToken ct = default);

    Task AddAsync(PartOutputMapping entity, CancellationToken ct = default);

    Task RemoveAsync(PartOutputMapping entity, CancellationToken ct = default);

    Task<int> SaveChangesAsync(CancellationToken ct = default);
}
