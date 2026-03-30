namespace Farm.Infrastructure.Domain;

/// <summary>
/// Tracks the version of catalog seed data applied to this instance.
/// Used by the catalog update system to detect when newer seed data is available.
/// </summary>
public class CatalogVersion
{
    /// <summary>
    /// Unique identifier for this catalog version record.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Semantic version string from the catalog manifest (e.g., "2026.03.29.1").
    /// </summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>
    /// SHA256 hash of the entire catalog-manifest.yaml file at the time of application.
    /// </summary>
    public string? ManifestHash { get; set; }

    /// <summary>
    /// When this catalog version was applied to the database.
    /// </summary>
    public DateTime AppliedAt { get; set; }

    /// <summary>
    /// Origin of this catalog update: "local" (bundled), "github" (remote), or "manual" (admin-triggered).
    /// </summary>
    public string? Source { get; set; }
}
