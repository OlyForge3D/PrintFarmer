using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure;

public class StartGcodeHarvestDto
{
    /// <summary>
    /// Target printer to harvest from.
    /// </summary>
    public Guid PrinterId { get; set; }

    /// <summary>
    /// Include subdirectories below the printer's root G-code storage path (default: true).
    /// </summary>
    public bool IncludeSubdirectories { get; set; } = true;

    /// <summary>
    /// Maximum file size (bytes) to consider. Files larger than this are ignored. Default 100MB.
    /// </summary>
    public long? MaxFileSizeBytes { get; set; } = 100 * 1024 * 1024; // 100MB

    /// <summary>
    /// Harvest only files modified strictly after this timestamp (UTC recommended).
    /// </summary>
    public DateTime? ModifiedAfter { get; set; }

    /// <summary>
    /// Allowlist of file extensions (without the leading dot). Empty/null means all supported extensions.
    /// Example: ["gcode", "gco", "g"]
    /// </summary>
    public string[]? FileExtensions { get; set; }

    /// <summary>
    /// Minimum file size (bytes). Files smaller than this are ignored.
    /// </summary>
    public long? MinFileSizeBytes { get; set; }

    /// <summary>
    /// Behavior when a file already exists in the library: "skip" (default), "overwrite", or "rename".
    /// rename => auto-appends -copy / -copy2 etc. to create a distinct entry.
    /// </summary>
    public string? DuplicateHandling { get; set; }
}
