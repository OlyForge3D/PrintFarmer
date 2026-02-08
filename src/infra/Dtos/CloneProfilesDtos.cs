namespace Farm.Infrastructure;

/// <summary>
/// Request DTO for cloning profiles from a template machine profile to a custom printer.
/// </summary>
public class CloneProfilesRequestDto
{
    public Guid SourceMachineProfileId { get; set; } // Machine profile to clone from (e.g., "Prusa CORE One")

    public Guid TargetPrinterId { get; set; } // Printer to clone profiles to (e.g., "Prusa CORE One L custom instance)
}

/// <summary>
/// Response DTO for profile cloning operation results.
/// </summary>
public class CloneProfilesResponseDto
{
    public Guid SourceMachineProfileId { get; set; }

    public string SourceMachineName { get; set; } = string.Empty;

    public Guid TargetPrinterId { get; set; }

    public string TargetPrinterName { get; set; } = string.Empty;

    public int ProcessProfilesCloned { get; set; }

    public int FilamentProfilesCloned { get; set; }

    public int TotalProfilesCloned { get; set; }
}

/// <summary>
/// Request DTO for cloning a single profile to create a custom copy.
/// </summary>
public class CloneSingleProfileRequestDto
{
    /// <summary>
    /// ID of the source profile to clone from.
    /// </summary>
    public Guid SourceProfileId { get; set; }

    /// <summary>
    /// Type of profile being cloned: "machine", "filament", or "process".
    /// </summary>
    public string ProfileType { get; set; } = string.Empty;

    /// <summary>
    /// Name for the new custom profile. If not provided, uses original name with "(Custom)" suffix.
    /// </summary>
    public string? Name { get; set; }
}

/// <summary>
/// Response DTO for single profile clone operation.
/// </summary>
public class CloneSingleProfileResponseDto
{
    /// <summary>
    /// ID of the newly created custom profile.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Name of the cloned profile.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Type of profile: "machine", "filament", or "process".
    /// </summary>
    public string ProfileType { get; set; } = string.Empty;

    /// <summary>
    /// Whether this is a system profile (always false for cloned profiles).
    /// </summary>
    public bool IsSystem { get; set; }
}

/// <summary>
/// Request DTO for uploading a custom profile from raw JSON.
/// </summary>
public class UploadProfileRequestDto
{
    /// <summary>
    /// Raw slicer profile JSON content.
    /// </summary>
    public string RawJson { get; set; } = string.Empty;

    /// <summary>
    /// Type of profile being uploaded: "machine", "filament", or "process".
    /// </summary>
    public string ProfileType { get; set; } = string.Empty;

    /// <summary>
    /// Name for the custom profile. If not provided, extracted from JSON if possible.
    /// </summary>
    public string? Name { get; set; }
}

/// <summary>
/// DTO representing a user's custom profile (IsSystem=false).
/// </summary>
public class CustomProfileDto
{
    /// <summary>
    /// Profile ID.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Profile name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Type of profile: "machine", "filament", or "process".
    /// </summary>
    public string ProfileType { get; set; } = string.Empty;

    /// <summary>
    /// Whether this is a system profile (always false for custom profiles).
    /// </summary>
    public bool IsSystem { get; set; }

    /// <summary>
    /// When the profile was created.
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// When the profile was last updated.
    /// </summary>
    public DateTime? UpdatedAt { get; set; }

    /// <summary>
    /// Optional description of customizations.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// The raw JSON content of the profile.
    /// </summary>
    public string? RawJson { get; set; }
}

/// <summary>
/// Request DTO for updating a custom profile.
/// </summary>
public class UpdateCustomProfileRequestDto
{
    /// <summary>
    /// Updated raw JSON content. If null, only name is updated.
    /// </summary>
    public string? RawJson { get; set; }

    /// <summary>
    /// Updated profile name. If null, only RawJson is updated.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Updated description.
    /// </summary>
    public string? Description { get; set; }
}

/// <summary>
/// Response DTO for listing custom profiles with summary counts.
/// </summary>
public class CustomProfilesListResponseDto
{
    /// <summary>
    /// List of user's custom profiles.
    /// </summary>
    public IReadOnlyList<CustomProfileDto> Profiles { get; set; } = [];

    /// <summary>
    /// Total count of custom profiles.
    /// </summary>
    public int TotalCount { get; set; }

    /// <summary>
    /// Count of custom machine profiles.
    /// </summary>
    public int MachineProfileCount { get; set; }

    /// <summary>
    /// Count of custom process profiles.
    /// </summary>
    public int ProcessProfileCount { get; set; }

    /// <summary>
    /// Count of custom filament profiles.
    /// </summary>
    public int FilamentProfileCount { get; set; }
}
