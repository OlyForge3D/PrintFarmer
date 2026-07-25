using System;
using Farm.Infrastructure.Domain;

namespace Farm.Web.Api.Services.Gcode;

/// <summary>
/// Immutable lineage stamped onto a G-code file that was produced by promoting a slicer artifact.
/// </summary>
/// <remarks>
/// Every member is an identifier, hash, version or manifest. Storage paths, private URLs, worker
/// credentials and profile bodies are intentionally absent so the lineage is safe to return and log.
/// </remarks>
public sealed record GcodePromotionLineage
{
    /// <summary>Source slicer artifact identity.</summary>
    public required Guid SourceArtifactId { get; init; }

    /// <summary>Slice job that produced the source artifact.</summary>
    public required Guid SourceSliceJobId { get; init; }

    /// <summary>Worker that produced the source artifact, when known.</summary>
    public Guid? SourceWorkerId { get; init; }

    /// <summary>Calibration project owning the promotion.</summary>
    public Guid? CalibrationProjectId { get; init; }

    /// <summary>Calibration attempt owning the promotion.</summary>
    public Guid? CalibrationAttemptId { get; init; }

    /// <summary>Durable orchestration that requested the promotion.</summary>
    public Guid? CalibrationOrchestrationId { get; init; }

    /// <summary>Idempotency operation key of the promotion.</summary>
    public required string PromotionOperationId { get; init; }

    /// <summary>
    /// Owner-scoped identity of the promotion. Uniqueness is enforced on this value so two owners may
    /// reuse the same raw idempotency key; it is never returned to a caller.
    /// </summary>
    public required string PromotionOperationKey { get; init; }

    /// <summary>Correlation identifier carried from the canonical slice submission.</summary>
    public Guid? PromotionCorrelationId { get; init; }

    /// <summary>SHA-256 (hex) of the canonical calibration specification.</summary>
    public string? SpecificationSha256 { get; init; }

    /// <summary>SHA-256 (hex) of the stored model bytes the slice consumed.</summary>
    public string? SourceModelSha256 { get; init; }

    /// <summary>SHA-256 (hex) of the effective native machine profile.</summary>
    public string? MachineProfileSha256 { get; init; }

    /// <summary>SHA-256 (hex) of the effective native process profile.</summary>
    public string? ProcessProfileSha256 { get; init; }

    /// <summary>SHA-256 (hex) of the effective native filament profile.</summary>
    public string? FilamentProfileSha256 { get; init; }

    /// <summary>Canonical slicer engine name.</summary>
    public string? SlicerEngineName { get; init; }

    /// <summary>Slicer distribution the job was pinned to.</summary>
    public string? SlicerDistribution { get; init; }

    /// <summary>Pinned slicer version the job required.</summary>
    public string? PinnedSlicerVersion { get; init; }

    /// <summary>Pinned slicer container digest the job required.</summary>
    public string? SlicerContainerDigest { get; init; }

    /// <summary>Firmware family the promoted output targets.</summary>
    public string? FirmwareFamily { get; init; }

    /// <summary>G-code dialect of the promoted output.</summary>
    public string? GcodeDialect { get; init; }

    /// <summary>Generator that produced the promoted output.</summary>
    public string? GeneratorName { get; init; }

    /// <summary>Generator version that produced the promoted output.</summary>
    public string? GeneratorVersion { get; init; }

    /// <summary>Server-built calibration manifest describing the promoted output.</summary>
    public string? CalibrationManifestJson { get; init; }

    /// <summary>SHA-256 (hex) of the sibling calibration-manifest artifact, when the job produced one.</summary>
    public string? CalibrationManifestSha256 { get; init; }
}

/// <summary>
/// A server-side ingest of already-verified G-code bytes into the library.
/// </summary>
/// <remarks>
/// The stream is read exactly once and hashed while it is copied, so an ingest never buffers the whole
/// payload and never requires a client download/re-upload round trip.
/// </remarks>
public sealed record GcodeStreamIngestRequest
{
    /// <summary>Identity the created file must use, keeping interrupted ingests convergent.</summary>
    public required Guid FileId { get; init; }

    /// <summary>Readable source stream. The caller owns disposal.</summary>
    public required Stream Content { get; init; }

    /// <summary>Display file name; sanitized before use.</summary>
    public required string FileName { get; init; }

    /// <summary>SHA-256 (hex) the copied bytes must produce.</summary>
    public required string ExpectedSha256 { get; init; }

    /// <summary>Byte count the copied bytes must match.</summary>
    public required long ExpectedSizeBytes { get; init; }

    /// <summary>Virtual library directory that receives the file. Defaults to the root.</summary>
    public string? VirtualDirectory { get; init; }

    /// <summary>Library provenance classification for the created record.</summary>
    public GcodeSource Source { get; init; } = GcodeSource.Upload;

    /// <summary>Immutable promotion lineage stamped onto the created record.</summary>
    public required GcodePromotionLineage Lineage { get; init; }
}

/// <summary>Outcome of a stream ingest.</summary>
/// <param name="File">The stored G-code file.</param>
/// <param name="AlreadyExisted">
/// <see langword="true"/> when identical content was already in the library, in which case no second
/// copy was created and the existing record is returned.
/// </param>
public readonly record struct GcodeStreamIngestResult(GcodeFile File, bool AlreadyExisted);

/// <summary>
/// Raised when ingested bytes do not match the digest or size the caller verified them against.
/// </summary>
public sealed class GcodeStreamIngestException : InvalidOperationException
{
    /// <summary>The copied bytes produced a different SHA-256 than the caller declared.</summary>
    public const string HashMismatch = "gcode_content_hash_mismatch";

    /// <summary>The copied bytes had a different length than the caller declared.</summary>
    public const string SizeMismatch = "gcode_content_size_mismatch";

    /// <summary>Initializes a new instance of the <see cref="GcodeStreamIngestException"/> class.</summary>
    /// <param name="code">A stable machine-readable reason.</param>
    /// <param name="message">A non-sensitive explanation.</param>
    public GcodeStreamIngestException(string code, string message)
        : base(message) => Code = code;

    /// <summary>Initializes a new instance of the <see cref="GcodeStreamIngestException"/> class.</summary>
    public GcodeStreamIngestException()
        : this(HashMismatch, "The ingested G-code bytes failed verification.")
    {
    }

    /// <summary>Initializes a new instance of the <see cref="GcodeStreamIngestException"/> class.</summary>
    /// <param name="message">A non-sensitive explanation.</param>
    public GcodeStreamIngestException(string message)
        : this(HashMismatch, message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="GcodeStreamIngestException"/> class.</summary>
    /// <param name="message">A non-sensitive explanation.</param>
    /// <param name="innerException">The underlying failure.</param>
    public GcodeStreamIngestException(string message, Exception innerException)
        : base(message, innerException) => Code = HashMismatch;

    /// <summary>Gets the stable machine-readable reason.</summary>
    public string Code { get; }
}
