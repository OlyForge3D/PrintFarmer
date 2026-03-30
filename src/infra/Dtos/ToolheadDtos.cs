using Farm.Infrastructure.Domain;

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

    #region Component Model References

    /// <summary>
    /// Reference to a hotend model definition (e.g., Phaetus Dragon, Slice Engineering Mosquito).
    /// </summary>
    public Guid? HotendModelId { get; set; }

    /// <summary>
    /// Hotend model name (for display/export; resolved from HotendModelId on read).
    /// </summary>
    public string? HotendModelName { get; set; }

    /// <summary>
    /// Reference to an extruder model definition (e.g., Bondtech BMG, LGX).
    /// </summary>
    public Guid? ExtruderModelId { get; set; }

    /// <summary>
    /// Extruder model name (for display/export; resolved from ExtruderModelId on read).
    /// </summary>
    public string? ExtruderModelName { get; set; }

    /// <summary>
    /// Reference to a toolhead model definition (e.g., StealthBurner, DragonBurner).
    /// </summary>
    public Guid? ToolheadModelDefId { get; set; }

    /// <summary>
    /// Toolhead model name (for display/export; resolved from ToolheadModelDefId on read).
    /// </summary>
    public string? ToolheadModelDefName { get; set; }

    /// <summary>
    /// Reference to a nozzle model definition (e.g., West3D Undertaker, Slice Vanadium).
    /// </summary>
    public Guid? NozzleModelId { get; set; }

    /// <summary>
    /// Nozzle model name (for display/export; resolved from NozzleModelId on read).
    /// </summary>
    public string? NozzleModelName { get; set; }

    #endregion

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
/// Includes resolved component model names for display.
/// Nozzle diameter is derived from NozzleModel.Diameter.
/// MaxFlowRate and MaxTemp are derived from HotendModel.
/// </summary>
public record ToolheadDto(
    Guid Id,
    string? Name,
    int Index,
    double? NozzleDiameter,  // Derived from NozzleModel.Diameter
    NozzleType? NozzleType,  // Derived from NozzleModel.NozzleType
    double? MaxFlowRate,     // Derived from HotendModel.MaxFlowRate
    int? MaxTemp,            // Derived from HotendModel.MaxTemp

    // Component model references (IDs and resolved names)
    Guid? HotendModelId,
    string? HotendModelName,
    Guid? ExtruderModelId,
    string? ExtruderModelName,
    Guid? ToolheadModelDefId,
    string? ToolheadModelDefName,
    Guid? NozzleModelId,
    string? NozzleModelName,
    string[]? SupportedMaterials,
    bool IsPrimary,
    DateTime? LastUpdated = null,
    ToolheadType ToolheadType = ToolheadType.Physical,
    int? CurrentSpoolId = null,
    string? CurrentMaterial = null,
    string? CurrentFilamentColor = null);
