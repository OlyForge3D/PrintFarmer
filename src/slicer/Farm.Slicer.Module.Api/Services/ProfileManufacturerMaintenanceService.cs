using Farm.Infrastructure;
using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Services.Catalog;
using Farm.Slicer.Module.Data;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Dtos;
using Microsoft.EntityFrameworkCore;

namespace Farm.Slicer.Module.Api.Services;

/// <summary>
/// Repairs persisted profile-family metadata created before catalog manufacturer attribution.
/// </summary>
public sealed class ProfileManufacturerMaintenanceService(
    SlicerDbContext dbContext,
    ICatalogService catalogService)
{
    private readonly SlicerDbContext _dbContext =
        dbContext ?? throw new ArgumentNullException(nameof(dbContext));

    private readonly ICatalogService _catalogService =
        catalogService ?? throw new ArgumentNullException(nameof(catalogService));

    /// <summary>
    /// Backfills exactly <c>Custom</c> family and variant manufacturers from the printer catalog.
    /// </summary>
    public async Task<ProfileManufacturerBackfillResultDto> BackfillAsync(
        CancellationToken ct = default)
    {
        List<MachineModelProfile> families = await _dbContext.MachineModelProfiles
            .Where(family =>
                family.Manufacturer == "Custom"
                && family.PrinterModelId != null)
            .ToListAsync(ct);
        if (families.Count == 0)
        {
            return new ProfileManufacturerBackfillResultDto(0, 0, 0);
        }

        (IReadOnlyList<PrinterModelDto> models, _) =
            await _catalogService.GetModelsAsync(null, ct);
        (IReadOnlyList<ManufacturerDto> manufacturers, _) =
            await _catalogService.GetManufacturersAsync(ct);
        Dictionary<Guid, Guid> manufacturerIdByModelId =
            models.ToDictionary(model => model.Id, model => model.ManufacturerId);
        Dictionary<Guid, string> manufacturerNameById =
            manufacturers.ToDictionary(manufacturer => manufacturer.Id, manufacturer => manufacturer.Name);

        HashSet<Guid> familyIds = families.Select(family => family.Id).ToHashSet();
        HashSet<Guid> modelIds = families
            .Select(family => family.PrinterModelId!.Value)
            .ToHashSet();
        List<MachineProfile> variants = await _dbContext.MachineProfiles
            .Where(variant =>
                variant.Manufacturer == "Custom"
                && ((variant.MachineModelProfileId != null
                        && familyIds.Contains(variant.MachineModelProfileId.Value))
                    || (variant.PrinterModelId != null
                        && modelIds.Contains(variant.PrinterModelId.Value))))
            .ToListAsync(ct);

        int familiesUpdated = 0;
        int variantsUpdated = 0;
        int skipped = 0;
        HashSet<Guid> updatedVariantIds = [];
        foreach (MachineModelProfile family in families)
        {
            Guid modelId = family.PrinterModelId!.Value;
            if (!manufacturerIdByModelId.TryGetValue(modelId, out Guid manufacturerId)
                || !manufacturerNameById.TryGetValue(manufacturerId, out string? manufacturerName)
                || string.IsNullOrWhiteSpace(manufacturerName))
            {
                skipped++;
                continue;
            }

            family.Manufacturer = manufacturerName;
            familiesUpdated++;
            foreach (MachineProfile variant in variants.Where(variant =>
                         variant.MachineModelProfileId == family.Id
                         || variant.PrinterModelId == modelId)
                     .Where(variant => updatedVariantIds.Add(variant.Id)))
            {
                variant.Manufacturer = manufacturerName;
                variantsUpdated++;
            }
        }

        _ = await _dbContext.SaveChangesAsync(ct);
        return new ProfileManufacturerBackfillResultDto(
            familiesUpdated,
            variantsUpdated,
            skipped);
    }
}
