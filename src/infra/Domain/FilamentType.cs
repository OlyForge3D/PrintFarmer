using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Diagnostics.CodeAnalysis;
using Farm.Infrastructure;
using Farm.Infrastructure.Annotations;

namespace Farm.Infrastructure.Domain;

public class FilamentType
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public double? DefaultHotendTemp { get; set; }

    public double? DefaultBedTemp { get; set; }

    /// <summary>
    /// Indicates if this filament type contains abrasive materials (e.g., carbon fiber, glass fiber, glow-in-the-dark).
    /// Abrasive filaments require hardened nozzles to prevent excessive wear.
    /// </summary>
    public bool IsAbrasive { get; set; }

    /// <summary>
    /// Indicates if this filament type requires an enclosure for optimal printing (e.g., ABS, ASA, PC).
    /// </summary>
    public bool NeedsEnclosure { get; set; }

    /// <summary>
    /// Default price per kilogram in USD, used for cost estimation when no spool-level price is available.
    /// </summary>
    public double? DefaultPricePerKg { get; set; }

    /// <summary>
    /// Default material density in g/cm³, used for weight-based cost calculation from gcode volume estimates.
    /// </summary>
    public double? DefaultDensity { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<PrinterModel> PrinterModels { get; } = new List<PrinterModel>();

    public bool IsActive { get; set; } = true;
}
