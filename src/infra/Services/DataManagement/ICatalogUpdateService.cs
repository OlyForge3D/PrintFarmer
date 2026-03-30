using Farm.Infrastructure.Dtos.DataManagement;

namespace Farm.Infrastructure.Services.DataManagement;

/// <summary>
/// Service for detecting and applying catalog seed data updates from a remote source.
/// </summary>
public interface ICatalogUpdateService
{
    /// <summary>
    /// Check whether a newer catalog version is available from the remote source.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Check result describing available updates and changed files.</returns>
    Task<CatalogUpdateCheckResult> CheckForUpdatesAsync(CancellationToken ct = default);

    /// <summary>
    /// Download and apply available catalog updates from the remote source.
    /// Fetches changed YAML files, re-seeds the database, and records the new version.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Apply result describing what was updated.</returns>
    Task<CatalogUpdateApplyResult> ApplyUpdatesAsync(CancellationToken ct = default);

    /// <summary>
    /// Get the currently applied catalog version, or null if no version has been recorded.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The current catalog version record, or null.</returns>
    Task<CatalogVersionDto?> GetCurrentVersionAsync(CancellationToken ct = default);
}
