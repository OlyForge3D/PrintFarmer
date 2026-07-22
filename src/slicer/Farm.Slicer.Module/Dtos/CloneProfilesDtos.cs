namespace Farm.Slicer.Module.Dtos;

/// <summary>
/// Request DTO for cloning profiles from a template machine profile to a custom printer.
/// </summary>
public class CloneProfilesRequestDto
{
    public Guid SourceMachineProfileId { get; set; } // Machine profile to clone from (e.g., "Prusa CORE One")

    public Guid TargetPrinterId { get; set; } // Printer to clone profiles to (e.g., "Prusa CORE One L custom instance)
}

#pragma warning disable SA1402 // File may only contain a single type

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

    /// <summary>
    /// Optional override of the catalog PrinterModel association for the cloned profile.
    /// When null, the cloned profile inherits the source profile's PrinterModelId
    /// (only relevant for machine and process profiles; filament profiles ignore this field).
    /// </summary>
    public Guid? PrinterModelId { get; set; }

    /// <summary>
    /// Optional override of the compatible-printers list for cloned filament profiles.
    /// When null, the cloned profile inherits the source's CompatiblePrinters string.
    /// Ignored for machine and process profiles.
    /// </summary>
    public IReadOnlyList<string>? CompatiblePrinters { get; set; }
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

    /// <summary>
    /// Optional explicit catalog PrinterModel association for the uploaded profile.
    /// When null, the service attempts to resolve the association from the raw JSON
    /// (printer_model / compatible_printers) via the printer-model alias service.
    /// Filament profiles ignore this field.
    /// </summary>
    public Guid? PrinterModelId { get; set; }

    /// <summary>
    /// Optional list of compatible machine variant names (e.g. "Prusa CORE One 0.4 nozzle")
    /// for filament profiles. When null/empty on a filament upload, the service attempts
    /// to extract the values from the <c>compatible_printers</c> array in the raw JSON.
    /// Ignored for machine and process profiles (those use PrinterModelId / their own JSON).
    /// </summary>
    public IReadOnlyList<string>? CompatiblePrinters { get; set; }
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

    /// <summary>
    /// Catalog PrinterModel association for this profile (machine and process only;
    /// always null for filament profiles which use CompatiblePrinters strings instead).
    /// </summary>
    public Guid? PrinterModelId { get; set; }

    /// <summary>
    /// List of compatible machine variant names (filament profiles only). Always null
    /// for machine and process profiles — those use <see cref="PrinterModelId" /> instead.
    /// </summary>
    public IReadOnlyList<string>? CompatiblePrinters { get; set; }
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

    /// <summary>
    /// Updated catalog PrinterModel association for the profile (machine/process only).
    /// When null, the existing association is left unchanged. To explicitly clear an
    /// existing association, set <see cref="ClearPrinterModelId" /> to true.
    /// Filament profiles ignore this field.
    /// </summary>
    public Guid? PrinterModelId { get; set; }

    /// <summary>
    /// When true, clears the profile's PrinterModelId (sets it to null). Takes precedence
    /// over <see cref="PrinterModelId" /> when both are supplied. Filament profiles ignore
    /// this field.
    /// </summary>
    public bool? ClearPrinterModelId { get; set; }

    /// <summary>
    /// Updated list of compatible machine variant names for filament profiles. When null,
    /// the existing list is left unchanged. To explicitly clear it, set
    /// <see cref="ClearCompatiblePrinters" /> to true. Ignored for machine/process profiles.
    /// </summary>
    public IReadOnlyList<string>? CompatiblePrinters { get; set; }

    /// <summary>
    /// When true, clears the filament profile's CompatiblePrinters list. Takes precedence
    /// over <see cref="CompatiblePrinters" /> when both are supplied. Ignored for
    /// machine and process profiles.
    /// </summary>
    public bool? ClearCompatiblePrinters { get; set; }
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

#pragma warning restore SA1402
