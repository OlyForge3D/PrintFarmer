namespace Farm.Infrastructure.Domain;

/// <summary>
/// Represents a single hotend/nozzle configuration.
/// Supports multi-toolhead printers (Prusa XL, Bambu Lab X1, etc.).
/// 
/// For single-toolhead printers, there will be one Toolhead per Printer.
/// For multi-toolhead printers (Prusa XL, Bambu Lab X1, etc.), there will be multiple Toolheads, one for each hotend.
/// </summary>
public class Toolhead
{
    /// <summary>
    /// Unique identifier for this toolhead.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Reference to the printer this toolhead belongs to.
    /// </summary>
    public Guid PrinterId { get; set; }

    /// <summary>
    /// Friendly name for this toolhead (e.g., "Extruder 1", "Left Tool", "Primary").
    /// Default is typically "Extruder 1", "Extruder 2", etc.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Zero-based index of this toolhead (0 = first, 1 = second, etc.).
    /// Important for distinguishing multiple toolheads in APIs.
    /// </summary>
    public int Index { get; set; }

    /// <summary>
    /// Nozzle diameter in millimeters (e.g., 0.4, 0.6, 0.8, 1.0).
    /// </summary>
    public double? NozzleDiameter { get; set; }

    /// <summary>
    /// Minimum hotend temperature in °C for this toolhead.
    /// </summary>
    public int? MinHotendTemp { get; set; }

    /// <summary>
    /// Maximum hotend temperature in °C for this toolhead.
    /// </summary>
    public int? MaxHotendTemp { get; set; }

    /// <summary>
    /// Materials this toolhead is rated for (e.g., ["PLA", "PETG", "ABS"]).
    /// Stored as JSON array in database.
    /// </summary>
    public string[]? SupportedMaterials { get; set; }

    /// <summary>
    /// Whether this toolhead has a heated chamber/enclosure for high-temp materials.
    /// </summary>
    public bool HasHeatedEnclosure { get; set; }

    /// <summary>
    /// Indicates if this is the primary/default toolhead for single-tool operations.
    /// For single-toolhead printers, this should be true.
    /// For multi-toolhead printers, typically only the first toolhead is primary.
    /// </summary>
    public bool IsPrimary { get; set; }

    /// <summary>
    /// When this toolhead configuration was last updated.
    /// </summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation property
    public Printer? Printer { get; set; }
}
