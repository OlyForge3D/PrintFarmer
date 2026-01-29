using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Web.Api.DTOs;

namespace Farm.Web.Api.Services.Slicing
{
    /// <summary>
    /// Service contract for managing slicer profiles (process, machine, and filament profiles).
    /// Provides orchestration logic for profile import/export, synchronization with external workers,
    /// and administrative operations on slicer profile configurations across multiple slicer types.
    /// </summary>
    /// <remarks>
    /// This service manages:
    /// - Profile CRUD operations (create, read, update, delete)
    /// - Import/export with hash-based deduplication detection
    /// - Hierarchical profile listing with optional filtering by manufacturer and machine profile
    /// - System profile management (seeding and reseeding from OrcaSlicer worker)
    /// - Profile synchronization for registered printers
    /// - Bulk import operations from worker or database
    /// - Profile cloning from template machines to custom printer instances
    /// - Default profile configuration per slicer type
    ///
    /// All profile operations maintain data integrity through validation, hash checking,
    /// and proper error handling. External worker communication is coordinated through
    /// this service layer, abstracting HTTP details from controllers.
    /// </remarks>
    public interface IProfilesService
    {
        /// <summary>
        /// Imports a process profile from raw slicer configuration JSON with deduplication.
        /// </summary>
        /// <param name="req">Import request containing raw profile JSON, slicer type, and optional metadata overrides</param>
        /// <param name="ct">Cancellation token for async operation</param>
        /// <returns>Tuple of (ProcessProfileExtendedDto with full profile details, bool indicating if profile was newly created)</returns>
        /// <remarks>
        /// Performs hash-based deduplication: if a profile with the same hash already exists,
        /// returns 200 OK (not created). New profiles return 201 Created.
        /// Parses metadata from raw JSON to extract properties like layer height, infill, and material.
        /// Throws ArgumentException if rawJson or slicerType are invalid.
        /// </remarks>
        Task<(ProcessProfileExtendedDto Dto, bool Created)> ImportProfileAsync(ImportProcessProfileDto req, CancellationToken ct);

        /// <summary>
        /// Exports a profile to a storable format including raw JSON and metadata.
        /// </summary>
        /// <param name="id">ID of the profile to export</param>
        /// <param name="ct">Cancellation token for async operation</param>
        /// <returns>ProcessProfileExportDto with raw JSON and metadata, or null if profile not found</returns>
        Task<ProcessProfileExportDto?> ExportProfileAsync(Guid id, CancellationToken ct);

        /// <summary>
        /// Sets a profile as the default for its slicer type and scope.
        /// </summary>
        /// <param name="id">ID of the profile to set as default</param>
        /// <param name="ct">Cancellation token for async operation</param>
        /// <returns>True if profile was found and updated, false if profile not found</returns>
        Task<bool> SetDefaultProfileAsync(Guid id, CancellationToken ct);

        /// <summary>
        /// Lists all profiles organized by type (process, filament, machine) with basic properties.
        /// </summary>
        /// <param name="ct">Cancellation token for async operation</param>
        /// <returns>ExtendedProfilesResponseDto containing separate lists for each profile type</returns>
        /// <remarks>
        /// Returns all system and user profiles. Useful for admin UIs showing complete profile inventory.
        /// </remarks>
        Task<ExtendedProfilesResponseDto> ListExtendedAsync(CancellationToken ct);

        /// <summary>
        /// Lists profiles organized in a hierarchical structure by manufacturer and machine model.
        /// </summary>
        /// <param name="manufacturer">Optional: filter results to a specific manufacturer name</param>
        /// <param name="machineProfileId">Optional: filter to profiles compatible with a specific machine profile</param>
        /// <param name="ct">Cancellation token for async operation</param>
        /// <returns>HierarchicalProfilesResponseDto with profiles grouped by manufacturer and model</returns>
        /// <remarks>
        /// Used by frontend admin UI to display profiles in a nested tree structure.
        /// Both filters are optional; if neither is provided, returns full hierarchy.
        /// </remarks>
        Task<HierarchicalProfilesResponseDto> ListHierarchyAsync(string? manufacturer, Guid? machineProfileId, CancellationToken ct);

        /// <summary>
        /// Lists system OrcaSlicer profiles available for import to a printer.
        /// </summary>
        /// <param name="ct">Cancellation token for async operation</param>
        /// <returns>Read-only list of system OrcaSlicer profile items with basic properties</returns>
        Task<IReadOnlyList<SlicerProfileListItemDto>> ListSystemOrcaProfilesAsync(CancellationToken ct);

        /// <summary>
        /// Seeds the database with system OrcaSlicer profiles from the worker.
        /// Only imports new profiles; does not delete existing ones.
        /// </summary>
        /// <param name="httpClient">HTTP client for communication with OrcaSlicer worker</param>
        /// <param name="ct">Cancellation token for async operation</param>
        /// <returns>Object containing operation summary (imported count, skipped count, etc.)</returns>
        /// <remarks>
        /// This is an incremental operation. Existing profiles by hash are skipped.
        /// Throws HttpRequestException if worker is unavailable.
        /// </remarks>
        Task<object> SeedSystemProfilesFromWorkerAsync(HttpClient httpClient, CancellationToken ct);

        /// <summary>
        /// Force-reseeds the database, deleting old system profiles and reimporting all profiles from the worker.
        /// </summary>
        /// <param name="httpClient">HTTP client for communication with OrcaSlicer worker</param>
        /// <param name="ct">Cancellation token for async operation</param>
        /// <returns>Object containing operation summary (imported count, deleted count, etc.)</returns>
        /// <remarks>
        /// WARNING: Deletes all system profiles first, then reimports from worker.
        /// User-created profiles are not affected.
        /// Throws HttpRequestException if worker is unavailable.
        /// </remarks>
        Task<object> ForceReseedSystemProfilesFromWorkerAsync(HttpClient httpClient, CancellationToken ct);

        /// <summary>
        /// Deletes all system profiles (IsSystem=true) from the database.
        /// This is used for Phase 3 cleanup to remove duplicated system profiles.
        /// </summary>
        /// <param name="ct">Cancellation token for async operation</param>
        /// <returns>Object containing counts of deleted machine, process, and filament profiles</returns>
        /// <remarks>
        /// After this operation, system profiles should only be fetched from OrcaSlicer worker.
        /// Custom profiles (IsSystem=false) are preserved.
        /// </remarks>
        Task<object> DeleteAllSystemProfilesAsync(CancellationToken ct);

        /// <summary>
        /// Fetches available OrcaSlicer profiles directly from the worker service.
        /// </summary>
        /// <param name="httpClient">HTTP client for worker communication</param>
        /// <param name="ct">Cancellation token for async operation</param>
        /// <returns>List of process profiles available in the worker's local OrcaSlicer installation</returns>
        /// <remarks>
        /// Does not persist to database; returns worker's current profile list for selection UI.
        /// Throws HttpRequestException if worker unavailable, with StatusCode set for HTTP error forwarding.
        /// </remarks>
        Task<IReadOnlyList<ProcessProfileDto>> GetAvailableProfilesFromWorkerAsync(HttpClient httpClient, CancellationToken ct);

        /// <summary>
        /// Fetches the full profile hierarchy from OrcaSlicer worker organized by manufacturer and model.
        /// </summary>
        /// <param name="httpClient">HTTP client for worker communication</param>
        /// <param name="ct">Cancellation token for async operation</param>
        /// <returns>AllProfilesResponseDto with profiles organized by manufacturer hierarchy, or null if worker unavailable</returns>
        /// <remarks>
        /// Proxies the worker's /api/profiles endpoint which returns all available profiles.
        /// Does not persist to database; returns worker's current profile hierarchy for import UI.
        /// </remarks>
        Task<AllProfilesResponseDto?> GetWorkerProfilesHierarchyAsync(HttpClient httpClient, CancellationToken ct);

        /// <summary>
        /// Fetches machine profiles for a specific manufacturer and model from the OrcaSlicer worker.
        /// </summary>
        /// <param name="httpClient">HTTP client for worker communication</param>
        /// <param name="manufacturer">Manufacturer name (e.g., "Elegoo", "Prusa")</param>
        /// <param name="model">Model name (e.g., "Centauri Carbon", "CORE One")</param>
        /// <param name="ct">Cancellation token for async operation</param>
        /// <returns>List of machine profiles matching the manufacturer and model</returns>
        /// <remarks>
        /// Proxies the worker's /api/profiles/machine/{manufacturer}/{model} endpoint.
        /// Does not persist to database; returns worker's current machine profiles for import UI.
        /// </remarks>
        Task<IReadOnlyList<MachineProfileDto>> GetMachineProfilesForModelAsync(HttpClient httpClient, string manufacturer, string model, CancellationToken ct);

        /// <summary>
        /// Gets names of profiles already imported for a specific printer model.
        /// Used by the import wizard to show which profiles have already been imported.
        /// </summary>
        /// <param name="printerModelId">The printer model ID to check imported profiles for</param>
        /// <param name="ct">Cancellation token for async operation</param>
        /// <returns>DTO containing lists of imported machine, process, and filament profile names</returns>
        Task<ImportedProfileNamesDto> GetImportedProfileNamesForModelAsync(Guid printerModelId, CancellationToken ct);

        /// <summary>
        /// Fetches machine profiles by OrcaSlicer alias (printer_model) from the worker.
        /// The alias is the exact printer_model value (e.g., "Thinker X400", "RatRig V-Core 4 HYBRID 400").
        /// </summary>
        /// <param name="httpClient">HTTP client for worker communication</param>
        /// <param name="printerModel">The OrcaSlicer alias (printer_model value)</param>
        /// <param name="ct">Cancellation token for async operation</param>
        /// <returns>List of machine profiles matching the printer_model</returns>
        Task<IReadOnlyList<MachineProfileDto>> GetMachineProfilesByAliasAsync(HttpClient httpClient, string printerModel, CancellationToken ct);

        /// <summary>
        /// Fetches process profiles compatible with specific machines from the OrcaSlicer worker.
        /// </summary>
        /// <param name="httpClient">HTTP client for worker communication</param>
        /// <param name="machineNames">List of machine profile names to find compatible process profiles for</param>
        /// <param name="ct">Cancellation token for async operation</param>
        /// <returns>List of process profiles compatible with the specified machines</returns>
        Task<IReadOnlyList<ProcessProfileDto>> GetProcessProfilesForMachinesAsync(HttpClient httpClient, IEnumerable<string> machineNames, CancellationToken ct);

        /// <summary>
        /// Fetches filament profiles compatible with specific machines from the OrcaSlicer worker.
        /// </summary>
        /// <param name="httpClient">HTTP client for worker communication</param>
        /// <param name="machineNames">List of machine profile names to find compatible filament profiles for</param>
        /// <param name="ct">Cancellation token for async operation</param>
        /// <returns>List of filament profiles compatible with the specified machines</returns>
        Task<IReadOnlyList<FilamentProfileDto>> GetFilamentProfilesForMachinesAsync(HttpClient httpClient, IEnumerable<string> machineNames, CancellationToken ct);

        /// <summary>
        /// Fetches template filament profiles from the OrcaFilamentLibrary (universal profiles).
        /// </summary>
        /// <param name="httpClient">HTTP client for worker communication</param>
        /// <param name="ct">Cancellation token for async operation</param>
        /// <returns>List of universal filament profiles from OrcaFilamentLibrary</returns>
        Task<IReadOnlyList<FilamentProfileDto>> GetFilamentTemplatesAsync(HttpClient httpClient, CancellationToken ct);

        /// <summary>
        /// Gets system OrcaSlicer profiles available for import to a specific registered printer.
        /// </summary>
        /// <param name="printerId">ID of the registered printer</param>
        /// <param name="ct">Cancellation token for async operation</param>
        /// <returns>List of system OrcaSlicer profile items compatible with the printer</returns>
        /// <remarks>
        /// Validates that the printer exists. Returns system profiles that can be imported for this printer.
        /// Throws KeyNotFoundException if printer not found.
        /// </remarks>
        Task<IReadOnlyList<SlicerProfileListItemDto>> GetAvailableProfilesForPrinterAsync(Guid printerId, CancellationToken ct);

        /// <summary>
        /// Bulk imports system OrcaSlicer profiles by ID for a specific registered printer.
        /// </summary>
        /// <param name="printerId">ID of the registered printer</param>
        /// <param name="request">Request containing list of system profile IDs to import and options</param>
        /// <param name="ct">Cancellation token for async operation</param>
        /// <returns>BulkProfileImportResultDto with counts of imported, duplicated, and requested profiles</returns>
        /// <remarks>
        /// Creates user-owned copies of system profiles. Duplicates (by hash) are skipped.
        /// Throws KeyNotFoundException if printer not found.
        /// Throws ArgumentException if request is invalid or no profiles found for import.
        /// </remarks>
        Task<BulkProfileImportResultDto> BulkImportProfilesForPrinterAsync(Guid printerId, BulkProfileImportRequest request, CancellationToken ct);

        /// <summary>
        /// Clones process profiles from a template machine to a custom printer instance.
        /// </summary>
        /// <param name="request">Request containing source machine profile ID and target printer ID</param>
        /// <param name="ct">Cancellation token for async operation</param>
        /// <returns>CloneProfilesResponseDto with counts of cloned profiles</returns>
        /// <remarks>
        /// Allows users to create custom printers (e.g., "Prusa CORE One L") using profiles
        /// from a template machine (e.g., "Prusa CORE One") as a starting point.
        /// Throws KeyNotFoundException if machine profile or printer not found.
        /// Throws ArgumentException if request is invalid.
        /// </remarks>
        Task<CloneProfilesResponseDto> CloneFromTemplateAsync(CloneProfilesRequestDto request, CancellationToken ct);

        /// <summary>
        /// Bulk imports profiles directly from the OrcaSlicer worker without pre-seeding to database.
        /// </summary>
        /// <param name="printerId">ID of the registered printer</param>
        /// <param name="request">Request containing profiles from worker and import options</param>
        /// <param name="ct">Cancellation token for async operation</param>
        /// <returns>BulkImportFromWorkerResultDto with counts of imported and duplicated profiles</returns>
        /// <remarks>
        /// Primary workflow: user fetches profiles from worker, selects which to import, then imports directly.
        /// Profiles are created as user-owned (IsSystem=false) in the database.
        /// Throws KeyNotFoundException if printer not found.
        /// Throws ArgumentException if request is invalid.
        /// </remarks>
        Task<BulkImportFromWorkerResultDto> BulkImportFromWorkerAsync(Guid printerId, BulkImportFromWorkerRequest request, CancellationToken ct);

        /// <summary>
        /// Imports selected profiles from the OrcaSlicer worker for a specific printer model.
        /// This is the primary workflow for on-demand profile import when a new printer model needs profiles.
        /// </summary>
        /// <param name="printerModelId">The catalog PrinterModel ID to associate profiles with</param>
        /// <param name="request">Request containing selected profile names for each type (machine, process, filament)</param>
        /// <param name="ct">Cancellation token for async operation</param>
        /// <returns>SelectiveProfileImportResultDto with counts of imported profiles by type</returns>
        /// <remarks>
        /// Called from the Profile Import Wizard after user selects specific profiles.
        /// Fetches only the selected profiles from worker and persists them as system profiles.
        /// </remarks>
        Task<SelectiveProfileImportResultDto> ImportSelectedProfilesForModelAsync(
            Guid printerModelId,
            SelectiveProfileImportRequest request,
            CancellationToken ct);

        /// <summary>
        /// Creates a new process profile with specified configuration.
        /// </summary>
        /// <param name="req">Create request with profile properties (name, slicer type, layer height, etc.)</param>
        /// <param name="ct">Cancellation token for async operation</param>
        /// <returns>ProcessProfileResponseDto with created profile details</returns>
        Task<ProcessProfileResponseDto> CreateProfileAsync(CreateProcessProfileDto req, CancellationToken ct);

        /// <summary>
        /// Retrieves a single profile by ID with full details.
        /// </summary>
        /// <param name="id">ID of the profile to retrieve</param>
        /// <param name="ct">Cancellation token for async operation</param>
        /// <returns>ProcessProfileResponseDto with profile details, or null if not found</returns>
        Task<ProcessProfileResponseDto?> GetProfileAsync(Guid id, CancellationToken ct);

        /// <summary>
        /// Retrieves all profiles with basic properties.
        /// </summary>
        /// <param name="ct">Cancellation token for async operation</param>
        /// <returns>Read-only list of all profiles in simplified DTO format</returns>
        Task<IReadOnlyList<SlicerProfileDto>> GetProfilesAsync(CancellationToken ct);

        /// <summary>
        /// Deletes a profile by ID.
        /// </summary>
        /// <param name="id">ID of the profile to delete</param>
        /// <param name="ct">Cancellation token for async operation</param>
        /// <remarks>
        /// Throws KeyNotFoundException if profile not found.
        /// </remarks>
        Task DeleteProfileAsync(Guid id, CancellationToken ct);

        /// <summary>
        /// Deletes multiple profiles by ID, supporting all profile types (machine, process, filament).
        /// </summary>
        /// <param name="profileIds">Collection of profile IDs to delete</param>
        /// <param name="ct">Cancellation token for async operation</param>
        /// <returns>BulkDeleteResultDto with counts of deleted profiles by type</returns>
        /// <remarks>
        /// Profiles are looked up in machine, process, and filament tables.
        /// Invalid or non-existent IDs are skipped (not treated as errors).
        /// Returns counts of successfully deleted profiles by type.
        /// </remarks>
        Task<BulkDeleteResultDto> BulkDeleteProfilesAsync(IEnumerable<Guid> profileIds, CancellationToken ct);

        /// <summary>
        /// Clones a single profile to create a user-owned custom copy.
        /// </summary>
        /// <param name="request">Clone request with source profile ID, type, and optional custom name</param>
        /// <param name="userId">ID of the user creating the clone</param>
        /// <param name="ct">Cancellation token for async operation</param>
        /// <returns>CloneSingleProfileResponseDto with details of the created profile</returns>
        /// <remarks>
        /// Creates a new profile with IsSystem=false and CreatedByUserId set to the current user.
        /// The cloned profile copies all settings from the source but gets a new ID.
        /// Throws KeyNotFoundException if source profile not found.
        /// Throws ArgumentException if profile type is invalid.
        /// </remarks>
        Task<CloneSingleProfileResponseDto> CloneSingleProfileAsync(CloneSingleProfileRequestDto request, Guid userId, CancellationToken ct);

        /// <summary>
        /// Uploads a custom profile from raw JSON content.
        /// </summary>
        /// <param name="request">Upload request with raw JSON, profile type, and optional name</param>
        /// <param name="userId">ID of the user uploading the profile</param>
        /// <param name="ct">Cancellation token for async operation</param>
        /// <returns>CustomProfileDto with details of the created profile</returns>
        /// <remarks>
        /// Creates a new profile with IsSystem=false and CreatedByUserId set to the current user.
        /// Parses the raw JSON to extract profile properties.
        /// Throws ArgumentException if rawJson or profileType is invalid.
        /// </remarks>
        Task<CustomProfileDto> UploadCustomProfileAsync(UploadProfileRequestDto request, Guid userId, CancellationToken ct);

        /// <summary>
        /// Lists all custom profiles owned by a specific user.
        /// </summary>
        /// <param name="userId">ID of the user to list profiles for</param>
        /// <param name="ct">Cancellation token for async operation</param>
        /// <returns>CustomProfilesListResponseDto with profiles and summary counts</returns>
        Task<CustomProfilesListResponseDto> ListCustomProfilesAsync(Guid userId, CancellationToken ct);

        /// <summary>
        /// Updates a custom profile's properties.
        /// </summary>
        /// <param name="profileId">ID of the profile to update</param>
        /// <param name="request">Update request with optional new name, rawJson, or description</param>
        /// <param name="userId">ID of the user requesting the update (for ownership validation)</param>
        /// <param name="ct">Cancellation token for async operation</param>
        /// <returns>CustomProfileDto with updated profile details</returns>
        /// <remarks>
        /// Only non-null fields in the request will be updated.
        /// Throws KeyNotFoundException if profile not found.
        /// Throws UnauthorizedAccessException if user doesn't own the profile.
        /// Throws InvalidOperationException if trying to update a system profile.
        /// </remarks>
        Task<CustomProfileDto> UpdateCustomProfileAsync(Guid profileId, UpdateCustomProfileRequestDto request, Guid userId, CancellationToken ct);
    }
}
