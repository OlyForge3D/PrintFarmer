using Farm.Infrastructure;
using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Logging;
using Farm.Infrastructure.Services.Catalog;
using Farm.Slicer.Module.Data;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Dtos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Farm.Slicer.Module.Api.Services;

/// <summary>
/// Repairs persisted profile-family metadata created before catalog manufacturer attribution.
/// </summary>
public sealed class ProfileManufacturerMaintenanceService(
    SlicerDbContext dbContext,
    ICatalogService catalogService,
    ILogger<ProfileManufacturerMaintenanceService> logger)
{
    private readonly SlicerDbContext _dbContext =
        dbContext ?? throw new ArgumentNullException(nameof(dbContext));

    private readonly ICatalogService _catalogService =
        catalogService ?? throw new ArgumentNullException(nameof(catalogService));

    private readonly ILogger<ProfileManufacturerMaintenanceService> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

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
            _logger.LogInformation(
                "Profile manufacturer backfill completed: {FamiliesExamined} families examined, {FamiliesUpdated} families updated, {VariantsUpdated} variants updated, {Skipped} skipped",
                0,
                0,
                0,
                0);
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

        Dictionary<Guid, MachineProfile> variantsById = [];
        foreach (Guid[] familyIdChunk in families.Select(family => family.Id).Chunk(500))
        {
            List<MachineProfile> familyVariants = await _dbContext.MachineProfiles
                .Where(variant =>
                    variant.Manufacturer == "Custom"
                    && variant.MachineModelProfileId != null
                    && familyIdChunk.Contains(variant.MachineModelProfileId.Value))
                .ToListAsync(ct);
            foreach (MachineProfile variant in familyVariants)
            {
                variantsById[variant.Id] = variant;
            }
        }

        foreach (Guid[] modelIdChunk in families
                     .Select(family => family.PrinterModelId!.Value)
                     .Distinct()
                     .Chunk(500))
        {
            List<MachineProfile> modelVariants = await _dbContext.MachineProfiles
                .Where(variant =>
                    variant.Manufacturer == "Custom"
                    && variant.PrinterModelId != null
                    && modelIdChunk.Contains(variant.PrinterModelId.Value))
                .ToListAsync(ct);
            foreach (MachineProfile variant in modelVariants)
            {
                variantsById[variant.Id] = variant;
            }
        }

        List<MachineProfile> variants = variantsById.Values.ToList();

        int familiesUpdated = 0;
        int variantsUpdated = 0;
        int skipped = 0;
        HashSet<Guid> updatedVariantIds = [];
        DateTime updatedAt = DateTime.UtcNow;
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
            family.UpdatedAt = updatedAt;
            familiesUpdated++;
            int familyVariantsUpdated = 0;
            foreach (MachineProfile variant in variants.Where(variant =>
                         variant.MachineModelProfileId == family.Id
                         || variant.PrinterModelId == modelId)
                     .Where(variant => updatedVariantIds.Add(variant.Id)))
            {
                variant.Manufacturer = manufacturerName;
                variant.UpdatedAt = updatedAt;
                variantsUpdated++;
                familyVariantsUpdated++;
            }

            _logger.LogInformation(
                "Profile manufacturer backfill resolved {Manufacturer}: {FamiliesUpdated} family and {VariantsUpdated} variants updated",
                LogSanitizer.Sanitize(manufacturerName),
                1,
                familyVariantsUpdated);
        }

        _ = await _dbContext.SaveChangesAsync(ct);
        _logger.LogInformation(
            "Profile manufacturer backfill completed: {FamiliesExamined} families examined, {FamiliesUpdated} families updated, {VariantsUpdated} variants updated, {Skipped} skipped",
            families.Count,
            familiesUpdated,
            variantsUpdated,
            skipped);
        return new ProfileManufacturerBackfillResultDto(
            familiesUpdated,
            variantsUpdated,
            skipped);
    }
}
