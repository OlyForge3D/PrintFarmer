using Farm.Infrastructure.Repositories.Catalog;

namespace Farm.Web.Api.Services;

/// <summary>
/// Service for getting default manufacturer and model IDs to avoid nullable foreign keys
/// </summary>
public interface IDefaultCatalogService
{
    Task<Guid> GetUnknownManufacturerIdAsync();
    Task<Guid> GetUnknownModelIdAsync();
    Task<(Guid ManufacturerId, Guid ModelId)> GetDefaultCatalogIdsAsync();
}

public class DefaultCatalogService(ICatalogRepository catalogRepository) : IDefaultCatalogService
{
    private readonly ICatalogRepository _catalogRepository = catalogRepository;
    private Guid? _cachedUnknownManufacturerId;
    private Guid? _cachedUnknownModelId;

    public async Task<Guid> GetUnknownManufacturerIdAsync()
    {
        if (_cachedUnknownManufacturerId.HasValue)
        {
            return _cachedUnknownManufacturerId.Value;
        }

        Guid? unknownId = await _catalogRepository.GetUnknownManufacturerIdAsync();
        if (!unknownId.HasValue)
        {
            throw new InvalidOperationException("Unknown manufacturer not found. Ensure database seeding has been completed.");
        }

        _cachedUnknownManufacturerId = unknownId.Value;
        return unknownId.Value;
    }

    public async Task<Guid> GetUnknownModelIdAsync()
    {
        if (_cachedUnknownModelId.HasValue)
        {
            return _cachedUnknownModelId.Value;
        }

        Guid? unknownModelId = await _catalogRepository.GetUnknownModelIdAsync();
        if (!unknownModelId.HasValue)
        {
            throw new InvalidOperationException("Unknown Model not found. Ensure database seeding has been completed.");
        }

        _cachedUnknownModelId = unknownModelId.Value;
        return unknownModelId.Value;
    }

    public async Task<(Guid ManufacturerId, Guid ModelId)> GetDefaultCatalogIdsAsync()
    {
        Guid manufacturerId = await GetUnknownManufacturerIdAsync();
        Guid modelId = await GetUnknownModelIdAsync();
        return (manufacturerId, modelId);
    }
}
