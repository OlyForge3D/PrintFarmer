using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;

namespace Farm.Infrastructure.Services.Gcode.PrinterFiles;

public sealed record PrinterFileDeleteCommand(Guid LocationId, long ObservationRevision);

/// <summary>Consent is specific to THIS delete intent, never a Queue snapshot confirmation.</summary>
public sealed record PrinterFileDeleteConfirmation(
    string ConfirmationBinding, bool AcceptIrreversibleDelete, bool AcceptCurrentPathRace);

/// <summary>
/// SendingDelete after a crash is exposed as OutcomeUnknown, never a request to replay.
/// ObservedAbsent is evidence about the current path, not proof of historic delete success.
/// </summary>
public sealed record PrinterFileDeleteOperation(
    Guid Id, long Revision, PrinterFileOperationState State, Guid LocationId, Guid SourcePrinterId,
    string StorageRoot, string RelativePath, long ObservationRevision, long ConfigurationGeneration,
    PrinterFileDeleteGuarantee Guarantee, string ConfirmationBinding, bool CurrentPathRaceAccepted,
    DateTime? DeleteSentAtUtc, string Outcome, string? ReasonCode);

public interface IPrinterFileDeleteService
{
    Task<PrinterFileDeleteOperation> CreateAsync(
        Guid actorId, string idempotencyKey, PrinterFileDeleteCommand command, CancellationToken ct = default);

    Task<PrinterFileDeleteOperation?> GetAsync(Guid actorId, Guid operationId, CancellationToken ct = default);

    Task<PrinterFileDeleteOperation?> ConfirmAsync(
        Guid actorId, Guid operationId, PrinterFileDeleteConfirmation confirmation, CancellationToken ct = default);

    /// <summary>Read-only provider calls only; this method can never invoke deletion.</summary>
    Task<PrinterFileDeleteOperation?> ReconcileAsync(
        Guid actorId, Guid operationId, CancellationToken ct = default);
}
