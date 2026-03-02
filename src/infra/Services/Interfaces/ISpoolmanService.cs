using Farm.Infrastructure;

namespace Farm.Infrastructure.Services.Interfaces;

/// <summary>
/// Interface for Spoolman service providing filament spool management functionality.
/// Handles configuration management and spool data retrieval from the Spoolman filament management system.
/// </summary>
public interface ISpoolmanService
{
    /// <summary>
    /// Gets all material types directly from Spoolman's /api/v1/material endpoint.
    /// </summary>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>A task containing a read-only list of all material types</returns>
    Task<IReadOnlyList<SpoolmanMaterialDto>> ListMaterialsAsync(CancellationToken ct);

    /// <summary>
    /// Gets the current Spoolman configuration including the base URL.
    /// </summary>
    /// <returns>The current Spoolman configuration, or null if not configured</returns>
    SpoolmanConfigDto? GetConfig();

    /// <summary>
    /// Sets the Spoolman configuration with the base URL for the Spoolman server.
    /// </summary>
    /// <param name="config">Configuration object containing the Spoolman server base URL</param>
    void SetConfig(SpoolmanConfigDto config);

    /// <summary>
    /// Clears the current Spoolman configuration (removes stored base URL).
    /// </summary>
    void ClearConfig();

    /// <summary>
    /// Gets a paginated, filtered, and sorted list of filament spools from the configured Spoolman server.
    /// </summary>
    /// <param name="queryParams">Query parameters for pagination, filtering, and sorting.</param>
    /// <param name="ct">Cancellation token to cancel the operation.</param>
    /// <returns>A paginated result containing matching spools and total count.</returns>
    Task<SpoolmanPagedResult<SpoolmanSpoolDto>> ListSpoolsAsync(SpoolmanSpoolQueryParams queryParams, CancellationToken ct);

    /// <summary>
    /// Gets a list of all filament types (product definitions) from the configured Spoolman server.
    /// Filaments represent the product/class (e.g., "PolyTerra PLA Charcoal Black"),
    /// while spools represent physical instances of a filament.
    /// </summary>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>A task containing a read-only list of all filament types</returns>
    Task<IReadOnlyList<SpoolmanFilamentDto>> ListFilamentsAsync(CancellationToken ct);

    /// <summary>
    /// Gets a specific filament type by its ID from the configured Spoolman server.
    /// </summary>
    /// <param name="filamentId">The unique identifier of the filament type to retrieve</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>A task containing the filament information, or null if not found</returns>
    Task<SpoolmanFilamentDto?> GetFilamentByIdAsync(int filamentId, CancellationToken ct);

    /// <summary>
    /// Gets detailed information about a specific filament spool by its ID.
    /// </summary>
    /// <param name="spoolId">The unique identifier of the spool to retrieve</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>A task containing the spool information, or null if the spool doesn't exist</returns>
    Task<SpoolmanSpoolDto?> GetSpoolByIdAsync(int spoolId, CancellationToken ct);

    /// <summary>
    /// Creates a new spool in Spoolman. Returns the created spool with its ID.
    /// </summary>
    Task<SpoolmanSpoolDto> CreateSpoolInSpoolmanAsync(SpoolmanSpoolRequest request, CancellationToken ct);

    /// <summary>
    /// Updates an existing spool in Spoolman by its ID.
    /// </summary>
    Task<SpoolmanSpoolDto> UpdateSpoolInSpoolmanAsync(int spoolId, SpoolmanSpoolRequest request, CancellationToken ct);

    /// <summary>
    /// Deletes a spool from Spoolman by its ID.
    /// </summary>
    Task DeleteSpoolFromSpoolmanAsync(int spoolId, CancellationToken ct);

    /// <summary>
    /// Bulk-updates multiple spools in Spoolman. Only non-null fields in the request are applied.
    /// </summary>
    Task<SpoolmanBulkUpdateResult> BulkUpdateSpoolsAsync(SpoolmanBulkUpdateSpoolsRequest request, CancellationToken ct);

    /// <summary>
    /// Bulk-deletes multiple spools from Spoolman. Returns success/error counts.
    /// </summary>
    Task<SpoolmanBulkUpdateResult> BulkDeleteSpoolsAsync(int[] spoolIds, CancellationToken ct);

    /// <summary>
    /// Scans the provided network ranges for Spoolman instances.
    /// </summary>
    /// <param name="networkRanges">Enumerable of CIDR or IP ranges to scan</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Enumerable of discovery results</returns>
    Task<IEnumerable<SpoolmanDiscoveryResult>> ScanNetworkForSpoolmanAsync(IEnumerable<string> networkRanges, CancellationToken ct = default);

    /// <summary>
    /// Probes a candidate Spoolman base URL for basic health/version endpoints without persisting configuration.
    /// Returns a SpoolmanProbeResult with normalized URL and success details.
    /// </summary>
    /// <param name="candidateBaseUrl">The base URL of the Spoolman instance to probe.</param>
    /// <param name="ct">The cancellation token.</param>
    Task<SpoolmanProbeResult> ProbeAsync(string candidateBaseUrl, CancellationToken ct);

    /// <summary>
    /// Performs a minimal health probe against the currently configured Spoolman instance.
    /// </summary>
    /// <param name="ct">The cancellation token.</param>
    Task<SpoolmanProbeResult> HealthProbeAsync(CancellationToken ct);

    /// <summary>
    /// Lists all vendors from the configured Spoolman server.
    /// </summary>
    Task<IReadOnlyList<SpoolmanVendorDto>> ListVendorsAsync(CancellationToken ct);

    /// <summary>
    /// Creates a new vendor in Spoolman. Returns the created vendor with its ID.
    /// </summary>
    Task<SpoolmanVendorDto> CreateVendorAsync(string name, string? externalId, CancellationToken ct);

    /// <summary>
    /// Creates a new filament in Spoolman. Returns the created filament with its ID.
    /// </summary>
    Task<SpoolmanFilamentDto> CreateFilamentInSpoolmanAsync(SpoolmanCreateFilamentRequest request, CancellationToken ct);

    /// <summary>
    /// Updates an existing filament in Spoolman by its ID.
    /// </summary>
    Task<SpoolmanFilamentDto> UpdateFilamentInSpoolmanAsync(int filamentId, SpoolmanCreateFilamentRequest request, CancellationToken ct);

    /// <summary>
    /// Bulk-updates multiple filaments in Spoolman. Only non-null fields in the request are applied.
    /// </summary>
    Task<SpoolmanBulkUpdateResult> BulkUpdateFilamentsAsync(SpoolmanBulkUpdateFilamentsRequest request, CancellationToken ct);

    /// <summary>
    /// Deletes a filament from Spoolman by its ID.
    /// </summary>
    Task DeleteFilamentFromSpoolmanAsync(int filamentId, CancellationToken ct);

    /// <summary>
    /// Bulk-deletes multiple filaments from Spoolman. Returns success/error counts.
    /// </summary>
    Task<SpoolmanBulkUpdateResult> BulkDeleteFilamentsAsync(int[] filamentIds, CancellationToken ct);

    /// <summary>
    /// Gets all filaments from Spoolman's external (SpoolmanDB) endpoint: /api/v1/external/filament.
    /// Spoolman periodically syncs this data from the SpoolmanDB community database.
    /// </summary>
    Task<IReadOnlyList<SpoolmanDbFilamentEntry>> GetExternalFilamentsAsync(CancellationToken ct);

    /// <summary>
    /// Gets all materials from Spoolman's external (SpoolmanDB) endpoint: /api/v1/external/material.
    /// Returns material definitions with density, extruder temp, and bed temp.
    /// </summary>
    Task<IReadOnlyList<SpoolmanDbMaterialEntry>> GetExternalMaterialsAsync(CancellationToken ct);

    /// <summary>
    /// Records filament consumption on a Spoolman spool by incrementing its used_weight.
    /// Calls PATCH /api/v1/spool/{spoolId} with the new total used_weight.
    /// </summary>
    /// <param name="spoolId">The Spoolman spool ID to update.</param>
    /// <param name="usedWeightGrams">The amount of filament consumed in this print (grams).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the update succeeded, false otherwise.</returns>
    Task<bool> ConsumeFilamentAsync(int spoolId, double usedWeightGrams, CancellationToken ct);
}
