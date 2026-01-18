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
    // No additional properties - toolheads only have base properties
    // ManufacturerId inherited as nullable from HardwareModel
}

/// <summary>
/// A nozzle model definition (e.g., Undertaker, GammaMaster, Vanadium).
/// References manufacturer for brand association.
/// </summary>
public class NozzleModelDefinition : HardwareModel
{
    /// <summary>
    /// Maximum temperature rating in °C.
    /// </summary>
    public int? MaxTemp { get; set; }

    /// <summary>
    /// Whether this nozzle is hardened for abrasive filaments.
    /// </summary>
    public bool IsHardened { get; set; }
}
