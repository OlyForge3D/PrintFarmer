namespace Farm.Web.Api.Services.Slicing;

using System.Linq;
using System.Text.Json;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Web.Shared;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Exports PrintFarmer profiles to OrcaSlicer config bundle JSON format.
/// </summary>
public class OrcaBundleExportService(AppDbContext db) : IOrcaBundleExportService
{
    private readonly AppDbContext _db = db;

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

        // Export filament presets
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
        IQueryable<PrinterModel> query = _db.Models
            .Include(m => m.Manufacturer)
            .AsNoTracking();

        if (filterIds != null && filterIds.Count > 0)
        {
            query = query.Where(m => filterIds.Contains(m.Id));
        }

        List<PrinterModel> models = await query.ToListAsync();
        List<Dictionary<string, object>> presets = new List<Dictionary<string, object>>();

        foreach (PrinterModel? model in models)
        {
            Dictionary<string, object> preset = new Dictionary<string, object>
            {
                ["name"] = $"{model.Manufacturer?.Name ?? "Unknown"} {model.Name}",
                ["printer_model"] = model.Name,
                ["manufacturer"] = model.Manufacturer?.Name ?? "Unknown",
                ["from"] = "PrintFarmer" // Inheritance marker
            };

            // Bed dimensions
            if (model.MaxX.HasValue)
            {
                preset["bed_width"] = model.MaxX.Value;
            }

            if (model.MaxY.HasValue)
            {
                preset["bed_depth"] = model.MaxY.Value;
            }

            if (model.MaxZ.HasValue)
            {
                preset["max_z_height"] = model.MaxZ.Value;
            }

            // Build volume
            if (model.MaxX.HasValue && model.MaxY.HasValue && model.MaxZ.HasValue)
            {
                preset["printable_area"] = new object[]
                {
                    new object[] { 0, 0 },
                    new object[] { model.MaxX.Value, 0 },
                    new object[] { model.MaxX.Value, model.MaxY.Value },
                    new object[] { 0, model.MaxY.Value }
                };
            }

            // Nozzle configuration
            if (model.DefaultNozzleDiameter.HasValue)
            {
                preset["nozzle_diameter"] = new[] { model.DefaultNozzleDiameter.Value };
            }

            // Temperature capabilities
            if (model.MaxHotendTemp.HasValue)
            {
                preset["max_hotend_temp"] = model.MaxHotendTemp.Value;
            }

            if (model.MaxBedTemp.HasValue)
            {
                preset["max_bed_temp"] = model.MaxBedTemp.Value;
            }

            // Capabilities
            preset["printer_technology"] = "FFF"; // Assume FFF for now

            if (model.HasHeatedBed)
            {
                preset["bed_temperature"] = 60; // Default heated bed temp
            }

            if (model.NumberOfExtruders > 1)
            {
                preset["extruder_count"] = model.NumberOfExtruders;
                preset["multi_material"] = model.MultiMaterial;
            }

            // Motion system
            if (model.MotionType.HasValue)
            {
                preset["motion_type"] = model.MotionType.Value switch
                {
                    0 => "Cartesian",
                    1 => "CoreXY",
                    2 => "Delta",
                    _ => "Unknown"
                };
            }

            // Speed capabilities
            if (model.MaxPrintSpeed.HasValue)
            {
                preset["max_print_speed"] = model.MaxPrintSpeed.Value;
            }

            // Features
            if (model.SupportsAutoLeveling)
            {
                preset["auto_leveling"] = true;
            }

            if (model.HasEnclosure)
            {
                preset["has_enclosure"] = true;
            }

            presets.Add(preset);
        }

        return presets;
    }

    private async Task<List<Dictionary<string, object>>> ExportFilamentPresetsAsync(IReadOnlyList<Guid>? filterIds)
    {
        IQueryable<FilamentType> query = _db.FilamentTypes.AsNoTracking();

        if (filterIds != null && filterIds.Count > 0)
        {
            query = query.Where(f => filterIds.Contains(f.Id));
        }

        List<FilamentType> filaments = await query.ToListAsync();
        List<Dictionary<string, object>> presets = new List<Dictionary<string, object>>();

        foreach (FilamentType? filament in filaments)
        {
            Dictionary<string, object> preset = new Dictionary<string, object>
            {
                ["name"] = filament.Name,
                ["filament_type"] = DeriveFilamentType(filament.Name),
                ["from"] = "PrintFarmer"
            };

            // Temperature settings
            if (filament.DefaultHotendTemp.HasValue)
            {
                preset["nozzle_temperature"] = (int)filament.DefaultHotendTemp.Value;
                preset["nozzle_temperature_range_low"] = (int)(filament.DefaultHotendTemp.Value - 10);
                preset["nozzle_temperature_range_high"] = (int)(filament.DefaultHotendTemp.Value + 10);
            }

            if (filament.DefaultBedTemp.HasValue)
            {
                preset["bed_temperature"] = (int)filament.DefaultBedTemp.Value;
                preset["bed_temperature_range_low"] = (int)(filament.DefaultBedTemp.Value - 10);
                preset["bed_temperature_range_high"] = (int)(filament.DefaultBedTemp.Value + 10);
            }

            // Material properties based on type
            string materialType = DeriveFilamentType(filament.Name);
            AddMaterialProperties(preset, materialType);

            presets.Add(preset);
        }

        return presets;
    }

    private async Task<List<Dictionary<string, object>>> ExportProcessPresetsAsync()
    {
        // Export ProcessProfile entities as process presets
        List<ProcessProfile> profiles = await _db.ProcessProfiles
            .Include(p => p.PrinterModel)
            .ThenInclude(m => m!.Manufacturer)
            .AsNoTracking()
            .ToListAsync();

        List<Dictionary<string, object>> presets = new List<Dictionary<string, object>>();

        // Add default process presets if no custom profiles exist
        if (profiles.Count == 0)
        {
            presets.AddRange(GetDefaultProcessPresets());
            return presets;
        }

        foreach (ProcessProfile? profile in profiles)
        {
            Dictionary<string, object> preset = new Dictionary<string, object>
            {
                ["name"] = profile.Name,
                ["from"] = "PrintFarmer",
                ["layer_height"] = profile.LayerHeight,
                ["first_layer_height"] = Math.Max(profile.LayerHeight, 0.2), // Usually thicker
                ["infill_sparse_density"] = profile.InfillPercentage
            };

            // Speed settings
            if (profile.PrintSpeed > 0)
            {
                preset["print_speed"] = profile.PrintSpeed;
                preset["outer_wall_speed"] = profile.PrintSpeed * 0.8; // Typically slower
                preset["inner_wall_speed"] = profile.PrintSpeed;
                preset["infill_speed"] = profile.PrintSpeed * 1.2; // Typically faster
            }

            // Temperature settings


            // Wall/layer settings
            preset["wall_loops"] = 3; // Default perimeters
            preset["top_shell_layers"] = 4;
            preset["bottom_shell_layers"] = 4;

            // Support settings
            if (profile.RawJson != null && profile.RawJson.Contains("support"))
            {
                preset["enable_support"] = true;
                preset["support_type"] = "normal";
                preset["support_angle"] = 45;
            }

            // Quality derivation
            string? quality = DeriveQuality(profile.LayerHeight);
            if (!string.IsNullOrEmpty(quality))
            {
                preset["quality"] = quality;
            }

            // Add printer model association if available
            if (profile.PrinterModel != null)
            {
                preset["compatible_printers"] = new[]
                {
                    $"{profile.PrinterModel.Manufacturer?.Name ?? "Unknown"} {profile.PrinterModel.Name}"
                };
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
                ["outer_wall_speed"] = 40,
                ["inner_wall_speed"] = 50,
                ["infill_speed"] = 60,
                ["wall_loops"] = 3,
                ["top_shell_layers"] = 4,
                ["bottom_shell_layers"] = 4,
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
                ["outer_wall_speed"] = 30,
                ["inner_wall_speed"] = 40,
                ["infill_speed"] = 50,
                ["wall_loops"] = 3,
                ["top_shell_layers"] = 5,
                ["bottom_shell_layers"] = 5,
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
                ["outer_wall_speed"] = 50,
                ["inner_wall_speed"] = 60,
                ["infill_speed"] = 80,
                ["wall_loops"] = 2,
                ["top_shell_layers"] = 3,
                ["bottom_shell_layers"] = 3,
                ["quality"] = "draft"
            }
        };
    }

    private static string DeriveFilamentType(string name)
    {
        string nameLower = name.ToLowerInvariant();

        if (nameLower.Contains("pla"))
        {
            return "PLA";
        }

        if (nameLower.Contains("petg"))
        {
            return "PETG";
        }

        if (nameLower.Contains("abs"))
        {
            return "ABS";
        }

        if (nameLower.Contains("asa"))
        {
            return "ASA";
        }

        if (nameLower.Contains("tpu"))
        {
            return "TPU";
        }

        if (nameLower.Contains("nylon"))
        {
            return "Nylon";
        }

        if (nameLower.Contains("pc"))
        {
            return "PC";
        }

        if (nameLower.Contains("pva"))
        {
            return "PVA";
        }

        if (nameLower.Contains("hips"))
        {
            return "HIPS";
        }

        return "PLA"; // Default fallback
    }

    private static void AddMaterialProperties(Dictionary<string, object> preset, string materialType)
    {
        // Add material-specific properties based on common characteristics
        switch (materialType)
        {
            case "PLA":
                preset["fan_cooling"] = true;
                preset["max_fan_speed"] = 100;
                preset["min_fan_speed"] = 50;
                break;
            case "PETG":
                preset["fan_cooling"] = true;
                preset["max_fan_speed"] = 50;
                preset["min_fan_speed"] = 20;
                break;
            case "ABS":
            case "ASA":
                preset["fan_cooling"] = false;
                preset["max_fan_speed"] = 20;
                preset["min_fan_speed"] = 0;
                preset["chamber_temperature"] = 40;
                break;
            case "TPU":
                preset["fan_cooling"] = true;
                preset["max_fan_speed"] = 50;
                preset["min_fan_speed"] = 30;
                preset["retraction_length"] = 0.5; // Flexible filaments need minimal retraction
                break;
        }
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
