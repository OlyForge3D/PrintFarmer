using Farm.Slicer.Module.Dtos;

namespace Farm.Slicer.Module.Services;

/// <summary>
/// Service contract for managing slicer profiles (process, machine, and filament profiles).
/// Provides orchestration logic for profile import/export, synchronization with external workers,
/// and administrative operations on slicer profile configurations.
/// </summary>
public interface IProfilesService
{
    /// <summary>Imports a process profile from raw slicer configuration JSON with deduplication.</summary>
    /// <param name="req">Import request containing raw profile JSON, slicer type, and optional metadata overrides.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Tuple of (profile DTO, bool indicating if profile was newly created).</returns>
    Task<(ProcessProfileExtendedDto Dto, bool Created)> ImportProfileAsync(ImportProcessProfileDto req, CancellationToken ct);

    /// <summary>Exports a profile to a storable format including raw JSON and metadata.</summary>
    /// <param name="id">ID of the profile to export.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<ProcessProfileExportDto?> ExportProfileAsync(Guid id, CancellationToken ct);

    /// <summary>Sets a profile as the default for its slicer type and scope.</summary>
    /// <param name="id">ID of the profile to set as default.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<bool> SetDefaultProfileAsync(Guid id, CancellationToken ct);

    /// <summary>Lists all profiles organized by type with basic properties.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task<ExtendedProfilesResponseDto> ListExtendedAsync(CancellationToken ct);

    /// <summary>Lists profiles organized in a hierarchical structure by manufacturer and machine model.</summary>
    /// <param name="manufacturer">Optional manufacturer name filter.</param>
    /// <param name="machineProfileId">Optional machine profile ID filter.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<HierarchicalProfilesResponseDto> ListHierarchyAsync(string? manufacturer, Guid? machineProfileId, CancellationToken ct);

    /// <summary>Lists system OrcaSlicer profiles available for import.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<SlicerProfileListItemDto>> ListSystemOrcaProfilesAsync(CancellationToken ct);

    /// <summary>Seeds the database with system OrcaSlicer profiles from the worker.</summary>
    /// <param name="httpClient">HTTP client for worker communication.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<object> SeedSystemProfilesFromWorkerAsync(HttpClient httpClient, CancellationToken ct);

    /// <summary>Force-reseeds the database, deleting old system profiles first.</summary>
    /// <param name="httpClient">HTTP client for worker communication.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<object> ForceReseedSystemProfilesFromWorkerAsync(HttpClient httpClient, CancellationToken ct);

    /// <summary>Deletes all system profiles (IsSystem=true) from the database.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task<object> DeleteAllSystemProfilesAsync(CancellationToken ct);

    /// <summary>Fetches available OrcaSlicer profiles directly from the worker service.</summary>
    /// <param name="httpClient">HTTP client for worker communication.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<ProcessProfileDto>> GetAvailableProfilesFromWorkerAsync(HttpClient httpClient, CancellationToken ct);

    /// <summary>Fetches the full profile hierarchy from OrcaSlicer worker.</summary>
    /// <param name="httpClient">HTTP client for worker communication.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<AllProfilesResponseDto?> GetWorkerProfilesHierarchyAsync(HttpClient httpClient, CancellationToken ct);

    /// <summary>Fetches machine profiles for a specific manufacturer and model from the worker.</summary>
    /// <param name="httpClient">HTTP client for worker communication.</param>
    /// <param name="manufacturer">Manufacturer name.</param>
    /// <param name="model">Model name.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<MachineProfileDto>> GetMachineProfilesForModelAsync(HttpClient httpClient, string manufacturer, string model, CancellationToken ct);

    /// <summary>Gets names of profiles already imported for a specific printer model.</summary>
    /// <param name="printerModelId">The printer model ID.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<ImportedProfileNamesDto> GetImportedProfileNamesForModelAsync(Guid printerModelId, CancellationToken ct);

    /// <summary>Fetches machine profiles by OrcaSlicer alias from the worker.</summary>
    /// <param name="httpClient">HTTP client for worker communication.</param>
    /// <param name="printerModel">The OrcaSlicer alias (printer_model value).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<MachineProfileDto>> GetMachineProfilesByAliasAsync(HttpClient httpClient, string printerModel, CancellationToken ct);

    /// <summary>Fetches process profiles compatible with specific machines from the worker.</summary>
    /// <param name="httpClient">HTTP client for worker communication.</param>
    /// <param name="machineNames">Machine profile names to find compatible processes for.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<ProcessProfileDto>> GetProcessProfilesForMachinesAsync(HttpClient httpClient, IEnumerable<string> machineNames, CancellationToken ct);

    /// <summary>Fetches filament profiles compatible with specific machines from the worker.</summary>
    /// <param name="httpClient">HTTP client for worker communication.</param>
    /// <param name="machineNames">Machine profile names to find compatible filaments for.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<FilamentProfileDto>> GetFilamentProfilesForMachinesAsync(HttpClient httpClient, IEnumerable<string> machineNames, CancellationToken ct);

    /// <summary>Fetches template filament profiles from the OrcaFilamentLibrary.</summary>
    /// <param name="httpClient">HTTP client for worker communication.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<FilamentProfileDto>> GetFilamentTemplatesAsync(HttpClient httpClient, CancellationToken ct);

    /// <summary>Gets system OrcaSlicer profiles available for import to a specific printer.</summary>
    /// <param name="printerId">ID of the registered printer.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<SlicerProfileListItemDto>> GetAvailableProfilesForPrinterAsync(Guid printerId, CancellationToken ct);

    /// <summary>Bulk imports system OrcaSlicer profiles by ID for a specific printer.</summary>
    /// <param name="printerId">ID of the registered printer.</param>
    /// <param name="request">Request containing list of system profile IDs to import.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<BulkProfileImportResultDto> BulkImportProfilesForPrinterAsync(Guid printerId, BulkProfileImportRequest request, CancellationToken ct);

    /// <summary>Clones process profiles from a template machine to a custom printer instance.</summary>
    /// <param name="request">Request containing source machine profile ID and target printer ID.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<CloneProfilesResponseDto> CloneFromTemplateAsync(CloneProfilesRequestDto request, CancellationToken ct);

    /// <summary>Bulk imports profiles directly from the OrcaSlicer worker.</summary>
    /// <param name="printerId">ID of the registered printer.</param>
    /// <param name="request">Request containing profiles from worker and import options.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<BulkImportFromWorkerResultDto> BulkImportFromWorkerAsync(Guid printerId, BulkImportFromWorkerRequest request, CancellationToken ct);

    /// <summary>Imports selected profiles from the OrcaSlicer worker for a specific printer model.</summary>
    /// <param name="printerModelId">The catalog PrinterModel ID.</param>
    /// <param name="request">Request containing selected profile names for each type.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<SelectiveProfileImportResultDto> ImportSelectedProfilesForModelAsync(
        Guid printerModelId,
        SelectiveProfileImportRequest request,
        CancellationToken ct);

    /// <summary>Creates a new process profile.</summary>
    /// <param name="req">Create request with profile properties.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<ProcessProfileResponseDto> CreateProfileAsync(CreateProcessProfileDto req, CancellationToken ct);

    /// <summary>Retrieves a single profile by ID.</summary>
    /// <param name="id">The profile identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<ProcessProfileResponseDto?> GetProfileAsync(Guid id, CancellationToken ct);

    /// <summary>Retrieves all profiles with basic properties.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<SlicerProfileDto>> GetProfilesAsync(CancellationToken ct);

    /// <summary>Deletes a profile by ID.</summary>
    /// <param name="id">The profile identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task DeleteProfileAsync(Guid id, CancellationToken ct);

    /// <summary>Deletes multiple profiles by ID.</summary>
    /// <param name="profileIds">Collection of profile IDs to delete.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<BulkDeleteResultDto> BulkDeleteProfilesAsync(IEnumerable<Guid> profileIds, CancellationToken ct);

    /// <summary>Clones a single profile to create a user-owned custom copy.</summary>
    /// <param name="request">Clone request with source profile ID, type, and optional custom name.</param>
    /// <param name="userId">ID of the user creating the clone.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<CloneSingleProfileResponseDto> CloneSingleProfileAsync(CloneSingleProfileRequestDto request, Guid userId, CancellationToken ct);

    /// <summary>Uploads a custom profile from raw JSON content.</summary>
    /// <param name="request">Upload request with raw JSON, profile type, and optional name.</param>
    /// <param name="userId">ID of the user uploading the profile.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<CustomProfileDto> UploadCustomProfileAsync(UploadProfileRequestDto request, Guid userId, CancellationToken ct);

    /// <summary>Lists all custom profiles owned by a specific user.</summary>
    /// <param name="userId">ID of the user.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<CustomProfilesListResponseDto> ListCustomProfilesAsync(Guid userId, CancellationToken ct);

    /// <summary>Updates a custom profile's properties.</summary>
    /// <param name="profileId">ID of the profile to update.</param>
    /// <param name="request">Update request with optional new name, rawJson, or description.</param>
    /// <param name="userId">ID of the user requesting the update.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<CustomProfileDto> UpdateCustomProfileAsync(Guid profileId, UpdateCustomProfileRequestDto request, Guid userId, CancellationToken ct);
}
