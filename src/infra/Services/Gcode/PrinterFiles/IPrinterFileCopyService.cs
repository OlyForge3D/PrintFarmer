using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Services.Gcode.PrinterFiles;

/// <summary>Explicit intent; no source URLs, credentials, remote metadata overrides or job payloads.</summary>
public sealed record PrinterFileCopyCommand(
    Guid LocationId,
    long ObservationRevision,
    string? VirtualDirectory = "/",
    PrinterFileAction Action = PrinterFileAction.Copy,
    Guid? DestinationPrinterId = null,
    PrinterFileQueueOptions? Queue = null);

/// <summary>Immutable enqueue intent; metadata requirements always come from captured local bytes.</summary>
public sealed record PrinterFileQueueOptions(
    int Copies = 1,
    PrintJobPriority Priority = PrintJobPriority.Normal,
    DateTime? DeadlineAtUtc = null,
    Guid? ProjectId = null,
    Guid? ProjectFileId = null,
    int? PlateIndex = null,
    string? PlateName = null);

/// <summary>Explicit consent binds the actor's exact captured bytes and immutable enqueue payload.</summary>
public sealed record PrinterFileSnapshotConfirmation(
    Guid GcodeFileId, string ContentSha256, long SizeBytes, string PayloadSha256, bool Accept);

/// <summary>Safe durable progress; no backend connection details or serialized command are exposed.</summary>
public sealed record PrinterFileCopyOperation(
    Guid Id, long Revision, PrinterFileAction Action, PrinterFileOperationState State,
    Guid LocationId, Guid? GcodeFileId, string? ContentSha256, long? SizeBytes,
    PrinterFileVerification Verification, string? ReasonCode, bool CancellationRequested,
    Guid? DestinationPrinterId = null, string? PayloadSha256 = null, IReadOnlyList<Guid>? JobIds = null);

/// <summary>
/// Shared Copy/Queue staging seam. Queue stops at LocalReady/AwaitingSourceConfirmation;
/// this service never admits a job. HTTP callers must additionally require an interactive session.
/// </summary>
public interface IPrinterFileCopyService
{
    Task<IReadOnlyList<PrinterFileCopyOperation>> ListAsync(Guid actorId, CancellationToken ct = default);

    Task<PrinterFileCopyOperation> CreateAsync(
        Guid actorId, string idempotencyKey, PrinterFileCopyCommand command, CancellationToken ct = default);

    Task<PrinterFileCopyOperation?> GetAsync(Guid actorId, Guid operationId, CancellationToken ct = default);

    Task<PrinterFileCopyOperation?> RetryAsync(Guid actorId, Guid operationId, CancellationToken ct = default);

    Task<PrinterFileCopyOperation?> CancelAsync(Guid actorId, Guid operationId, CancellationToken ct = default);
}

/// <summary>Safe command rejection suitable for mapping to an HTTP status without transport details.</summary>
public sealed class PrinterFileCommandException : InvalidOperationException
{
    public PrinterFileCommandException(string reason, int statusCode) : base(reason)
    {
        Reason = reason;
        StatusCode = statusCode;
    }

    public PrinterFileCommandException() : this("copy_rejected")
    {
    }

    public PrinterFileCommandException(string message) : this(message, 409)
    {
    }

    public PrinterFileCommandException(string message, Exception innerException) : base(message, innerException)
    {
        Reason = "copy_rejected";
        StatusCode = 409;
    }

    public string Reason { get; }

    public int StatusCode { get; }
}
