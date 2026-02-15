namespace Farm.Slicer.Module.Dtos;

/// <summary>
/// Request to bulk-import slicer profiles by their IDs.
/// </summary>
public class BulkProfileImportRequest
{
    public List<Guid>? ProfileIds { get; set; }

    public bool? MakePublic { get; set; }
}

#pragma warning disable SA1402 // File may only contain a single type

/// <summary>
/// Result of a bulk profile import operation.
/// </summary>
public class BulkProfileImportResultDto
{
    public Guid PrinterId { get; set; }

    public string PrinterName { get; set; } = string.Empty;

    public int TotalRequested { get; set; }

    public int TotalFound { get; set; }

    public int Imported { get; set; }

    public int Duplicated { get; set; }
}

/// <summary>
/// Request to import profiles directly from the OrcaSlicer worker (not from pre-seeded database).
/// Used when profiles haven't been seeded yet and come directly from the worker.
/// </summary>
public class BulkImportFromWorkerRequest
{
    /// <summary>
    /// Profiles to import, as returned from the OrcaSlicer worker (/profiles endpoint).
    /// </summary>
    public List<SlicerProfileDto>? Profiles { get; set; }

    public bool? MakePublic { get; set; }
}

public class BulkImportFromWorkerResultDto
{
    public Guid PrinterId { get; set; }

    public string PrinterName { get; set; } = string.Empty;

    public int Imported { get; set; }

    public int Duplicated { get; set; }
}

/// <summary>
/// Request for selective profile import from the Profile Import Wizard.
/// Contains the names of profiles selected by the user for each profile type.
/// </summary>
public class SelectiveProfileImportRequest
{
    /// <summary>
    /// Manufacturer name for the printer model (e.g., "Prusa", "Elegoo").
    /// </summary>
    public string ManufacturerName { get; set; } = string.Empty;

    /// <summary>
    /// Selected machine profile names to import.
    /// </summary>
    public List<string> SelectedMachineProfiles { get; set; } = new();

    /// <summary>
    /// Selected process profile names to import.
    /// </summary>
    public List<string> SelectedProcessProfiles { get; set; } = new();

    /// <summary>
    /// Selected filament profile names to import.
    /// </summary>
    public List<string> SelectedFilamentProfiles { get; set; } = new();
}

/// <summary>
/// Result of selective profile import operation.
/// </summary>
public class SelectiveProfileImportResultDto
{
    /// <summary>
    /// The printer model ID profiles were imported for.
    /// </summary>
    public Guid PrinterModelId { get; set; }

    /// <summary>
    /// Number of machine profiles imported.
    /// </summary>
    public int MachineProfilesImported { get; set; }

    /// <summary>
    /// Number of process profiles imported.
    /// </summary>
    public int ProcessProfilesImported { get; set; }

    /// <summary>
    /// Number of filament profiles imported.
    /// </summary>
    public int FilamentProfilesImported { get; set; }

    /// <summary>
    /// Total profiles imported across all types.
    /// </summary>
    public int TotalImported => MachineProfilesImported + ProcessProfilesImported + FilamentProfilesImported;

    /// <summary>
    /// Number of profiles skipped (duplicates or errors).
    /// </summary>
    public int Skipped { get; set; }

    /// <summary>
    /// Optional error message if import partially failed.
    /// </summary>
    public string? Error { get; set; }
}

/// <summary>
/// Result of bulk profile deletion operation.
/// </summary>
public class BulkDeleteResultDto
{
    /// <summary>
    /// Number of machine profiles deleted.
    /// </summary>
    public int MachineProfilesDeleted { get; set; }

    /// <summary>
    /// Number of process profiles deleted.
    /// </summary>
    public int ProcessProfilesDeleted { get; set; }

    /// <summary>
    /// Number of filament profiles deleted.
    /// </summary>
    public int FilamentProfilesDeleted { get; set; }

    /// <summary>
    /// Total profiles deleted across all types.
    /// </summary>
    public int TotalDeleted => MachineProfilesDeleted + ProcessProfilesDeleted + FilamentProfilesDeleted;

    /// <summary>
    /// Number of profile IDs that were not found.
    /// </summary>
    public int NotFound { get; set; }
}

#pragma warning restore SA1402
