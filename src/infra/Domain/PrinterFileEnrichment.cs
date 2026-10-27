using Farm.Infrastructure.Services.Printers;

namespace Farm.Infrastructure.Domain;

/// <summary>Independent, durable outcomes for one source-content identity.</summary>
public sealed class PrinterFileEnrichment : IRevisionedEntity
{
    public Guid LocationId { get; set; }

    public PrinterFileLocation Location { get; set; } = null!;

    public long Revision { get; set; } = 1;

    public string SourceIdentity { get; set; } = string.Empty;

    public int ExtractorVersion { get; set; }

    public PrinterFileEnrichmentState MetadataState { get; set; }

    public PrinterFileEnrichmentState ThumbnailState { get; set; }

    public string? MetadataReason { get; set; }

    public string? ThumbnailReason { get; set; }

    public string? MetadataSource { get; set; }

    public DateTime? LastAttemptAtUtc { get; set; }

    public DateTime? AcquiredAtUtc { get; set; }

    public DateTime NextAttemptAtUtc { get; set; }

    public int ConsecutiveFailures { get; set; }

    public Guid? ArtifactId { get; set; }

    public PrinterFileThumbnailArtifact? Artifact { get; set; }
}

/// <summary>Immutable image bytes addressed only by an opaque generated key.</summary>
public sealed class PrinterFileThumbnailArtifact
{
    public Guid Id { get; set; }

    public string ContentSha256 { get; set; } = string.Empty;

    public string ContentType { get; set; } = string.Empty;

    public int Width { get; set; }

    public int Height { get; set; }

    public long SizeBytes { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime? RetiredAtUtc { get; set; }
}

/// <summary>
/// An attempt owns both its .pending and .image names until publication or successful deletion.
/// Expiration never removes its quota charge.
/// </summary>
public sealed class PrinterFileThumbnailReservation
{
    public Guid Id { get; set; }

    public long ReservedBytes { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime ExpiresAtUtc { get; set; }
}

/// <summary>A seeded singleton row whose database write lock serializes storage mutations.</summary>
public sealed class PrinterFileThumbnailStorageLock
{
    public int Id { get; set; }

    public long Revision { get; set; }
}
