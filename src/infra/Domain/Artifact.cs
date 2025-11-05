using System;

namespace Farm.Infrastructure.Domain;

/// <summary>
/// Represents a persisted slicing output (G-code, preview image, log, etc.) stored locally on disk.
/// Metadata lives in the database; bytes live in the filesystem under a configured root.
/// </summary>
public class Artifact
{
    /// <summary>
    /// Primary identifier.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Associated slice job identifier (required for lifecycle correlation).
    /// </summary>
    public Guid JobId { get; set; }

    /// <summary>
    /// Worker producing this artifact (optional if legacy job or unknown source).
    /// </summary>
    public Guid? WorkerId { get; set; }

    /// <summary>
    /// Canonical kind of artifact: gcode | thumbnail | preview | log.
    /// </summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>
    /// Original filename provided by slicer/worker (sanitized before storage).
    /// </summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// Relative path from the configured artifact root to the stored file (no leading slash).
    /// </summary>
    public string RelativePath { get; set; } = string.Empty;

    /// <summary>
    /// Content type for downstream consumers (UI, downloads).
    /// </summary>
    public string ContentType { get; set; } = "application/octet-stream";

    /// <summary>
    /// Size of the file in bytes.
    /// </summary>
    public long SizeBytes { get; set; }

    /// <summary>
    /// SHA-256 hash (hex) of the artifact for integrity & future dedup.
    /// </summary>
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>
    /// UTC timestamp when the artifact was created/persisted.
    /// </summary>
    public DateTime CreatedAt { get; set; }
}
