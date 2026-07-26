using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Farm.Infrastructure.Domain;

/// <summary>
/// Builds the owner-scoped identity that promotion uniqueness is enforced on.
/// </summary>
/// <remarks>
/// The caller-supplied <c>Idempotency-Key</c> is only unique inside the caller's own scope, so it can
/// never be the persisted identity: two owners are allowed to pick the same key. The stored key is a
/// digest of the owner scope and the raw key, which keeps database uniqueness owner-scoped without
/// exposing the internal scope to any caller.
/// </remarks>
public static class GcodePromotionOperationKey
{
    private const string ScopePrefix = "gcode-promotion:user:";

    /// <summary>Returns the idempotency partition an owner's operation keys live in.</summary>
    /// <param name="ownerUserId">Owner of the source slice job.</param>
    /// <returns>The owner-scoped partition name.</returns>
    public static string ScopeFor(Guid ownerUserId) =>
        ScopePrefix + ownerUserId.ToString("N", CultureInfo.InvariantCulture);

    /// <summary>Computes the persisted identity for an owner's operation key.</summary>
    /// <param name="ownerUserId">Owner of the source slice job.</param>
    /// <param name="operationId">The caller-supplied idempotency operation key.</param>
    /// <returns>The SHA-256 (hex) scoped operation key.</returns>
    public static string Compute(Guid ownerUserId, string operationId) =>
        Compute(ScopeFor(ownerUserId), operationId);

    /// <summary>Computes the persisted identity for an operation key inside a known scope.</summary>
    /// <param name="operationScope">The owner-scoped idempotency partition.</param>
    /// <param name="operationId">The caller-supplied idempotency operation key.</param>
    /// <returns>The SHA-256 (hex) scoped operation key.</returns>
    public static string Compute(string operationScope, string operationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationScope);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        byte[] canonical = Encoding.UTF8.GetBytes($"{operationScope}\n{operationId.Trim()}");
        return Convert.ToHexString(SHA256.HashData(canonical));
    }
}

/// <summary>
/// Durable state of a single <c>Artifact -&gt; GcodeFile</c> promotion.
/// </summary>
/// <remarks>
/// The slicer artifact and the promoted G-code live in different database contexts, so the promotion
/// is recorded as a checkpoint instead of a distributed transaction. Every state is recoverable: a
/// crash at any point leaves enough information for the reconciler to determine the real outcome.
/// </remarks>
public enum GcodePromotionState
{
    /// <summary>The promotion was accepted and the source artifact was pinned against cleanup.</summary>
    Pending = 0,

    /// <summary>The bytes were streamed and verified but the terminal result is not committed yet.</summary>
    BytesStored = 1,

    /// <summary>The promotion produced a durable G-code file.</summary>
    Completed = 2,

    /// <summary>The promotion failed permanently and released the source artifact.</summary>
    Failed = 3,
}

/// <summary>
/// Durable request/result checkpoint for promoting a slicer artifact into the G-code library.
/// </summary>
/// <remarks>
/// The row is written before any bytes are copied and updated after every irreversible step, which is
/// what makes retries, restarts and split deployments reconcilable without a cross-context transaction.
/// </remarks>
public sealed class GcodePromotionCheckpoint
{
    /// <summary>Primary identifier.</summary>
    public Guid Id { get; set; }

    /// <summary>Owner of the source slice job at the time the promotion was accepted.</summary>
    public Guid OwnerUserId { get; set; }

    /// <summary>Owner-scoped idempotency partition (for example <c>user:{id}</c>).</summary>
    public string OperationScope { get; set; } = string.Empty;

    /// <summary>Caller-supplied idempotency operation key.</summary>
    public string OperationId { get; set; } = string.Empty;

    /// <summary>SHA-256 (hex) of the canonical immutable request payload.</summary>
    public string RequestSha256 { get; set; } = string.Empty;

    /// <summary>Source artifact identifier (soft ref — the artifact lives in the slicer context).</summary>
    public Guid SourceArtifactId { get; set; }

    /// <summary>Slice job that produced the source artifact.</summary>
    public Guid SourceSliceJobId { get; set; }

    /// <summary>Worker that produced the source artifact, when the artifact records one.</summary>
    public Guid? SourceWorkerId { get; set; }

    /// <summary>SHA-256 (hex) of the source artifact content.</summary>
    public string SourceContentSha256 { get; set; } = string.Empty;

    /// <summary>Size in bytes of the source artifact content.</summary>
    public long SourceSizeBytes { get; set; }

    /// <summary>Calibration project the promotion belongs to.</summary>
    public Guid? CalibrationProjectId { get; set; }

    /// <summary>Calibration attempt the promotion belongs to.</summary>
    public Guid? CalibrationAttemptId { get; set; }

    /// <summary>Durable orchestration that requested the promotion.</summary>
    public Guid? CalibrationOrchestrationId { get; set; }

    /// <summary>
    /// Identity the promoted G-code file will use. Assigned before any bytes are written so an
    /// interrupted promotion can be resolved by looking the identity up.
    /// </summary>
    public Guid GcodeFileId { get; set; }

    /// <summary>Current durable state.</summary>
    public GcodePromotionState State { get; set; }

    /// <summary>Stable machine-readable failure reason when <see cref="State"/> is failed.</summary>
    public string? FailureCode { get; set; }

    /// <summary>Number of reconciliation attempts applied to this checkpoint.</summary>
    public int ReconcileAttempts { get; set; }

    /// <summary>
    /// UTC timestamp when the slicer context acknowledged the terminal promotion result. Until it is
    /// set the source artifact stays pinned, because its lineage is not recoverable from that side yet.
    /// </summary>
    public DateTime? SourceAcknowledgedAtUtc { get; set; }

    /// <summary>UTC creation timestamp.</summary>
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>UTC timestamp of the last durable state change.</summary>
    public DateTime UpdatedAtUtc { get; set; }

    /// <summary>UTC timestamp of the terminal state transition.</summary>
    public DateTime? CompletedAtUtc { get; set; }

    /// <summary>Optimistic concurrency token guarding concurrent promotion of the same operation.</summary>
    public long Revision { get; set; } = 1;

    /// <summary>
    /// Returns the owner-scoped identity the promoted artifact and G-code file are stamped with.
    /// </summary>
    /// <returns>The SHA-256 (hex) scoped operation key.</returns>
    public string ScopedOperationKey() => GcodePromotionOperationKey.Compute(OperationScope, OperationId);
}
