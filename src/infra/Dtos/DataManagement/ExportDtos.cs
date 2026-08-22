using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Farm.Infrastructure;

namespace Farm.Infrastructure.Dtos.DataManagement;

/// <summary>
/// Catalog export data containing manufacturers, printer models, and component models
/// </summary>
public class CatalogExportDto
{
    public List<ManufacturerExportDto> Manufacturers { get; set; } = new();

    public List<FilamentTypeExportDto> FilamentTypes { get; set; } = new();

    public List<PrinterModelExportDto> PrinterModels { get; set; } = new();

    public List<HotendModelExportDto> Hotends { get; set; } = new();

    public List<ExtruderModelExportDto> Extruders { get; set; } = new();

    public List<ToolheadModelExportDto> Toolheads { get; set; } = new();

    public List<NozzleModelExportDto> Nozzles { get; set; } = new();

    public DateTime ExportedAt { get; set; } = DateTime.UtcNow;

    public string Version { get; set; } = "1.0";
}

/// <summary>
/// Full backup export containing catalog data, printers, locations, and settings
/// </summary>
public class FullBackupExportDto
{
    public CatalogExportDto Catalog { get; set; } = new();

    public List<PrinterExportDto> Printers { get; set; } = new();

    public List<LocationExportDto> Locations { get; set; } = new();

    public DateTime ExportedAt { get; set; } = DateTime.UtcNow;

    public string Version { get; set; } = "1.0";
}

/// <summary>
/// Manufacturer export data
/// </summary>
public class ManufacturerExportDto
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Url { get; set; }

    public string? Description { get; set; }

    public bool IsActive { get; set; } = true;
}

/// <summary>
/// Filament type export data
/// </summary>
public class FilamentTypeExportDto
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public int DefaultHotendTemp { get; set; }

    public int DefaultBedTemp { get; set; }

    public bool IsAbrasive { get; set; }

    public bool NeedsEnclosure { get; set; }

    public double? DefaultPricePerKg { get; set; }

    public double? DefaultDensity { get; set; }
}

/// <summary>
/// Printer model export data
/// </summary>
public class PrinterModelExportDto
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string ManufacturerName { get; set; } = string.Empty;

    public int? MotionType { get; set; }

    public double? MaxX { get; set; }

    public double? MaxY { get; set; }

    public double? MaxZ { get; set; }

    public int? DefaultBackend { get; set; }

    public bool HasHeatedBed { get; set; }

    public bool HasEnclosure { get; set; }

    public bool MultiMaterial { get; set; }

    public bool SupportsAutoLeveling { get; set; }

    public int? MaxBedTemp { get; set; }

    public int? MaxPrintSpeed { get; set; }

    public List<string> SupportedFilamentTypes { get; set; } = new();

    public bool IsActive { get; set; } = true;
}

/// <summary>
/// Hotend model export data
/// </summary>
public class HotendModelExportDto
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string ManufacturerName { get; set; } = string.Empty;

    public int MaxTemp { get; set; }

    public bool IsHighFlow { get; set; }

    public double? MaxFlowRate { get; set; }

    public string? Description { get; set; }

    public string? Url { get; set; }
}

/// <summary>
/// Extruder model export data
/// </summary>
public class ExtruderModelExportDto
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string ManufacturerName { get; set; } = string.Empty;

    public string? GearRatio { get; set; }

    public bool IsDirectDrive { get; set; }

    public string? Description { get; set; }
}

/// <summary>
/// Toolhead model export data
/// </summary>
public class ToolheadModelExportDto
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string ManufacturerName { get; set; } = string.Empty;

    public string? Description { get; set; }
}

/// <summary>
/// Nozzle model export data
/// </summary>
public class NozzleModelExportDto
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string ManufacturerName { get; set; } = string.Empty;

    public double Diameter { get; set; } = 0.4;

    public int? MaxTemp { get; set; }

    /// <summary>
    /// Nozzle material, as the <see cref="Farm.Infrastructure.Domain.NozzleMaterial"/> catalog
    /// name (e.g. "Brass", "Diamond", or any user-defined material name — an open string set,
    /// not a closed enum; see epic #1823 / issue #1826). Stored by name rather than an id/ordinal
    /// so renumbering or reordering the catalog cannot silently remap restored rows. An absent
    /// value (a backup pre-dating this field) restores as Brass; a present-but-unrecognized value
    /// rejects the row rather than guessing.
    /// </summary>
    public string? NozzleType { get; set; }

    /// <summary>
    /// Per-model hardness override, as the <c>NozzleHardnessOverride</c> enum name
    /// ("Auto", "Hardened", "NotHardened"). Round-tripping this matters for safety: an
    /// operator-pinned "NotHardened" that restored as "Auto" would silently re-admit the
    /// nozzle to abrasive-filament dispatch.
    /// </summary>
    public string? HardnessOverride { get; set; }

    /// <summary>
    /// Nozzle interface type, as the <c>NozzleInterfaceType</c> enum name (e.g. "V6", "Volcano").
    /// Given the same name-based export treatment as <see cref="NozzleType"/>/
    /// <see cref="HardnessOverride"/> so a future enum renumbering cannot silently remap
    /// restored rows (epic #1823 / issue #1826). Older backups wrote this as a raw ordinal
    /// number; <see cref="Farm.Infrastructure.Json.NozzleInterfaceExportJsonConverter"/>
    /// accepts either shape on read.
    /// </summary>
    [JsonConverter(typeof(Farm.Infrastructure.Json.NozzleInterfaceExportJsonConverter))]
    public string? NozzleInterface { get; set; }

    public string? Description { get; set; }
}

/// <summary>
/// Printer export data
/// </summary>
public class PrinterExportDto
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)]
    public string ServerUrl { get; set; } = string.Empty;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)]
    public string? OriginalServerUrl { get; set; }

    public int BackendPort { get; set; }

    public int? FrontendPort { get; set; }

    public string? ModelName { get; set; }

    public string? LocationName { get; set; }

    public int Backend { get; set; }

    public bool IsAvailable { get; set; }

    /// <summary>
    /// API key accepted during restore. Default exports never serialize this field.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)]
    public string? ApiKey { get; set; }

    /// <summary>
    /// Username accepted during restore. Default exports never serialize this field.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)]
    public string? Username { get; set; }

    /// <summary>
    /// Password accepted during restore. Default exports never serialize this field.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)]
    public string? Password { get; set; }
}

/// <summary>
/// Location export data
/// </summary>
public class LocationExportDto
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }
}
