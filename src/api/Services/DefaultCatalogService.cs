using Farm.Web.Api.Data;
using Farm.Web.Api.Domain;
using Microsoft.EntityFrameworkCore;

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

public class DefaultCatalogService : IDefaultCatalogService
{
    private readonly AppDbContext _context;
    private Guid? _cachedUnknownManufacturerId;
    private Guid? _cachedUnknownModelId;

    public DefaultCatalogService(AppDbContext context)
    {
        _context = context;
    }

    public async Task<Guid> GetUnknownManufacturerIdAsync()
    {
        if (_cachedUnknownManufacturerId.HasValue)
        {
            return _cachedUnknownManufacturerId.Value;
        }

        Manufacturer? unknown = await _context.Manufacturers.FirstOrDefaultAsync(m => m.Name == "Unknown");
        if (unknown == null)
        {
            throw new InvalidOperationException("Unknown manufacturer not found. Ensure database seeding has been completed.");
        }

        _cachedUnknownManufacturerId = unknown.Id;
        return unknown.Id;
    }

    public async Task<Guid> GetUnknownModelIdAsync()
    {
        if (_cachedUnknownModelId.HasValue)
        {
            return _cachedUnknownModelId.Value;
        }

        Guid unknownMfgId = await GetUnknownManufacturerIdAsync();
        PrinterModel? unknownModel = await _context.Models.FirstOrDefaultAsync(m =>
            m.ManufacturerId == unknownMfgId && m.Name == "Unknown Model");

        if (unknownModel == null)
        {
            throw new InvalidOperationException("Unknown Model not found. Ensure database seeding has been completed.");
        }

        _cachedUnknownModelId = unknownModel.Id;
        return unknownModel.Id;
    }

    public async Task<(Guid ManufacturerId, Guid ModelId)> GetDefaultCatalogIdsAsync()
    {
        Guid manufacturerId = await GetUnknownManufacturerIdAsync();
        Guid modelId = await GetUnknownModelIdAsync();
        return (manufacturerId, modelId);
    }
}
