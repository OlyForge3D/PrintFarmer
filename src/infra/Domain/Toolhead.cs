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
    /// Maximum hotend temperature in °C for this toolhead.
    /// </summary>
    public int? MaxHotendTemp { get; set; }

    /// <summary>
    /// Maximum volumetric flow rate in mm³/s for this toolhead.
    /// Depends on hotend, nozzle size, and material.
    /// </summary>
    public double? MaxFlowRate { get; set; }

    /// <summary>
    /// Whether this is a stock or custom/aftermarket toolhead.
    /// Stored as int; cast to ToolheadType enum.
    /// </summary>
    public int? ToolheadType { get; set; }

    #region Component Model References (database-backed, extensible)

    /// <summary>
    /// Reference to a hotend model definition (e.g., Phaetus Dragon, Slice Engineering Mosquito).
    /// Nullable for stock hotends or when not specified.
    /// </summary>
    public Guid? HotendModelId { get; set; }

    /// <summary>
    /// Reference to an extruder model definition (e.g., Bondtech BMG, LGX).
    /// Nullable for stock extruders or when not specified.
    /// </summary>
    public Guid? ExtruderModelId { get; set; }

    /// <summary>
    /// Reference to a toolhead model definition (e.g., StealthBurner, DragonBurner).
    /// Nullable for stock toolheads or when not specified.
    /// </summary>
    public Guid? ToolheadModelDefId { get; set; }

    /// <summary>
    /// Reference to a nozzle model definition (e.g., West3D Undertaker, Slice Vanadium).
    /// Nullable for generic/stock nozzles or when not specified.
    /// </summary>
    public Guid? NozzleModelId { get; set; }

    #endregion

    /// <summary>
    /// Materials this toolhead is rated for (e.g., ["PLA", "PETG", "ABS"]).
    /// Stored as JSON array in database.
    /// </summary>
    public string[]? SupportedMaterials { get; set; }

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

    #region Navigation Properties

    public Printer? Printer { get; set; }

    public HotendModelDefinition? HotendModel { get; set; }

    public ExtruderModelDefinition? ExtruderModel { get; set; }

    public ToolheadModelDefinition? ToolheadModelDef { get; set; }

    public NozzleModelDefinition? NozzleModel { get; set; }

    #endregion
}
