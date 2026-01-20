using Farm.Infrastructure;
using System.ComponentModel.DataAnnotations;

namespace Farm.Web.Api.Models.Admin;

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
    public int NumberOfExtruders { get; set; }
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
    public int NozzleInterface { get; set; }
    public string? Description { get; set; }
}

/// <summary>
/// Printer export data
/// </summary>
public class PrinterExportDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string ServerUrl { get; set; } = string.Empty;
    public string? OriginalServerUrl { get; set; }
    public int BackendPort { get; set; }
    public int? FrontendPort { get; set; }
    public string? ModelName { get; set; }
    public string? LocationName { get; set; }
    public int Backend { get; set; }
    public bool IsAvailable { get; set; }
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
