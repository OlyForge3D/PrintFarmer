namespace Farm.Infrastructure;

// ============================================================================
// COMPONENT MODEL CREATE/UPDATE DTOs
// Used for CRUD operations on hardware component definitions (hotends, extruders, etc.)
// Response DTOs are defined in their respective files (HotendModelDto.cs, etc.)
// ============================================================================
#region Hotend Model DTOs

/// <summary>
/// DTO for creating a new hotend model definition.
/// </summary>
/// <param name="Name">Name of the hotend model (e.g., "Dragon HF", "Mosquito")</param>
/// <param name="ManufacturerId">ID of the manufacturer</param>
/// <param name="MaxTemp">Maximum temperature rating in °C</param>
/// <param name="IsHighFlow">Whether this is a high-flow variant</param>
/// <param name="Description">Optional description</param>
/// <param name="Url">Optional product URL</param>
public record CreateHotendModelDto(
    string Name,
    Guid ManufacturerId,
    int? MaxTemp = null,
    bool IsHighFlow = false,
    string? Description = null,
    string? Url = null);

/// <summary>
/// DTO for updating an existing hotend model definition.
/// All fields are optional - only provided fields are updated.
/// </summary>
public record UpdateHotendModelDto(
    string? Name = null,
    Guid? ManufacturerId = null,
    int? MaxTemp = null,
    bool? IsHighFlow = null,
    string? Description = null,
    string? Url = null);

#endregion

#region Extruder Model DTOs

/// <summary>
/// DTO for creating a new extruder model definition.
/// </summary>
/// <param name="Name">Name of the extruder model (e.g., "BMG", "Orbiter 2.0")</param>
/// <param name="ManufacturerId">ID of the manufacturer</param>
/// <param name="GearRatio">Gear ratio (e.g., "3:1", "7.5:1")</param>
/// <param name="IsDirectDrive">Whether this is a direct drive extruder</param>
/// <param name="Description">Optional description</param>
/// <param name="Url">Optional product URL</param>
public record CreateExtruderModelDto(
    string Name,
    Guid ManufacturerId,
    string? GearRatio = null,
    bool IsDirectDrive = true,
    string? Description = null,
    string? Url = null);

/// <summary>
/// DTO for updating an existing extruder model definition.
/// All fields are optional - only provided fields are updated.
/// </summary>
public record UpdateExtruderModelDto(
    string? Name = null,
    Guid? ManufacturerId = null,
    string? GearRatio = null,
    bool? IsDirectDrive = null,
    string? Description = null,
    string? Url = null);

#endregion

#region Toolhead Model DTOs

/// <summary>
/// DTO for creating a new toolhead model definition.
/// </summary>
/// <param name="Name">Name of the toolhead model (e.g., "StealthBurner", "DragonBurner")</param>
/// <param name="ManufacturerId">ID of the manufacturer</param>
/// <param name="Description">Optional description</param>
/// <param name="Url">Optional product/documentation URL</param>
public record CreateToolheadModelDto(
    string Name,
    Guid ManufacturerId,
    string? Description = null,
    string? Url = null);

/// <summary>
/// DTO for updating an existing toolhead model definition.
/// All fields are optional - only provided fields are updated.
/// </summary>
public record UpdateToolheadModelDefDto(
    string? Name = null,
    Guid? ManufacturerId = null,
    string? Description = null,
    string? Url = null);

#endregion

#region Nozzle Model DTOs

/// <summary>
/// DTO for creating a new nozzle model definition.
/// </summary>
/// <param name="Name">Name of the nozzle model (e.g., "Undertaker", "Vanadium")</param>
/// <param name="ManufacturerId">ID of the manufacturer</param>
/// <param name="MaxTemp">Maximum temperature rating in °C</param>
/// <param name="IsHardened">Whether this nozzle is hardened for abrasive filaments</param>
/// <param name="Description">Optional description</param>
/// <param name="Url">Optional product URL</param>
public record CreateNozzleModelDto(
    string Name,
    Guid ManufacturerId,
    int? MaxTemp = null,
    bool IsHardened = false,
    string? Description = null,
    string? Url = null);

/// <summary>
/// DTO for updating an existing nozzle model definition.
/// All fields are optional - only provided fields are updated.
/// </summary>
public record UpdateNozzleModelDto(
    string? Name = null,
    Guid? ManufacturerId = null,
    int? MaxTemp = null,
    bool? IsHardened = null,
    string? Description = null,
    string? Url = null);

#endregion

#region Contextual Manufacturer DTOs

/// <summary>
/// Context types for filtering manufacturers by what items they have.
/// </summary>
public enum CatalogContext
{
    Printers,
    Hotends,
    Extruders,
    Toolheads,
    Nozzles
}

/// <summary>
/// Manufacturer with item count for a specific context.
/// </summary>
/// <param name="Id">Manufacturer ID</param>
/// <param name="Name">Manufacturer name</param>
/// <param name="ItemCount">Number of items in the current context</param>
public record ManufacturerWithCountDto(
    Guid Id,
    string Name,
    int ItemCount);

/// <summary>
/// Response DTO grouping manufacturers by whether they have items in a context.
/// </summary>
/// <param name="WithItems">Manufacturers that have at least one item</param>
/// <param name="WithoutItems">Manufacturers that have no items</param>
public record ManufacturersByContextDto(
    IReadOnlyList<ManufacturerWithCountDto> WithItems,
    IReadOnlyList<ManufacturerWithCountDto> WithoutItems);

#endregion
