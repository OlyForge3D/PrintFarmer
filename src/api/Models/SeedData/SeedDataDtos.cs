using System.ComponentModel.DataAnnotations;

namespace Farm.Web.Api.Models.SeedData;

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

    public bool SupportsAutoLeveling { get; set; }

    public bool MultiMaterial { get; set; }

    public int NumberOfExtruders { get; set; } = 1;

    public int? MinHotendTemp { get; set; }

    public int? MaxHotendTemp { get; set; }

    public int? MinBedTemp { get; set; }

    public int? MaxBedTemp { get; set; }

    public int? MaxPrintSpeed { get; set; }

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

    public int MaxTemp { get; set; } = 300;

    public string NozzleType { get; set; } = "Brass";

    public string? Description { get; set; }

    public string? Url { get; set; }
}
