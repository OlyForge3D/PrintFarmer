using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Diagnostics.CodeAnalysis;
using Farm.Infrastructure;
using Farm.Infrastructure.Annotations;

namespace Farm.Infrastructure.Domain;

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

    public string? RawJson { get; set; } // Full profile JSON

    public string? SettingsJson { get; set; } // Extracted settings as key-value pairs

    public string? Hash { get; set; } // SHA256 for deduplication

    public bool IsSystem { get; set; } // From OrcaSlicer system profiles

    public bool IsDefault { get; set; } // Can be set as default filament

    public bool IsPublic { get; set; } = true; // Can be used by other users

    public string? SlicerVersion { get; set; }

    public Guid? CreatedByUserId { get; set; }

    public User? CreatedByUser { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
