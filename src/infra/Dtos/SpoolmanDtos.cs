using System.Text.Json.Serialization;

namespace Farm.Infrastructure;

// ===========================================================================
// Spoolman Integration DTOs
// ===========================================================================
// DTOs for interacting with Spoolman filament management system.
// ===========================================================================

/// <summary>
/// Configuration settings for integrating with an external Spoolman instance.
/// </summary>
/// <param name="BaseUrl">Base URL of the Spoolman server (e.g., "http://spoolman:7912").</param>
public record SpoolmanConfigDto(string? BaseUrl)
{
    /// <summary>
    /// Parsed URI from BaseUrl, or null if invalid/empty.
    /// </summary>
    [JsonIgnore]
    public Uri? BaseUri => string.IsNullOrWhiteSpace(BaseUrl)
        ? null
        : (Uri.TryCreate(BaseUrl, UriKind.Absolute, out Uri? u) ? u : null);
}

/// <summary>
/// Result of scanning a network address for a Spoolman instance.
/// </summary>
/// <param name="Url">The URL that was scanned.</param>
/// <param name="IsAvailable">Whether a Spoolman instance was found at this URL.</param>
/// <param name="Error">Error message if discovery failed.</param>
/// <param name="Version">Spoolman version if available.</param>
/// <param name="ResponseTime">Time taken to probe the endpoint.</param>
public record SpoolmanDiscoveryResult(
    string Url,
    bool IsAvailable,
    string? Error = null,
    string? Version = null,
    TimeSpan? ResponseTime = null);

/// <summary>
/// Result of importing filament types from Spoolman.
/// </summary>
/// <param name="ImportedCount">Number of filament types successfully imported.</param>
/// <param name="SkippedCount">Number of filament types skipped (already exist).</param>
/// <param name="TotalSpoolmanMaterials">Total materials available in Spoolman.</param>
/// <param name="ImportedNames">Names of the imported filament types.</param>
public record SpoolmanFilamentImportResult(
    int ImportedCount,
    int SkippedCount,
    int TotalSpoolmanMaterials,
    string[] ImportedNames);

/// <summary>
/// Represents a material type definition from Spoolman's /api/v1/material endpoint.
/// </summary>
/// <param name="Id">Spoolman material ID.</param>
/// <param name="Name">Material name (e.g., "PLA", "PETG").</param>
/// <param name="Density">Material density in g/cm³.</param>
/// <param name="ColorHex">Default color hex code.</param>
public record SpoolmanMaterialDto(
    int Id,
    string Name,
    double? Density = null,
    string? ColorHex = null);

/// <summary>
/// Result of probing a Spoolman endpoint (used by the setup flow and health probes).
/// </summary>
/// <param name="Success">Whether the probe was successful.</param>
/// <param name="NormalizedUrl">Normalized URL of the Spoolman instance.</param>
/// <param name="EndpointTried">The specific endpoint that was probed.</param>
/// <param name="StatusCode">HTTP status code from the probe.</param>
/// <param name="Version">Spoolman version if available.</param>
/// <param name="Message">Status or error message.</param>
/// <param name="ErrorCategory">Category of error if probe failed.</param>
public record SpoolmanProbeResult(
    bool Success,
    string? NormalizedUrl = null,
    string? EndpointTried = null,
    int? StatusCode = null,
    string? Version = null,
    string? Message = null,
    string? ErrorCategory = null);
