using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Catalog;
using Farm.Slicer.Module.Services;

namespace Farm.Web.Api.Services.Adapters;

/// <summary>
/// Adapter bridging the module's <see cref="ICatalogServiceAdapter"/> to
/// the API project's <see cref="ICatalogService"/> for catalog queries.
/// </summary>
public sealed class ModuleCatalogServiceAdapter(ICatalogService catalogService) : ICatalogServiceAdapter
{
    private readonly ICatalogService _catalogService = catalogService ?? throw new ArgumentNullException(nameof(catalogService));

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GetManufacturerNamesAsync(CancellationToken ct = default)
    {
        (IReadOnlyList<ManufacturerDto> list, _) = await _catalogService.GetManufacturersAsync(ct);
        return list.Select(m => m.Name).ToList();
    }

    /// <inheritdoc />
    public async Task<string?> GetPrinterModelNameAsync(Guid printerModelId, CancellationToken ct = default)
    {
        PrinterModelDto? model = await _catalogService.GetModelByIdAsync(printerModelId, ct);
        return model?.Name;
    }

    /// <inheritdoc />
    public async Task<CatalogModelInfo?> GetModelByIdAsync(Guid modelId, CancellationToken ct = default)
    {
        PrinterModelDto? model = await _catalogService.GetModelByIdAsync(modelId, ct);
        if (model is null)
        {
            return null;
        }

        // Resolve manufacturer name via manufacturers list
        string? manufacturerName = null;
        (IReadOnlyList<ManufacturerDto> manufacturers, _) = await _catalogService.GetManufacturersAsync(ct);
        ManufacturerDto? manufacturer = manufacturers.FirstOrDefault(m => m.Id == model.ManufacturerId);
        if (manufacturer is not null)
        {
            manufacturerName = manufacturer.Name;
        }

        return new CatalogModelInfo(model.Id, model.Name, manufacturerName);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SlicerModelAliasDto>> GetModelAliasesAsync(Guid modelId, CancellationToken ct = default)
    {
        IEnumerable<SlicerModelAliasDto> aliases =
            await _catalogService.GetModelAliasesAsync(modelId, ct);
        return aliases.ToList();
    }
}
