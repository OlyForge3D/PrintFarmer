using System.Text.Json.Serialization;

namespace Farm.Slicer.Module.Domain;

/// <summary>
/// Represents a persisted slicing output (G-code, preview image, log, etc.) stored locally on disk.
/// Metadata lives in the database; bytes live in the filesystem under a configured root.
/// </summary>
public class Artifact
{
    /// <summary>Primary identifier.</summary>
    public Guid Id { get; set; }

    /// <summary>Associated slice job identifier.</summary>
    public Guid JobId { get; set; }

    /// <summary>Worker producing this artifact (optional if legacy job or unknown source).</summary>
    public Guid? WorkerId { get; set; }

    /// <summary>Claim incarnation that authorized this worker artifact.</summary>
    [JsonIgnore]
    public Guid? ClaimToken { get; set; }

    /// <summary>Canonical kind of artifact. See <see cref="SlicerArtifactKinds"/> for the allowlist.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Original filename provided by slicer/worker (sanitized before storage).</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>Relative path from the configured artifact root to the stored file (no leading slash).</summary>
    [JsonIgnore]
    public string RelativePath { get; set; } = string.Empty;

    /// <summary>Content type for downstream consumers (UI, downloads).</summary>
    public string ContentType { get; set; } = "application/octet-stream";

    /// <summary>Size of the file in bytes.</summary>
    public long SizeBytes { get; set; }

    /// <summary>SHA-256 hash (hex) of the artifact for integrity and future dedup.</summary>
    [JsonIgnore]
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>
    /// SHA-256 hash (hex) declared by the producing worker before upload. Persisted for audit;
    /// the API rejects the upload when it does not match the computed hash.
    /// </summary>
    [JsonIgnore]
    public string? DeclaredSha256 { get; set; }

    /// <summary>UTC timestamp when the artifact was created/persisted.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Idempotency operation key of the promotion that currently owns this artifact, if any.
    /// </summary>
    /// <remarks>
    /// Caller-supplied and therefore only unique inside the owner's scope; it is kept for diagnostics
    /// and never used as the persisted identity.
    /// </remarks>
    [JsonIgnore]
    public string? PromotionOperationId { get; set; }

    /// <summary>
    /// Owner-scoped identity of the promotion that currently owns this artifact. Single-writer
    /// ownership is enforced on this column so two owners may reuse the same raw idempotency key.
    /// </summary>
    [JsonIgnore]
    public string? PromotionOperationKey { get; set; }

    /// <summary>Durable core-context checkpoint identifier coordinating the promotion.</summary>
    [JsonIgnore]
    public Guid? PromotionCheckpointId { get; set; }

    /// <summary>
    /// UTC timestamp when a promotion pinned this artifact. While this is set and
    /// <see cref="PromotedAtUtc"/> is not, the outcome is unknown and cleanup must leave the bytes alone.
    /// </summary>
    public DateTime? PromotionStartedAtUtc { get; set; }

    /// <summary>UTC timestamp when the promotion result became durable in the core context.</summary>
    public DateTime? PromotedAtUtc { get; set; }

    /// <summary>Promoted G-code file identity, recorded so lineage survives artifact cleanup.</summary>
    public Guid? PromotedGcodeFileId { get; set; }

    /// <summary>
    /// Whether cleanup may reclaim this artifact. A promotion in flight has an unknown outcome, so the
    /// artifact stays until the promoter or its reconciler resolves the result.
    /// </summary>
    /// <returns>
    /// <see langword="false"/> only while a promotion is pinned without a durable result.
    /// </returns>
    public bool IsCleanupEligible() =>
        PromotionStartedAtUtc is null || PromotedAtUtc is not null;
}

/// <summary>
/// Owner-scoped identity of a promotion operation as the slicer context sees it.
/// </summary>
/// <param name="Key">
/// Stable owner-scoped key uniqueness is enforced on. It is opaque to callers and never returned by
/// an API.
/// </param>
/// <param name="OperationId">
/// The caller-supplied idempotency key, persisted for diagnostics only.
/// </param>
public readonly record struct PromotionOperationIdentity(string Key, string OperationId);
