namespace Farm.Infrastructure;

/// <summary>
/// Toolhead configuration for import/export.
/// Represents a single hotend/nozzle configuration on a printer.
/// </summary>
public class CreateToolheadDto
{
    /// <summary>
    /// Unique identifier (from exported data or generated on create).
    /// </summary>
    public Guid? Id { get; set; }

    /// <summary>
    /// Friendly name for this toolhead (e.g., "Extruder 1", "Left Tool").
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Zero-based index of this toolhead.
    /// </summary>
    public int Index { get; set; }

    /// <summary>
    /// Nozzle diameter in millimeters.
    /// </summary>
    public double? NozzleDiameter { get; set; }

    /// <summary>
    /// Maximum hotend temperature in °C.
    /// </summary>
    public int? MaxHotendTemp { get; set; }

    /// <summary>
    /// Materials this toolhead is rated for.
    /// </summary>
    public string[]? SupportedMaterials { get; set; }

    /// <summary>
    /// Whether this is the primary/default toolhead.
    /// </summary>
    public bool IsPrimary { get; set; }
}

/// <summary>
/// Toolhead data for reading/display purposes.
/// </summary>
public record ToolheadDto(
    Guid Id,
    string? Name,
    int Index,
    double? NozzleDiameter,
    int? MaxHotendTemp,
    string[]? SupportedMaterials,
    bool IsPrimary,
    DateTime? LastUpdated = null);
