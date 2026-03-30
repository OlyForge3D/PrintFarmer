using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Diagnostics.CodeAnalysis;
using Farm.Infrastructure;
using Farm.Infrastructure.Annotations;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Domain;

// Explicit table mapping to ensure EF Core creates the expected "PrinterModels" table during test initialization.
[Table("PrinterModels")]
public class PrinterModel
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public Guid ManufacturerId { get; set; }

    public Manufacturer? Manufacturer { get; set; }

    public int? MotionType { get; set; } // MotionType enum: 0=Cartesian, 1=CoreXY, 2=Delta, 99=Unknown

    public double? MaxX { get; set; }

    public double? MaxY { get; set; }

    public double? MaxZ { get; set; }

    public int? DefaultBackend { get; set; } // Stored as int: cast to PrinterBackend enum (0=Unknown, 1=Moonraker, 2=PrusaLink, 3=SDCP, 4=OctoPrint)

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

    public bool MultiMaterial { get; set; }

    public bool SupportsAutoLeveling { get; set; }

    public int? MaxBedTemp { get; set; } = 120;

    public int? MaxPrintSpeed { get; set; } = 150; // mm/s

    public ICollection<FilamentType> SupportedFilamentTypes { get; } = new List<FilamentType>();

    // Toolhead templates for multi-toolhead printers (contains nozzle diameter and max hotend temp)
    public ICollection<PrinterModelToolhead> Toolheads { get; } = new List<PrinterModelToolhead>();

    /// <summary>
    /// Slicer-specific model names that map to this printer model (e.g., "COREONEL", "MK4IS").
    /// </summary>
    public ICollection<PrinterModelAlias> Aliases { get; } = new List<PrinterModelAlias>();

    // Asset URLs for UI display
    public string? CoverImageUrl { get; set; } // URL to printer cover image (from OrcaSlicer assets)

    public string? BedTextureUrl { get; set; } // URL to bed texture image (from OrcaSlicer assets)

    /// <summary>
    /// Default power consumption in watts for this printer model.
    /// Used as fallback when per-printer wattage is not set.
    /// </summary>
    public decimal? DefaultWattage { get; set; }

    /// <summary>
    /// Default machine hourly rate for this printer model.
    /// Used as fallback when per-printer hourly rate is not set.
    /// Cascade: printer.MachineHourlyRate → model.DefaultHourlyRate → settings.DefaultMachineHourlyRate.
    /// </summary>
    public decimal? DefaultHourlyRate { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Tracks when this printer model definition was last modified.
    /// Used to detect when printers linked to this model need their configuration refreshed.
    /// </summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
