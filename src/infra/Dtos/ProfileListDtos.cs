namespace Farm.Infrastructure;

/// <summary>
/// Base interface for profile list items used in slicer profile listings.
/// Provides common properties shared across all profile types (process, filament, machine).
/// </summary>
public interface IProfileListItem
{
    /// <summary>Gets the unique identifier of the profile.</summary>
    Guid Id { get; }

    /// <summary>Gets the display name of the profile.</summary>
    string Name { get; }

    /// <summary>Gets the slicer type this profile is for (e.g., "OrcaSlicer", "PrusaSlicer").</summary>
    string SlicerType { get; }

    /// <summary>Gets a value indicating whether this is the default profile for its type.</summary>
    bool IsDefault { get; }

    /// <summary>Gets a value indicating whether this is a system-provided profile.</summary>
    bool IsSystem { get; }

    /// <summary>Gets a value indicating whether this profile is publicly shared.</summary>
    bool IsPublic { get; }

    /// <summary>Gets the content hash for change detection and caching.</summary>
    string Hash { get; }

    /// <summary>Gets the profile type identifier (e.g., "process", "filament", "machine").</summary>
    string ProfileType { get; }
}

/// <summary>
/// Process profile list item DTO
/// </summary>
public class ProcessProfileListItemDto : IProfileListItem
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string SlicerType { get; set; } = string.Empty;

    public string Quality { get; set; } = string.Empty;

    public double LayerHeight { get; set; }

    public int InfillPercentage { get; set; }

    public bool IsDefault { get; set; }

    public bool IsSystem { get; set; }

    public bool IsPublic { get; set; }

    public string Hash { get; set; } = string.Empty;

    public string ProfileType => "process";
}

/// <summary>
/// Filament profile list item DTO
/// </summary>
#pragma warning disable SA1402 // File may only contain a single type
public class FilamentProfileListItemDto : IProfileListItem
#pragma warning restore SA1402 // File may only contain a single type
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string SlicerType { get; set; } = string.Empty;

    public string Material { get; set; } = string.Empty;

    public int? NozzleTemperature { get; set; }

    public int? BedTemperature { get; set; }

    public int PrintSpeed { get; set; }

    public bool IsDefault { get; set; }

    public bool IsSystem { get; set; }

    public bool IsPublic { get; set; }

    public string Hash { get; set; } = string.Empty;

    public string ProfileType => "filament";
}

/// <summary>
/// Machine profile list item DTO
/// </summary>
#pragma warning disable SA1402 // File may only contain a single type
public class MachineProfileListItemDto : IProfileListItem
#pragma warning restore SA1402 // File may only contain a single type
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string SlicerType { get; set; } = string.Empty;

    public string Manufacturer { get; set; } = string.Empty;

    public bool IsDefault { get; set; }

    public bool IsSystem { get; set; }

    public bool IsPublic { get; set; }

    public string Hash { get; set; } = string.Empty;

    public string ProfileType => "machine";
}

// Backwards compatibility alias - use ProcessProfileListItemDto directly
#pragma warning disable S2094 // Empty class alias required for backward compatibility
#pragma warning disable SA1402 // File may only contain a single type
public class SlicerProfileListItemDto : ProcessProfileListItemDto
#pragma warning restore SA1402 // File may only contain a single type
{
}

#pragma warning restore S2094

/// <summary>
/// Response containing all profile types organized separately
/// </summary>
#pragma warning disable SA1402 // File may only contain a single type
public class ExtendedProfilesResponseDto
#pragma warning restore SA1402 // File may only contain a single type
{
    public IList<ProcessProfileListItemDto> ProcessProfiles { get; set; } = new List<ProcessProfileListItemDto>();

    public IList<FilamentProfileListItemDto> FilamentProfiles { get; set; } = new List<FilamentProfileListItemDto>();

    public IList<MachineProfileListItemDto> MachineProfiles { get; set; } = new List<MachineProfileListItemDto>();
}

/// <summary>
/// Response containing names of already-imported profiles for a printer model.
/// Used by the import wizard to pre-check already-imported profiles.
/// </summary>
#pragma warning disable SA1402 // File may only contain a single type
public class ImportedProfileNamesDto
#pragma warning restore SA1402 // File may only contain a single type
{
    /// <summary>
    /// Names of machine profiles already imported for the model
    /// </summary>
    public IList<string> MachineProfileNames { get; set; } = new List<string>();

    /// <summary>
    /// Names of process profiles already imported for the model
    /// </summary>
    public IList<string> ProcessProfileNames { get; set; } = new List<string>();

    /// <summary>
    /// Names of filament profiles already imported for the model
    /// </summary>
    public IList<string> FilamentProfileNames { get; set; } = new List<string>();
}
