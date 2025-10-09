using Farm.Web.Shared;

namespace Farm.Web.Api.Services.Interfaces;

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
    /// Gets a list of all filament spools from the configured Spoolman server.
    /// </summary>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>A task containing a read-only list of all spools with their current status and information</returns>
    Task<IReadOnlyList<SpoolmanSpoolDto>> ListSpoolsAsync(CancellationToken ct);

    /// <summary>
    /// Gets detailed information about a specific filament spool by its ID.
    /// </summary>
    /// <param name="spoolId">The unique identifier of the spool to retrieve</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>A task containing the spool information, or null if the spool doesn't exist</returns>
    Task<SpoolmanSpoolDto?> GetSpoolByIdAsync(int spoolId, CancellationToken ct);

    /// <summary>
    /// Scans the provided network ranges for Spoolman instances.
    /// </summary>
    /// <param name="networkRanges">Enumerable of CIDR or IP ranges to scan</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Enumerable of discovery results</returns>
    Task<IEnumerable<SpoolmanDiscoveryResult>> ScanNetworkForSpoolmanAsync(IEnumerable<string> networkRanges, CancellationToken ct = default);
}
