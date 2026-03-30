namespace Farm.Infrastructure.Dtos.DataManagement;

/// <summary>
/// Represents the catalog-manifest.yaml structure used for update detection.
/// </summary>
public class CatalogManifest
{
    /// <summary>
    /// Catalog version string (e.g., "2026.03.29.1").
    /// </summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>
    /// Date the catalog was last updated (ISO 8601 date).
    /// </summary>
    public string? LastUpdated { get; set; }

    /// <summary>
    /// Map of logical file names to their path and SHA256 hash.
    /// </summary>
    public Dictionary<string, CatalogFileEntry> Files { get; set; } = new();
}

/// <summary>
/// A single file entry in the catalog manifest with its path and content hash.
/// </summary>
public class CatalogFileEntry
{
    /// <summary>
    /// Relative path from the seed data root (e.g., "components/hotends.yaml").
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// SHA256 hash of the file contents.
    /// </summary>
    public string Sha256 { get; set; } = string.Empty;
}

/// <summary>
/// Result of checking whether a catalog update is available.
/// </summary>
public class CatalogUpdateCheckResult
{
    /// <summary>
    /// Whether a newer catalog version is available remotely.
    /// </summary>
    public bool UpdateAvailable { get; set; }

    /// <summary>
    /// Currently applied catalog version, or null if never applied.
    /// </summary>
    public string? CurrentVersion { get; set; }

    /// <summary>
    /// Version available from the remote source.
    /// </summary>
    public string? AvailableVersion { get; set; }

    /// <summary>
    /// List of files that differ between local and remote catalogs.
    /// </summary>
    public List<CatalogFileChange> ChangedFiles { get; set; } = [];

    /// <summary>
    /// Timestamp when this check was performed.
    /// </summary>
    public DateTime CheckedAt { get; set; }

    /// <summary>
    /// Error message if the check failed (e.g., network unreachable).
    /// </summary>
    public string? Error { get; set; }
}

/// <summary>
/// Describes a single changed file between local and remote catalog versions.
/// </summary>
public class CatalogFileChange
{
    /// <summary>
    /// Logical file name (e.g., "manufacturers", "printer-models").
    /// </summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable category (e.g., "Manufacturers", "Printer Models").
    /// </summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>
    /// Type of change: "Modified", "New", or "Removed".
    /// </summary>
    public string ChangeType { get; set; } = string.Empty;
}

/// <summary>
/// Result of applying a catalog update.
/// </summary>
public class CatalogUpdateApplyResult
{
    /// <summary>
    /// Whether the update was applied successfully.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Catalog version before the update.
    /// </summary>
    public string? PreviousVersion { get; set; }

    /// <summary>
    /// Catalog version after the update.
    /// </summary>
    public string? AppliedVersion { get; set; }

    /// <summary>
    /// Categories that were updated (e.g., ["Manufacturers", "Printer Models"]).
    /// </summary>
    public List<string> UpdatedCategories { get; set; } = [];

    /// <summary>
    /// Timestamp when the update was applied.
    /// </summary>
    public DateTime AppliedAt { get; set; }

    /// <summary>
    /// Error message if the update failed.
    /// </summary>
    public string? Error { get; set; }
}

/// <summary>
/// Summary of the currently applied catalog version for the admin UI.
/// </summary>
public class CatalogVersionDto
{
    /// <summary>
    /// Currently applied catalog version string.
    /// </summary>
    public string? Version { get; set; }

    /// <summary>
    /// When this catalog version was applied.
    /// </summary>
    public DateTime? AppliedAt { get; set; }

    /// <summary>
    /// Source of the last update: "local", "github", or "manual".
    /// </summary>
    public string? Source { get; set; }
}
