namespace Farm.Infrastructure.Domain;

/// <summary>Lifecycle states for a calibration project.</summary>
public enum CalibrationProjectLifecycleStatus
{
    Active = 0,
    Completed = 1,
    Archived = 2,
}

/// <summary>The level of calibration guidance requested by the operator.</summary>
public enum CalibrationExperienceMode
{
    Coach = 0,
    Expert = 1,
}

/// <summary>The durable state of a resumable calibration orchestration.</summary>
public enum CalibrationOrchestrationStatus
{
    Pending = 0,
    Running = 1,
    WaitingToRetry = 2,
    Completed = 3,
    Failed = 4,
    Cancelled = 5,
}

/// <summary>The immutable type of a row in the calibration change journal.</summary>
public enum CalibrationChangeType
{
    Created = 0,
    Updated = 1,
    Deleted = 2,
}

/// <summary>The lifecycle of an idempotency operation.</summary>
public enum CalibrationIdempotencyState
{
    InProgress = 0,
    Completed = 1,
    Failed = 2,
}

/// <summary>
/// Authoritative editable root for a user's printer-calibration work.
/// Slicer, printer, spool, and execution identifiers are soft references so this
/// bounded context remains deployable independently from those services.
/// </summary>
public sealed class CalibrationProject
{
    public Guid Id { get; set; }

    public Guid OwnerUserId { get; set; }

    public string Name { get; set; } = string.Empty;

    public CalibrationProjectLifecycleStatus LifecycleStatus { get; set; }

    public CalibrationExperienceMode ExperienceMode { get; set; }

    public Guid PrinterId { get; set; }

    public Guid? CurrentPrinterConfigurationSnapshotId { get; set; }

    public Guid? SelectedToolheadId { get; set; }

    public int? SelectedToolheadIndex { get; set; }

    public string FilamentProvider { get; set; } = string.Empty;

    public string FilamentProductId { get; set; } = string.Empty;

    public string? FilamentSku { get; set; }

    public string? FilamentVendor { get; set; }

    public string FilamentProductName { get; set; } = string.Empty;

    public string FilamentMaterial { get; set; } = string.Empty;

    public decimal? FilamentDiameter { get; set; }

    public string? FilamentColor { get; set; }

    public Guid? FilamentTypeId { get; set; }

    public Guid? SpoolmanFilamentId { get; set; }

    public Guid? LocalSpoolId { get; set; }

    public Guid? SpoolmanSpoolId { get; set; }

    public string FilamentSnapshotJson { get; set; } = "{}";

    public string OrderedStepsJson { get; set; } = "[]";

    public string? CurrentStep { get; set; }

    public string CurrentSelectionsJson { get; set; } = "{}";

    public long Revision { get; set; } = 1;

    public string CreateRequestId { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    public DateTime? CompletedAtUtc { get; set; }

    public string CreatedBySubject { get; set; } = string.Empty;

    public string UpdatedBySubject { get; set; } = string.Empty;

    public string? DeletedBySubject { get; set; }

    public DateTime? DeletedAtUtc { get; set; }
}

/// <summary>
/// Immutable, credential-free snapshot of the explicit printer and profile
/// context used by a project or attempt.
/// </summary>
public sealed class PrinterConfigurationSnapshot
{
    public Guid Id { get; set; }

    public Guid ProjectId { get; set; }

    public Guid? AttemptId { get; set; }

    public Guid PrinterId { get; set; }

    public string SchemaVersion { get; set; } = string.Empty;

    public string SanitizedSnapshotJson { get; set; } = "{}";

    public string SnapshotSha256 { get; set; } = string.Empty;

    public long PrinterConfigurationRevision { get; set; }

    public PrinterFirmwareFamily FirmwareFamily { get; set; }

    public PrinterGcodeDialect GcodeDialect { get; set; }

    public FirmwareDetectionSource FirmwareDetectionSource { get; set; }

    public string? FirmwareVersion { get; set; }

    public int Backend { get; set; }

    public string? BackendVersion { get; set; }

    public string? BackendApiVersion { get; set; }

    public string SlicerEngine { get; set; } = string.Empty;

    public string SlicerDistribution { get; set; } = string.Empty;

    public string? SlicerVersion { get; set; }

    public string? SlicerContainerDigest { get; set; }

    public Guid? MachineProfileId { get; set; }

    public string? ExactMachineProfileJson { get; set; }

    public string? MachineProfileSha256 { get; set; }

    public Guid? ProcessProfileId { get; set; }

    public string? ExactProcessProfileJson { get; set; }

    public string? ProcessProfileSha256 { get; set; }

    public Guid? FilamentProfileId { get; set; }

    public string? ExactFilamentProfileJson { get; set; }

    public string? FilamentProfileSha256 { get; set; }

    public DateTime CapturedAtUtc { get; set; }

    public string CapturedBySubject { get; set; } = string.Empty;
}

/// <summary>Editable, device-lineage-specific work in progress for a calibration step.</summary>
public sealed class CalibrationDraft
{
    public Guid Id { get; set; }

    public Guid ProjectId { get; set; }

    public string StepId { get; set; } = string.Empty;

    public string DeviceLineageId { get; set; } = string.Empty;

    public string Method { get; set; } = string.Empty;

    public string ValuesJson { get; set; } = "{}";

    public string PrerequisitesJson { get; set; } = "{}";

    public long Revision { get; set; } = 1;

    public DateTime CreatedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    public string CreatedBySubject { get; set; } = string.Empty;

    public string UpdatedBySubject { get; set; } = string.Empty;

    public DateTime? DeletedAtUtc { get; set; }
}

/// <summary>Immutable execution plan for one calibration run.</summary>
public sealed class CalibrationAttempt
{
    public Guid Id { get; set; }

    public Guid ProjectId { get; set; }

    public long Sequence { get; set; }

    public Guid? ParentAttemptId { get; set; }

    public string CalibrationKind { get; set; } = string.Empty;

    public string Method { get; set; } = string.Empty;

    public string DefinitionVersion { get; set; } = string.Empty;

    public string InputJson { get; set; } = "{}";

    public string SpecificationJson { get; set; } = "{}";

    public string SpecificationSha256 { get; set; } = string.Empty;

    public Guid? PrinterConfigurationSnapshotId { get; set; }

    public string ProfileSnapshotIdsJson { get; set; } = "[]";

    public string? ActualSpoolSnapshotJson { get; set; }

    public string AttemptRequestId { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }

    public string CreatedBySubject { get; set; } = string.Empty;
}

/// <summary>Append-only lifecycle fact for a calibration attempt.</summary>
public sealed class CalibrationAttemptEvent
{
    public Guid Id { get; set; }

    public Guid ProjectId { get; set; }

    public Guid AttemptId { get; set; }

    public long Sequence { get; set; }

    public string EventType { get; set; } = string.Empty;

    public string DerivedStatus { get; set; } = string.Empty;

    public Guid? Model3DId { get; set; }

    public Guid? SliceJobId { get; set; }

    public Guid? ArtifactId { get; set; }

    public Guid? GcodeFileId { get; set; }

    public Guid? PrintJobId { get; set; }

    public Guid? CalibrationOrchestrationId { get; set; }

    public string? ErrorCode { get; set; }

    public string? ErrorJson { get; set; }

    public int? RetryNumber { get; set; }

    public string OperationId { get; set; } = string.Empty;

    public DateTime OccurredAtUtc { get; set; }

    public string ActorSubject { get; set; } = string.Empty;
}

/// <summary>Append-only measurement or operator result for a calibration attempt.</summary>
public sealed class CalibrationObservation
{
    public Guid Id { get; set; }

    public Guid ProjectId { get; set; }

    public Guid AttemptId { get; set; }

    public long Sequence { get; set; }

    public string ObservationType { get; set; } = string.Empty;

    public string MeasurementsJson { get; set; } = "{}";

    public string ResultJson { get; set; } = "{}";

    public string UnitsJson { get; set; } = "{}";

    public decimal? Confidence { get; set; }

    public bool RetestRecommended { get; set; }

    public string? Notes { get; set; }

    public Guid? SelectionParentObservationId { get; set; }

    public string? SelectionReason { get; set; }

    public string OperationId { get; set; } = string.Empty;

    public DateTime ObservedAtUtc { get; set; }

    public string ActorSubject { get; set; } = string.Empty;
}

/// <summary>Authenticated metadata for a privately stored calibration image.</summary>
public sealed class CalibrationPhoto
{
    public Guid Id { get; set; }

    public Guid ProjectId { get; set; }

    public Guid AttemptId { get; set; }

    public string ClientUploadId { get; set; } = string.Empty;

    // This identifier is intentionally never mapped to an API DTO.
    public string OpaqueStorageKey { get; set; } = string.Empty;

    public string OriginalFileName { get; set; } = string.Empty;

    public string ContentType { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    public string Sha256 { get; set; } = string.Empty;

    public int Width { get; set; }

    public int Height { get; set; }

    public DateTime? CapturedAtUtc { get; set; }

    public string? Caption { get; set; }

    public int SortOrder { get; set; }

    public long Revision { get; set; } = 1;

    public DateTime CreatedAtUtc { get; set; }

    public string CreatedBySubject { get; set; } = string.Empty;

    public DateTime? DeletedAtUtc { get; set; }

    public string? DeletedBySubject { get; set; }

    public DateTime? DeleteRequestedAtUtc { get; set; }

    public DateTime? PurgedAtUtc { get; set; }
}

/// <summary>
/// Durable compensating cleanup for a private blob whose metadata transaction
/// failed after the blob was written.
/// </summary>
public sealed class CalibrationBlobCleanup
{
    public Guid Id { get; set; }

    public string OpaqueStorageKey { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }
}

/// <summary>Immutable authoritative version of an upstream OrcaSlicer profile.</summary>
public sealed class GeneratedProfileRevision
{
    public Guid Id { get; set; }

    public Guid ProjectId { get; set; }

    public Guid SourceAttemptId { get; set; }

    public Guid? ParentRevisionId { get; set; }

    public long RevisionNumber { get; set; }

    public string ProfileType { get; set; } = string.Empty;

    public string SchemaVersion { get; set; } = string.Empty;

    public string SlicerEngine { get; set; } = string.Empty;

    public string SlicerDistribution { get; set; } = string.Empty;

    public string? SlicerVersion { get; set; }

    public string? SlicerContainerDigest { get; set; }

    public string Name { get; set; } = string.Empty;

    public string NormalizedSettingsJson { get; set; } = "{}";

    public decimal? FlowRatio { get; set; }

    public decimal? PressureAdvance { get; set; }

    public decimal? PressureAdvanceSmoothTime { get; set; }

    public decimal? RetractionLength { get; set; }

    public decimal? RetractionSpeed { get; set; }

    public decimal? RetractionMinimumTravel { get; set; }

    public decimal? RetractionLiftZ { get; set; }

    public int? NozzleTemperature { get; set; }

    public int? BedTemperature { get; set; }

    public decimal? MaximumVolumetricFlow { get; set; }

    public Guid? SourceMachineProfileId { get; set; }

    public Guid? SourceProcessProfileId { get; set; }

    public Guid? SourceFilamentProfileId { get; set; }

    public string SourceProfileFingerprint { get; set; } = string.Empty;

    public string ExactProfileJson { get; set; } = "{}";

    public string Sha256 { get; set; } = string.Empty;

    public string GeneratorVersion { get; set; } = string.Empty;

    public string GenerationRequestId { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }

    public string CreatedBySubject { get; set; } = string.Empty;
}

/// <summary>Append-only audit row for a generated-profile export or publication request.</summary>
public sealed class GeneratedProfileRevisionOperation
{
    public Guid Id { get; set; }

    public Guid GeneratedProfileRevisionId { get; set; }

    public string OperationType { get; set; } = string.Empty;

    public string OperationId { get; set; } = string.Empty;

    public Guid? PublishedProfileId { get; set; }

    public string? ExportFormat { get; set; }

    public DateTime OccurredAtUtc { get; set; }

    public string ActorSubject { get; set; } = string.Empty;
}

/// <summary>Durable exact-replay record for a caller's calibration mutation.</summary>
public sealed class CalibrationIdempotencyRecord
{
    public Guid Id { get; set; }

    public Guid OwnerUserId { get; set; }

    public Guid? ProjectId { get; set; }

    public string Scope { get; set; } = string.Empty;

    public string ClientId { get; set; } = string.Empty;

    public string OperationId { get; set; } = string.Empty;

    public string OperationType { get; set; } = string.Empty;

    public string CanonicalRequestSha256 { get; set; } = string.Empty;

    public string ResourceType { get; set; } = string.Empty;

    public Guid? ResourceId { get; set; }

    public int StoredStatusCode { get; set; }

    public string? StoredResultJson { get; set; }

    public CalibrationIdempotencyState State { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime? CompletedAtUtc { get; set; }

    public DateTime? ExpiresAtUtc { get; set; }
}

/// <summary>Durable checkpoint for cross-context calibration effects.</summary>
public sealed class CalibrationOrchestration
{
    public Guid Id { get; set; }

    public Guid ProjectId { get; set; }

    public Guid AttemptId { get; set; }

    public string CurrentStep { get; set; } = string.Empty;

    public CalibrationOrchestrationStatus Status { get; set; }

    public int RetryCount { get; set; }

    public DateTime? NextRetryAtUtc { get; set; }

    public string? LastErrorCode { get; set; }

    public string? LastErrorJson { get; set; }

    public Guid? Model3DId { get; set; }

    public Guid? SliceJobId { get; set; }

    public Guid? SourceArtifactId { get; set; }

    public Guid? GcodeFileId { get; set; }

    public Guid? PrintJobId { get; set; }

    public string OperationId { get; set; } = string.Empty;

    public long Revision { get; set; } = 1;

    public DateTime CreatedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    public DateTime? CompletedAtUtc { get; set; }

    /// <summary>SHA-256 of the canonical generation request that owns this orchestration run.</summary>
    /// <remarks>
    /// A second call carrying the same operation identifier but a different canonical payload is a
    /// conflict rather than a resume, so the digest is durable rather than recomputed from memory.
    /// </remarks>
    public string? GenerationRequestSha256 { get; set; }

    /// <summary>SHA-256 of the recompiled canonical specification this run is pinned to.</summary>
    public string? SpecificationSha256 { get; set; }

    /// <summary>SHA-256 of the compiled upstream-Orca plan manifest.</summary>
    public string? PlanManifestSha256 { get; set; }

    /// <summary>SHA-256 of the final annotated calibration G-code.</summary>
    public string? GcodeSha256 { get; set; }

    /// <summary>SHA-256 of the canonical calibration manifest describing the final G-code.</summary>
    public string? ManifestSha256 { get; set; }

    /// <summary>Version of the trusted generator that produced the specification, plan and program.</summary>
    public string? GeneratorVersion { get; set; }

    /// <summary>Pinned slicer container digest the accepted worker attested.</summary>
    public string? SlicerContainerDigest { get; set; }

    /// <summary>Pinned slicer binary digest the accepted worker attested.</summary>
    public string? SlicerBinarySha256 { get; set; }

    /// <summary>Worker that claimed and executed the submitted slice job.</summary>
    public Guid? WorkerId { get; set; }

    /// <summary>Server-composed final artifact that was safety validated and promoted.</summary>
    public Guid? FinalArtifactId { get; set; }

    /// <summary>Idempotency operation key used for the artifact promotion hop.</summary>
    public string? PromotionOperationId { get; set; }

    /// <summary>UTC timestamp at which the current step started, used for stuck-step reconciliation.</summary>
    public DateTime? StepStartedAtUtc { get; set; }

    /// <summary>Opaque owner of the current in-process lease, or <see langword="null"/> when free.</summary>
    /// <remarks>
    /// The lease is advisory and always bounded by <see cref="LeaseExpiresAtUtc"/>. It exists so a
    /// restarted host and a live request do not process the same orchestration concurrently; the
    /// durable checkpoints, not the lease, are what make the saga correct.
    /// </remarks>
    public string? LeaseOwner { get; set; }

    /// <summary>UTC instant at which the current lease lapses.</summary>
    public DateTime? LeaseExpiresAtUtc { get; set; }
}

/// <summary>Append-only, cursor-addressable row in the calibration synchronization journal.</summary>
public sealed class CalibrationChange
{
    public long Sequence { get; set; }

    public Guid Id { get; set; }

    public Guid OwnerUserId { get; set; }

    public Guid ProjectId { get; set; }

    public string EntityType { get; set; } = string.Empty;

    public Guid EntityId { get; set; }

    public long EntityRevision { get; set; }

    public CalibrationChangeType ChangeType { get; set; }

    public string? TombstoneJson { get; set; }

    public string MutationId { get; set; } = string.Empty;

    public string ActorSubject { get; set; } = string.Empty;

    public DateTime OccurredAtUtc { get; set; }
}

/// <summary>
/// Singleton durable allocator that serializes calibration journal publication.
/// Its row is updated in the same transaction as each aggregate mutation and
/// journal row, so a later cursor can never observe a committed gap.
/// </summary>
public sealed class CalibrationChangeFeedState
{
    public int Id { get; set; }

    public long LastSequence { get; set; }
}

/// <summary>Opaque, owner-scoped durable position in the calibration change feed.</summary>
public sealed class CalibrationSyncCursor
{
    public Guid Id { get; set; }

    public string Scope { get; set; } = string.Empty;

    public long Sequence { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}
