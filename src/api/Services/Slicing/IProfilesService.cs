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
        Task<(ProcessProfileExtendedDto dto, bool created)> ImportProfileAsync(ImportProcessProfileDto req, CancellationToken ct);

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
    }
}
