using System.ComponentModel.DataAnnotations;

namespace Farm.Infrastructure.Dtos.DataManagement;

/// <summary>
/// DTO for manufacturer seed data from YAML
/// </summary>
public class ManufacturerSeedDto
{
    [Required]
    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// DTO for printer model seed data from YAML
/// </summary>
public class PrinterModelSeedDto
{
    [Required]
    public string Name { get; set; } = string.Empty;

    [Required]
    public string Manufacturer { get; set; } = string.Empty;

    public BuildVolumeDto? BuildVolume { get; set; }

    public string? DefaultBackend { get; set; }

    public string? MotionType { get; set; }

    public bool HasHeatedBed { get; set; } = true;

    public bool HasEnclosure { get; set; }

    public bool HasCarbonFilter { get; set; }

    public bool HasHepaFilter { get; set; }

    public bool HasBowdenTube { get; set; }

    public bool HasPtfeLiner { get; set; }

    public bool HasLinearRails { get; set; }

    public bool HasLeadScrews { get; set; }

    public bool HasToolchanger { get; set; }

    public bool HasFilamentCutter { get; set; }

    public bool HasHeatedChamber { get; set; }

    public bool SupportsAutoLeveling { get; set; }

    public bool MultiMaterial { get; set; }

    public int? MinHotendTemp { get; set; }

    public int? MaxHotendTemp { get; set; }

    public int? MinBedTemp { get; set; }

    public int? MaxBedTemp { get; set; }

    public int? MaxPrintSpeed { get; set; }

    /// <summary>
    /// Default power consumption in watts for this printer model.
    /// </summary>
    public decimal? DefaultWattage { get; set; }

    public List<string>? SupportedMaterials { get; set; }

    public List<ToolheadAssignmentDto>? Toolheads { get; set; }

    public List<SlicerAliasDto>? Aliases { get; set; }
}

/// <summary>
/// DTO for build volume dimensions
/// </summary>
public class BuildVolumeDto
{
    public double X { get; set; }

    public double Y { get; set; }

    public double Z { get; set; }
}

/// <summary>
/// DTO for toolhead component assignments
/// </summary>
public class ToolheadAssignmentDto
{
    [Required]
    public string Name { get; set; } = "Primary";

    public string? Toolhead { get; set; }

    public string? Hotend { get; set; }

    public string? Extruder { get; set; }

    public string? Nozzle { get; set; }

    public double? NozzleDiameter { get; set; }
}

/// <summary>
/// DTO for slicer model name aliases
/// </summary>
public class SlicerAliasDto
{
    [Required]
    public string SlicerType { get; set; } = string.Empty;

    [Required]
    public string SlicerModelName { get; set; } = string.Empty;
}

/// <summary>
/// DTO for filament type seed data from YAML
/// </summary>
public class FilamentTypeSeedDto
{
    [Required]
    public string Name { get; set; } = string.Empty;

    public int DefaultHotendTemp { get; set; }

    public int DefaultBedTemp { get; set; }

    public bool IsAbrasive { get; set; }

    public bool NeedsEnclosure { get; set; }

    public double? DefaultPricePerKg { get; set; }

    public double? DefaultDensity { get; set; }
}

/// <summary>
/// DTO for hotend model seed data from YAML
/// </summary>
public class HotendModelSeedDto
{
    [Required]
    public string Name { get; set; } = string.Empty;

    [Required]
    public string Manufacturer { get; set; } = string.Empty;

    public int MaxTemp { get; set; } = 300;

    public bool IsHighFlow { get; set; }

    /// <summary>
    /// Maximum volumetric flow rate in mm³/s.
    /// </summary>
    public double? MaxFlowRate { get; set; }

    public string? Description { get; set; }

    public string? Url { get; set; }
}

/// <summary>
/// DTO for extruder model seed data from YAML
/// </summary>
public class ExtruderModelSeedDto
{
    [Required]
    public string Name { get; set; } = string.Empty;

    [Required]
    public string Manufacturer { get; set; } = string.Empty;

    public string GearRatio { get; set; } = "3:1";

    public bool IsDirectDrive { get; set; } = true;

    public string? Description { get; set; }

    public string? Url { get; set; }
}

/// <summary>
/// DTO for toolhead model seed data from YAML
/// </summary>
public class ToolheadModelSeedDto
{
    [Required]
    public string Name { get; set; } = string.Empty;

    [Required]
    public string Manufacturer { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string? Url { get; set; }

    public string? DefaultHotend { get; set; }

    public string? DefaultExtruder { get; set; }

    public string? DefaultNozzle { get; set; }
}

/// <summary>
/// DTO for nozzle model seed data from YAML
/// </summary>
public class NozzleModelSeedDto
{
    [Required]
    public string Name { get; set; } = string.Empty;

    [Required]
    public string Manufacturer { get; set; } = string.Empty;

    public double Diameter { get; set; } = 0.4;

    public int MaxTemp { get; set; } = 300;

    public string NozzleType { get; set; } = "Brass";

    public string? Description { get; set; }

    public string? Url { get; set; }
}

/// <summary>
/// DTO for deserializing maintenance component (spare part) seed data from YAML.
/// Establishes initial parts inventory with category taxonomy.
/// Note: inStock is NOT seeded — every deployment starts at 0.
/// </summary>
public class MaintenanceComponentSeedDto
{
    [Required]
    public string Name { get; set; } = string.Empty;

    [Required]
    public string Category { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string? Sku { get; set; }

    public decimal? UnitCost { get; set; }

    public string? Supplier { get; set; }

    public string? Url { get; set; }

    public int RecommendedMinimumStock { get; set; }
}

/// <summary>\n/// DTO for global maintenance task catalog seed data from YAML.\n/// </summary>
public class MaintenanceTaskSeedDto
{
    [Required]
    public string TaskName { get; set; } = string.Empty;

    [Required]
    public string Category { get; set; } = string.Empty;

    public string? Description { get; set; }

    public double? IntervalHours { get; set; }

    public int? IntervalDays { get; set; }

    public int? EstimatedDurationMinutes { get; set; }

    public int Priority { get; set; } = 2;

    public bool IsActive { get; set; } = true;

    // Scope rules — nullable bools
    public bool? RequiresEnclosure { get; set; }

    public bool? RequiresCarbonFilter { get; set; }

    public bool? RequiresHepaFilter { get; set; }

    public bool? RequiresBowdenTube { get; set; }

    public bool? RequiresPtfeLiner { get; set; }

    public bool? RequiresLinearRails { get; set; }

    public bool? RequiresLeadScrews { get; set; }

    public bool? RequiresToolchanger { get; set; }

    public bool? RequiresFilamentCutter { get; set; }

    public bool? RequiresHeatedChamber { get; set; }

    public bool? RequiresHeatedBed { get; set; }

    public bool? RequiresMultiMaterial { get; set; }
}

/// <summary>
/// DTO for deserializing maintenance plan seed data from YAML.
/// Plans reference tasks by name — resolved to IDs at seed time.
/// </summary>
public class MaintenancePlanSeedDto
{
    [Required]
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>
    /// List of task names to include in this plan (resolved by name at seed time).
    /// </summary>
    public List<string> Tasks { get; set; } = [];
}
