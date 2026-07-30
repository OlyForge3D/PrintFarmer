using System;
using System.Threading;
using System.Threading.Tasks;
using Farm.Slicer.Module.Domain;
using Farm.Web.Api.Services.Calibration;

namespace Farm.Web.Api.Services.Gcode;

/// <summary>
/// Immutable promotion request. The same values always produce the same canonical payload hash, which
/// is what makes an exact replay indistinguishable from the original call.
/// </summary>
public sealed record GcodeArtifactPromotionRequest
{
    /// <summary>Caller-supplied idempotency operation key.</summary>
    public required string OperationId { get; init; }

    /// <summary>Completed slicer artifact to promote.</summary>
    public required Guid SourceArtifactId { get; init; }

    /// <summary>Slice job the caller believes produced the artifact.</summary>
    public required Guid SourceSliceJobId { get; init; }

    /// <summary>SHA-256 (hex) the caller verified for the artifact bytes.</summary>
    public required string ExpectedSha256 { get; init; }

    /// <summary>Byte count the caller verified for the artifact bytes.</summary>
    public required long ExpectedSizeBytes { get; init; }

    /// <summary>Canonical artifact kind; only <see cref="SlicerArtifactKinds.Gcode"/> is promotable.</summary>
    public string ArtifactKind { get; init; } = SlicerArtifactKinds.Gcode;

    /// <summary>Worker the caller believes produced the artifact.</summary>
    public Guid? SourceWorkerId { get; init; }

    /// <summary>Calibration project the promotion belongs to.</summary>
    public Guid? CalibrationProjectId { get; init; }

    /// <summary>Calibration attempt the promotion belongs to.</summary>
    public Guid? CalibrationAttemptId { get; init; }

    /// <summary>Durable orchestration requesting the promotion.</summary>
    public Guid? CalibrationOrchestrationId { get; init; }

    /// <summary>Virtual library directory that receives the promoted file.</summary>
    public string? VirtualDirectory { get; init; }
}

/// <summary>Non-sensitive description of a durable promotion.</summary>
public sealed record GcodePromotionDto
{
    /// <summary>Idempotency operation key that owns the promotion.</summary>
    public required string OperationId { get; init; }

    /// <summary>Source slicer artifact identity.</summary>
    public required Guid SourceArtifactId { get; init; }

    /// <summary>Slice job that produced the source artifact.</summary>
    public required Guid SourceSliceJobId { get; init; }

    /// <summary>Promoted G-code file identity. Stable across replays.</summary>
    public required Guid GcodeFileId { get; init; }

    /// <summary>SHA-256 (hex) of the promoted bytes.</summary>
    public required string ContentSha256 { get; init; }

    /// <summary>Size in bytes of the promoted content.</summary>
    public required long SizeBytes { get; init; }

    /// <summary>Durable promotion state name.</summary>
    public required string Status { get; init; }

    /// <summary>Calibration project the promotion belongs to.</summary>
    public Guid? CalibrationProjectId { get; init; }

    /// <summary>Calibration attempt the promotion belongs to.</summary>
    public Guid? CalibrationAttemptId { get; init; }

    /// <summary>Durable orchestration that requested the promotion.</summary>
    public Guid? CalibrationOrchestrationId { get; init; }

    /// <summary>Stable machine-readable failure reason when the promotion failed.</summary>
    public string? FailureCode { get; init; }

    /// <summary>Whether the slicer context recorded the terminal result against the source artifact.</summary>
    public required bool SourceAcknowledged { get; init; }

    /// <summary>Server-built calibration manifest for the promoted output.</summary>
    public string? CalibrationManifestJson { get; init; }

    /// <summary>UTC timestamp of the terminal transition.</summary>
    public DateTime? CompletedAtUtc { get; init; }
}

/// <summary>Health of every hop the promotion path depends on.</summary>
public sealed record GcodePromotionCapabilityDto
{
    /// <summary>Whether promotion can currently be performed end to end.</summary>
    public required bool Operational { get; init; }

    /// <summary>Whether artifact metadata and bytes are routable from this process.</summary>
    public required bool ArtifactSourceAvailable { get; init; }

    /// <summary>Whether the G-code library storage root is writable.</summary>
    public required bool LibraryStorageWritable { get; init; }

    /// <summary>Whether the durable checkpoint store answers queries.</summary>
    public required bool CheckpointStoreAvailable { get; init; }

    /// <summary>Whether the reconciler is wired and has not given up.</summary>
    public required bool ReconcilerHealthy { get; init; }

    /// <summary>Stable machine-readable reason when promotion is unavailable.</summary>
    public string? UnavailableCode { get; init; }
}

/// <summary>
/// Promotes completed slicer artifacts into the G-code library with verified bytes, immutable lineage
/// and database-enforced idempotency.
/// </summary>
/// <remarks>
/// The artifact lives in the slicer context and the promoted file lives in the core context, so the
/// promoter never claims a distributed transaction. It checkpoints the request, copies the bytes,
/// commits the result and then acknowledges the source; a crash at any step is resolved by
/// <see cref="ReconcilePendingAsync"/> rather than by guessing.
/// </remarks>
public interface IGcodeArtifactPromoter
{
    /// <summary>Reports whether every promotion hop is currently healthy.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The per-hop capability snapshot.</returns>
    Task<GcodePromotionCapabilityDto> GetCapabilityAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Promotes a completed artifact, or replays the stable result of an identical earlier request.
    /// </summary>
    /// <param name="request">The immutable promotion request.</param>
    /// <param name="actor">The authenticated caller.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <c>201</c> for a new promotion, <c>200</c> for an exact replay, <c>409</c> when the same
    /// operation key carries a different payload, and <c>503</c> when a required hop is unavailable.
    /// </returns>
    Task<CalibrationApiResult<GcodePromotionDto>> PromoteAsync(
        GcodeArtifactPromotionRequest request,
        CalibrationActor actor,
        CancellationToken cancellationToken);

    /// <summary>Returns the durable promotion recorded for an operation key.</summary>
    /// <param name="operationId">The idempotency operation key.</param>
    /// <param name="actor">The authenticated caller.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The promotion, or a not-found failure.</returns>
    Task<CalibrationApiResult<GcodePromotionDto>> GetPromotionAsync(
        string operationId,
        CalibrationActor actor,
        CancellationToken cancellationToken);

    /// <summary>Returns the durable promotion recorded for a source artifact.</summary>
    /// <param name="sourceArtifactId">The source artifact identity.</param>
    /// <param name="actor">The authenticated caller.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The promotion, or a not-found failure.</returns>
    Task<CalibrationApiResult<GcodePromotionDto>> GetPromotionByArtifactAsync(
        Guid sourceArtifactId,
        CalibrationActor actor,
        CancellationToken cancellationToken);

    /// <summary>
    /// Pins a source artifact against cleanup ahead of a promotion the caller is about to request.
    /// </summary>
    /// <remarks>
    /// A caller that re-validates the stored bytes before promoting would otherwise race artifact
    /// cleanup. The reservation is held by the same operation key the promotion will use, so the
    /// promoter adopts it instead of competing with it.
    /// </remarks>
    /// <param name="sourceArtifactId">The artifact the caller intends to promote.</param>
    /// <param name="operationId">The idempotency operation key the promotion will use.</param>
    /// <param name="actor">The authenticated caller.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <see langword="false"/> when the artifact is gone, unroutable or already owned by another
    /// promotion, in which case the caller must retry rather than promote.
    /// </returns>
    Task<bool> TryReserveSourceArtifactAsync(
        Guid sourceArtifactId,
        string operationId,
        CalibrationActor actor,
        CancellationToken cancellationToken);

    /// <summary>Releases a reservation the caller no longer intends to promote.</summary>
    /// <param name="sourceArtifactId">The reserved artifact.</param>
    /// <param name="operationId">The operation key that holds the reservation.</param>
    /// <param name="actor">The authenticated caller.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the reservation is no longer held.</returns>
    Task ReleaseSourceArtifactReservationAsync(
        Guid sourceArtifactId,
        string operationId,
        CalibrationActor actor,
        CancellationToken cancellationToken);

    /// <summary>Resolves one checkpoint whose outcome is unknown or unacknowledged.</summary>
    /// <param name="checkpointId">The checkpoint identity.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The resolved promotion, or a failure describing why it stays unresolved.</returns>
    Task<CalibrationApiResult<GcodePromotionDto>> ReconcileAsync(
        Guid checkpointId,
        CancellationToken cancellationToken);

    /// <summary>Resolves outstanding checkpoints after a restart or a transient outage.</summary>
    /// <param name="maxCheckpoints">Maximum number of checkpoints to resolve in this pass.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of checkpoints that reached a durable, acknowledged state.</returns>
    Task<int> ReconcilePendingAsync(int maxCheckpoints, CancellationToken cancellationToken);

    /// <summary>
    /// Reports whether the source artifact may be reclaimed by cleanup.
    /// </summary>
    /// <param name="sourceArtifactId">The source artifact identity.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <see langword="false"/> while a promotion of that artifact has an unknown or unacknowledged
    /// outcome, because its lineage would not be recoverable after deletion.
    /// </returns>
    Task<bool> IsSourceArtifactCleanupSafeAsync(Guid sourceArtifactId, CancellationToken cancellationToken);
}
