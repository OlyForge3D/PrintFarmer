namespace Farm.Infrastructure.Domain;

/// <summary>
/// Abstract base class for hardware component models (hotends, extruders, toolheads, nozzles).
/// Provides common properties for identification, manufacturer association, and documentation.
/// </summary>
public abstract class HardwareModel
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Manufacturer ID. Required - use "Community", "Unknown", or specific community
    /// manufacturer entries (e.g., "Voron Design") for open-source hardware.
    /// </summary>
    public Guid ManufacturerId { get; set; }

    /// <summary>
    /// Optional description or notes about this hardware.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// URL to the product page, GitHub repo, or documentation.
    /// </summary>
    public string? Url { get; set; }

    // Navigation
    public Manufacturer? Manufacturer { get; set; }
}

/// <summary>
/// Defines the nozzle thread/interface type that determines compatibility between hotends and nozzles.
/// This is the physical interface standard - hotends and nozzles must match to be compatible.
/// </summary>
public enum NozzleInterfaceType
{
    /// <summary>Unknown or unspecified nozzle interface.</summary>
    Unknown = 0,

    /// <summary>E3D V6 standard thread (M6 x 1.0) - most common. Used by V6, Dragon, Rapido, Mosquito, CHC, most budget hotends.</summary>
    V6 = 1,

    /// <summary>E3D Volcano extended length (M6 x 1.0, longer melt zone) - for high-flow applications.</summary>
    Volcano = 2,

    /// <summary>E3D Revo quick-change system - no threading, magnetic/snap-fit.</summary>
    Revo = 3,

    /// <summary>Prusa Nextruder interface - proprietary for MK4/MK3.9S/CORE One.</summary>
    Nextruder = 4,

    /// <summary>BIQU H2 interface - proprietary for H2 hotend system.</summary>
    H2 = 5,

    /// <summary>Microswiss FlowTech interface - proprietary across their FlowTech line.</summary>
    FlowTech = 6,

    /// <summary>Bambu Lab proprietary interface - for X1/P1/A1 series.</summary>
    BambuLab = 7,

    /// <summary>Proprietary interface unique to a specific manufacturer/model.</summary>
    Proprietary = 99
}

/// <summary>
/// A hotend model definition (e.g., Dragon, Rapido, Mosquito).
/// References manufacturer for brand association.
/// </summary>
public class HotendModelDefinition : HardwareModel
{
    /// <summary>
    /// Max temperature rating in °C (e.g., 500 for all-metal).
    /// </summary>
    public int? MaxTemp { get; set; }

    /// <summary>
    /// Whether this is a high-flow variant.
    /// </summary>
    public bool IsHighFlow { get; set; }

    /// <summary>
    /// The nozzle interface type this hotend uses (determines compatible nozzles).
    /// </summary>
    public NozzleInterfaceType NozzleInterface { get; set; } = NozzleInterfaceType.V6;
}

/// <summary>
/// An extruder model definition (e.g., BMG, LGX, Orbiter).
/// References manufacturer for brand association.
/// </summary>
public class ExtruderModelDefinition : HardwareModel
{
    /// <summary>
    /// Gear ratio (e.g., 3:1, 7.5:1).
    /// </summary>
    public string? GearRatio { get; set; }

    /// <summary>
    /// Whether this is a direct drive extruder.
    /// </summary>
    public bool IsDirectDrive { get; set; } = true;
}

/// <summary>
/// A toolhead model definition (e.g., StealthBurner, DragonBurner, Xol).
/// References manufacturer for brand association.
/// ManufacturerId is nullable because many toolheads are community designs.
/// </summary>
public class ToolheadModelDefinition : HardwareModel
{
    /// <summary>
    /// Default hotend model for this toolhead (optional).
    /// When a user selects this toolhead, this hotend is auto-populated.
    /// </summary>
    public Guid? DefaultHotendId { get; set; }

    /// <summary>
    /// Default extruder model for this toolhead (optional).
    /// When a user selects this toolhead, this extruder is auto-populated.
    /// </summary>
    public Guid? DefaultExtruderId { get; set; }

    /// <summary>
    /// Default nozzle model for this toolhead (optional).
    /// When a user selects this toolhead, this nozzle is auto-populated.
    /// </summary>
    public Guid? DefaultNozzleId { get; set; }

    // Navigation properties
    public HotendModelDefinition? DefaultHotend { get; set; }

    public ExtruderModelDefinition? DefaultExtruder { get; set; }

    public NozzleModelDefinition? DefaultNozzle { get; set; }
}

/// <summary>
/// A nozzle model definition (e.g., Undertaker, GammaMaster, Vanadium).
/// References manufacturer for brand association.
/// </summary>
public class NozzleModelDefinition : HardwareModel
{
    /// <summary>
    /// Nozzle diameter in millimeters (e.g., 0.4, 0.6, 0.8, 1.0).
    /// </summary>
    public double Diameter { get; set; } = 0.4;

    /// <summary>
    /// Maximum temperature rating in °C.
    /// </summary>
    public int? MaxTemp { get; set; }

    /// <summary>
    /// The material type of this nozzle (Brass, HardenedSteel, StainlessSteel, etc.).
    /// </summary>
    public NozzleType NozzleType { get; set; } = NozzleType.Brass;

    /// <summary>
    /// Whether this nozzle is hardened for abrasive filaments.
    /// Computed from NozzleType - not persisted to database.
    /// </summary>
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public bool IsHardened => NozzleType is NozzleType.HardenedSteel or NozzleType.TungstenCarbide or NozzleType.Abrasive;

    /// <summary>
    /// The nozzle interface type (determines which hotends this nozzle fits).
    /// </summary>
    public NozzleInterfaceType NozzleInterface { get; set; } = NozzleInterfaceType.V6;
}
