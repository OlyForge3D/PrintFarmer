namespace Farm.Slicer.Module.Domain;

/// <summary>
/// Filament/Material profile from OrcaSlicer.
/// Contains material-specific settings like temperature, speed, etc.
/// Stored separately from machine and process profiles as they have no overlap.
/// </summary>
public class FilamentProfile
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Material { get; set; } = "PLA";

    public string? Manufacturer { get; set; }

    public string? Description { get; set; }

    public SlicerType SlicerType { get; set; }

    public int NozzleTemperature { get; set; } = 210; // °C

    public int BedTemperature { get; set; } = 60; // °C

    public int PrintSpeed { get; set; } = 50; // mm/s

    public string? RawJson { get; set; }

    public string? SettingsJson { get; set; }

    public string? Hash { get; set; }

    /// <summary>
    /// Comma-separated list of compatible machine profile names.
    /// Extracted from OrcaSlicer's compatible_printers array during import.
    /// </summary>
    public string? CompatiblePrinters { get; set; }

    public bool IsSystem { get; set; }

    public bool IsDefault { get; set; }

    public bool IsPublic { get; set; } = true;

    public string? SlicerVersion { get; set; }

    /// <summary>Soft reference to the user who created this profile (no FK constraint).</summary>
    public Guid? CreatedByUserId { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
