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
    Task<List<PartOutputMapping>> GetAllAsync(CancellationToken ct = default);

    Task<List<PartOutputMapping>> GetForPartAsync(Guid partInventoryId, CancellationToken ct = default);

    Task<List<PartOutputMapping>> GetForGcodeFileAsync(Guid gcodeFileId, CancellationToken ct = default);

    Task<List<PartOutputMapping>> GetForProjectFileAsync(Guid projectFileId, CancellationToken ct = default);

    Task<PartOutputMapping?> GetByIdAsync(Guid id, CancellationToken ct = default);

    Task<bool> SourceExistsAsync(Guid? gcodeFileId, Guid? projectFileId, CancellationToken ct = default);

    Task<bool> MappingExistsAsync(Guid partInventoryId, Guid? gcodeFileId, Guid? projectFileId, CancellationToken ct = default);

    Task AddAsync(PartOutputMapping entity, CancellationToken ct = default);

    Task RemoveAsync(PartOutputMapping entity, CancellationToken ct = default);

    /// <summary>
    /// Deletes every PartOutputMapping whose direct source is the given <paramref name="gcodeFileId"/>
    /// (i.e., <c>GcodeFileId</c> matches, not <c>PrintProjectFileId</c>). Used by
    /// <c>GcodeFilesService.DeleteFilesAsync</c> to clear the direct FK before the GcodeFile
    /// is removed, since <c>FK_PartOutputMappings_GcodeFiles_GcodeFileId</c> is
    /// <c>OnDelete(Restrict)</c> after the Dallas cascade adjudication for #953 (broke the
    /// SQL Server 1785 multi-cascading-path graph GcodeFiles ⇒ PartOutputMappings via
    /// {direct, via PrintProjectFiles}). Mappings that reach the GcodeFile indirectly via
    /// <c>PrintProjectFileId</c> are NOT touched — they cascade normally when the
    /// PrintProjectFile is deleted.
    /// </summary>
    Task DeleteDirectMappingsForGcodeFileAsync(Guid gcodeFileId, CancellationToken ct = default);

    Task<int> SaveChangesAsync(CancellationToken ct = default);
}
