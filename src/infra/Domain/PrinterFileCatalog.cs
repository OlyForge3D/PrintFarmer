namespace Farm.Infrastructure.Domain;

/// <summary>Availability of one observed remote path, independent of local copies.</summary>
public enum PrinterFileAvailability
{
    Available,
    Missing,
    Deleted,
}

/// <summary>The last inventory attempt; only Complete can establish absence.</summary>
public enum PrinterFileScanOutcome
{
    NeverScanned,
    Complete,
    Partial,
    Unavailable,
    Unsupported,
    Cancelled,
    Deferred,
    AuthenticationRequired,
}

/// <summary>Explicit actions on one registered printer location.</summary>
public enum PrinterFileAction
{
    Copy,
    Queue,
    Delete,
}

/// <summary>Durable progress; nonterminal states never imply an admitted job.</summary>
public enum PrinterFileOperationState
{
    Pending,
    Copying,
    Validating,
    LocalReady,
    AwaitingSourceConfirmation,
    Enqueueing,
    Queued,
    AwaitingDeleteConfirmation,
    SendingDelete,
    ReconcilingDelete,
    Deleted,
    Blocked,
    Failed,
    Cancelled,
}

/// <summary>Evidence about the source, not just the digest of received bytes.</summary>
public enum PrinterFileVerification
{
    UnverifiedSnapshot,
    ProviderVersion,
    ProviderDigest,
}

/// <summary>
/// A printer/root/path observation. IdentityKey hashes backend-canonical strings
/// ordinally so database collation cannot merge distinct paths.
/// </summary>
public sealed class PrinterFileLocation : IRevisionedEntity
{
    public Guid Id { get; set; }

    public long Revision { get; set; } = 1;

    public Guid SourcePrinterId { get; set; }

    public Guid? RegisteredPrinterId { get; set; }

    public Printer? RegisteredPrinter { get; set; }

    public string StorageRoot { get; set; } = string.Empty;

    public string RelativePath { get; set; } = string.Empty;

    public string IdentityKey { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public long? SizeBytes { get; set; }

    public string? ModifiedValue { get; set; }

    public string? ProviderVersion { get; set; }

    public string? ProviderDigest { get; set; }

    public long ObservationRevision { get; set; } = 1;

    public long ConfigurationGeneration { get; set; } = 1;

    public long MutationGeneration { get; set; }

    public DateTime FirstSeenAtUtc { get; set; }

    public DateTime LastSeenAtUtc { get; set; }

    public DateTime? LastCompleteScanAtUtc { get; set; }

    public PrinterFileAvailability Availability { get; set; }
}

/// <summary>
/// Persisted enrollment, due time and exclusive feature ownership for a printer.
/// Printer deletion removes the live association, not provenance or imported bytes.
/// </summary>
public sealed class PrinterFileScanState : IRevisionedEntity
{
    public Guid Id { get; set; }

    public long Revision { get; set; } = 1;

    public Guid SourcePrinterId { get; set; }

    public Guid? RegisteredPrinterId { get; set; }

    public Printer? RegisteredPrinter { get; set; }

    public bool Enabled { get; set; } = true;

    public long ConfigurationGeneration { get; set; } = 1;

    public string ConfigurationFingerprint { get; set; } = string.Empty;

    public long MutationGeneration { get; set; }

    public DateTime NextScanAtUtc { get; set; }

    public DateTime? LastAttemptAtUtc { get; set; }

    public DateTime? LastCompleteScanAtUtc { get; set; }

    public PrinterFileScanOutcome Outcome { get; set; }

    public string? ReasonCode { get; set; }

    public int ConsecutiveFailures { get; set; }

    public Guid? LeaseOwner { get; set; }

    public DateTime? LeaseExpiresAtUtc { get; set; }

    public long LeaseFence { get; set; }

    public DateTime? InteractiveRequestedAtUtc { get; set; }
}

/// <summary>Coverage of one root/subtree; incomplete attempts do not advance it.</summary>
public sealed class PrinterFileRootScan : IRevisionedEntity
{
    public Guid Id { get; set; }

    public long Revision { get; set; } = 1;

    public Guid ScanStateId { get; set; }

    public PrinterFileScanState ScanState { get; set; } = null!;

    public string StorageRoot { get; set; } = string.Empty;

    public string Subtree { get; set; } = string.Empty;

    public string IdentityKey { get; set; } = string.Empty;

    public long ConfigurationGeneration { get; set; }

    public long MutationGeneration { get; set; }

    public DateTime? LastCompleteScanAtUtc { get; set; }
}

/// <summary>
/// Immutable captured-version provenance. The asset link may be removed without
/// erasing the source snapshot, and deduplicated assets may have many origins.
/// </summary>
public sealed class PrinterFileOrigin
{
    public Guid Id { get; set; }

    public Guid OperationId { get; set; }

    public Guid SourcePrinterId { get; set; }

    public Guid ActorId { get; set; }

    public Guid? LocationId { get; set; }

    public PrinterFileLocation? Location { get; set; }

    public Guid? GcodeFileId { get; set; }

    public GcodeFile? GcodeFile { get; set; }

    public string StorageRoot { get; set; } = string.Empty;

    public string RelativePath { get; set; } = string.Empty;

    public long ObservationRevision { get; set; }

    public long ConfigurationGeneration { get; set; }

    public string? ProviderVersion { get; set; }

    public string? ProviderDigest { get; set; }

    public string ContentSha256 { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    public PrinterFileVerification Verification { get; set; }

    public DateTime CapturedAtUtc { get; set; }
}

/// <summary>
/// Actor-scoped durable command. Delete consent and captured-byte consent are
/// separate records; send-boundary persistence forbids blind destructive replay.
/// </summary>
public sealed class PrinterFileOperation : IRevisionedEntity
{
    public Guid Id { get; set; }

    public long Revision { get; set; } = 1;

    public Guid ActorId { get; set; }

    public string CommandKeyHash { get; set; } = string.Empty;

    public string PayloadSha256 { get; set; } = string.Empty;

    public Guid SourcePrinterId { get; set; }

    public Guid LocationId { get; set; }

    public PrinterFileLocation Location { get; set; } = null!;

    public long ObservationRevision { get; set; }

    public long ConfigurationGeneration { get; set; }

    public long MutationGeneration { get; set; }

    public PrinterFileAction Action { get; set; }

    public PrinterFileOperationState State { get; set; }

    public string CommandJson { get; set; } = string.Empty;

    public Guid? DestinationPrinterId { get; set; }

    public Guid? GcodeFileId { get; set; }

    public GcodeFile? GcodeFile { get; set; }

    public string? ContentSha256 { get; set; }

    public long? SizeBytes { get; set; }

    public long ReservedBytes { get; set; }

    public PrinterFileVerification Verification { get; set; }

    public string? SnapshotConfirmationSha256 { get; set; }

    public DateTime? SnapshotConfirmedAtUtc { get; set; }

    public string? DeleteConfirmationSha256 { get; set; }

    public DateTime? DeleteConfirmedAtUtc { get; set; }

    public bool CurrentPathRaceAccepted { get; set; }

    public DateTime? DeleteSentAtUtc { get; set; }

    public bool CancellationRequested { get; set; }

    public string? ReasonCode { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    public Guid? WorkerId { get; set; }

    public DateTime? WorkerLeaseExpiresAtUtc { get; set; }

    public long WorkerFence { get; set; }

    public ICollection<PrinterFileOperationJob> Jobs { get; set; } = [];
}

/// <summary>Committed atomically with admission; unique ordinal prevents replay.</summary>
public sealed class PrinterFileOperationJob
{
    public Guid OperationId { get; set; }

    public PrinterFileOperation Operation { get; set; } = null!;

    public int Ordinal { get; set; }

    public Guid JobId { get; set; }
}

/// <summary>A finite deployment-wide I/O slot, not a per-process semaphore.</summary>
public sealed class PrinterFileIoSlot : IRevisionedEntity
{
    public int Id { get; set; }

    public long Revision { get; set; } = 1;

    public Guid? LeaseOwner { get; set; }

    public Guid? SourcePrinterId { get; set; }

    public DateTime? LeaseExpiresAtUtc { get; set; }

    public long LeaseFence { get; set; }
}
