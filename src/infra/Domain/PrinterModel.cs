using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Diagnostics.CodeAnalysis;
using Farm.Infrastructure;
using Farm.Infrastructure.Annotations;

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

    public bool MultiMaterial { get; set; }

    public int NumberOfExtruders { get; set; } = 1;

    public bool SupportsAutoLeveling { get; set; }

    public int? MaxBedTemp { get; set; } = 120;

    public int? MaxPrintSpeed { get; set; } = 150; // mm/s

    public ICollection<PrinterModelFilamentType> SupportedFilamentTypes { get; } = new List<PrinterModelFilamentType>();

    // Toolhead templates for multi-toolhead printers (contains nozzle diameter and max hotend temp)
    public ICollection<PrinterModelToolhead> Toolheads { get; } = new List<PrinterModelToolhead>();

    // Asset URLs for UI display
    public string? CoverImageUrl { get; set; } // URL to printer cover image (from OrcaSlicer assets)

    public string? BedTextureUrl { get; set; } // URL to bed texture image (from OrcaSlicer assets)

    public bool IsActive { get; set; } = true;
}
