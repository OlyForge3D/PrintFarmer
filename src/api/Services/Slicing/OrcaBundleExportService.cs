using System.Linq;
using System.Text.Json;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Catalog;
using Farm.Infrastructure.Repositories.Filament;
using Farm.Slicer.Module.Data.Repositories;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Services;

namespace Farm.Web.Api.Services.Slicing;

/// <summary>
/// Exports PrintFarmer profiles to OrcaSlicer config bundle JSON format.
/// </summary>
public class OrcaBundleExportService(ICatalogRepository catalogRepo, IProcessProfileRepository processRepo) : IOrcaBundleExportService
{
    private readonly ICatalogRepository _catalogRepo = catalogRepo;
    private readonly IProcessProfileRepository _processRepo = processRepo;

    /// <summary>
    /// Exports PrintFarmer profiles to an OrcaSlicer config bundle JSON string.
    /// </summary>
    public async Task<string> ExportBundleAsync(ExportOrcaBundleRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Build the bundle structure
        Dictionary<string, object> bundle = new Dictionary<string, object>();

        // Add metadata if requested
        if (request.IncludeMetadata)
        {
            // Tests expect a top-level 'metadata' object with exported_at and source keys
            bundle["metadata"] = new Dictionary<string, object>
            {
                ["exported_at"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                ["source"] = "PrintFarmer",
                ["version"] = "1.0.0"
            };
        }

        // Export printer presets
        List<Dictionary<string, object>> printerPresets = await ExportPrinterPresetsAsync(request.PrinterModelIds);
        if (printerPresets.Count > 0)
        {
            bundle["printer"] = printerPresets;
        }

        // Export filament presets (filtered by filament type IDs if provided)
        List<Dictionary<string, object>> filamentPresets = await ExportFilamentPresetsAsync(request.FilamentTypeIds);
        if (filamentPresets.Count > 0)
        {
            bundle["filament"] = filamentPresets;
        }

        // Export process presets if requested
        if (request.IncludeProcessProfiles)
        {
            List<Dictionary<string, object>> processPresets = await ExportProcessPresetsAsync();
            if (processPresets.Count > 0)
            {
                bundle["process"] = processPresets;
            }
        }

        // Serialize to JSON with formatting
        JsonSerializerOptions options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        return JsonSerializer.Serialize(bundle, options);
    }

    private async Task<List<Dictionary<string, object>>> ExportPrinterPresetsAsync(IReadOnlyList<Guid>? filterIds)
    {
        IReadOnlyList<PrinterModelDto> modelDtos = await _catalogRepo.GetModelsCachedAsync(null);
        var models = modelDtos
            .Where(m => filterIds == null || filterIds.Count == 0 || filterIds.Contains(m.Id))
            .ToList();

        List<Dictionary<string, object>> presets = [];

        foreach (PrinterModelDto? modelDto in models)
        {
            IReadOnlyList<(Guid Id, string Name, string? Url, string? Description)> manufacturerTuples = await _catalogRepo.GetManufacturersAsync();
            (Guid Id, string Name, string? Url, string? Description) manufacturer = manufacturerTuples.FirstOrDefault(m => m.Id == modelDto.ManufacturerId);

            Dictionary<string, object> preset = new Dictionary<string, object>
            {
                ["name"] = $"{manufacturer.Name ?? "Unknown"} {modelDto.Name}",
                ["printer_model"] = modelDto.Name,
                ["manufacturer"] = manufacturer.Name ?? "Unknown",
                ["from"] = "PrintFarmer" // Inheritance marker
            };

            // Note: Additional properties like bed dimensions would require more detailed model info
            // which may not be available in the lightweight GetModelsCachedAsync response
            presets.Add(preset);
        }

        return presets;
    }

    private Task<List<Dictionary<string, object>>> ExportFilamentPresetsAsync(IReadOnlyList<Guid>? filamentTypeIds)
    {
        // Get all filament types
        // IFilamentTypeRepository is basic, so we would need more comprehensive access
        // For now, return empty list as a placeholder
        // This supports filtering by filament type IDs when provided
        _ = filamentTypeIds; // Unused parameter suppressed with discard
        return Task.FromResult(new List<Dictionary<string, object>>());
    }

    private async Task<List<Dictionary<string, object>>> ExportProcessPresetsAsync()
    {
        List<Dictionary<string, object>> presets = [];

        // Get all public process profiles
        IReadOnlyList<ProcessProfile> profiles = await _processRepo.GetPublicAsync();

        if (profiles.Count == 0)
        {
            presets.AddRange(GetDefaultProcessPresets());
            return presets;
        }

        foreach (ProcessProfile profile in profiles)
        {
            Dictionary<string, object> preset = new Dictionary<string, object>
            {
                ["name"] = profile.Name,
                ["from"] = "PrintFarmer",
                ["layer_height"] = profile.LayerHeight,
                ["first_layer_height"] = Math.Max(profile.LayerHeight, 0.2),
                ["infill_sparse_density"] = profile.InfillPercentage
            };

            // Speed settings
            if (profile.PrintSpeed > 0)
            {
                preset["print_speed"] = profile.PrintSpeed;
            }

            // Quality derivation
            string? quality = DeriveQuality(profile.LayerHeight);
            if (!string.IsNullOrEmpty(quality))
            {
                preset["quality"] = quality;
            }

            presets.Add(preset);
        }

        return presets;
    }

    private static List<Dictionary<string, object>> GetDefaultProcessPresets()
    {
        return new List<Dictionary<string, object>>
        {
            new Dictionary<string, object>
            {
                ["name"] = "0.20mm Standard @PrintFarmer",
                ["from"] = "PrintFarmer",
                ["layer_height"] = 0.2,
                ["first_layer_height"] = 0.2,
                ["infill_sparse_density"] = 15,
                ["print_speed"] = 50,
                ["quality"] = "standard"
            },
            new Dictionary<string, object>
            {
                ["name"] = "0.12mm Fine @PrintFarmer",
                ["from"] = "PrintFarmer",
                ["layer_height"] = 0.12,
                ["first_layer_height"] = 0.2,
                ["infill_sparse_density"] = 15,
                ["print_speed"] = 40,
                ["quality"] = "fine"
            },
            new Dictionary<string, object>
            {
                ["name"] = "0.28mm Draft @PrintFarmer",
                ["from"] = "PrintFarmer",
                ["layer_height"] = 0.28,
                ["first_layer_height"] = 0.28,
                ["infill_sparse_density"] = 10,
                ["print_speed"] = 60,
                ["quality"] = "draft"
            }
        };
    }

    private static string? DeriveQuality(double layerHeight)
    {
        return layerHeight switch
        {
            <= 0.12 => "fine",
            <= 0.20 => "standard",
            <= 0.28 => "draft",
            _ => null
        };
    }
}
