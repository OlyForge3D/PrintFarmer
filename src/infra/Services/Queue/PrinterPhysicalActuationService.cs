// <copyright file="PrinterPhysicalActuationService.cs" company="PlaceholderCompany">
// SPDX-License-Identifier: AGPL-3.0-only
// </copyright>

using System.Text.Json;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.Queue;

/// <summary>Result codes shared by all safety-sensitive printer actuation routes.</summary>
public enum PrinterActuationResultCode
{
    Accepted,
    PrinterNotFound,
    PrinterBusy,
    FenceConflict,
    ConcurrencyConflict,
}

/// <summary>Database-backed lease acquired before a direct physical backend call.</summary>
public sealed record PrinterActuationLease(
    Guid CommandId,
    Guid PrinterId,
    Guid? AttemptId,
    string Operation,
    string ActorSubject);

/// <summary>Result of acquiring or queuing a physical printer operation.</summary>
public sealed record PrinterActuationResult(
    PrinterActuationResultCode Code,
    PrinterActuationLease? Lease = null,
    Guid? CommandId = null,
    string? Detail = null)
{
    public bool Success => Code == PrinterActuationResultCode.Accepted;
}

/// <summary>
/// Enforces the physical-actuation resource/attempt matrix and owns the durable physical-I/O
/// barrier. Direct idle-only controls acquire a barrier before network I/O. Active lifecycle
/// controls are queued with the exact active attempt and executed by the durable consumer.
/// </summary>
public interface IPrinterPhysicalActuationService
{
    Task<PrinterActuationResult> AcquireDirectAsync(
        Guid printerId,
        string actorSubject,
        string operation,
        CancellationToken ct = default);

    Task<PrinterActuationResult> AcquireActiveAsync(
        Guid printerId,
        string actorSubject,
        string operation,
        CancellationToken ct = default);

    Task CompleteDirectAsync(
        PrinterActuationLease lease,
        bool accepted,
        string? failureCode = null,
        CancellationToken ct = default);

    Task MarkDirectUnknownAsync(
        PrinterActuationLease lease,
        string failureCode,
        CancellationToken ct = default);

    Task<PrinterActuationResult> QueueLifecycleAsync(
        Guid printerId,
        string actorSubject,
        string operation,
        CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class PrinterPhysicalActuationService(
    AppDbContext db,
    IDbOutboxSequenceAllocator sequenceAllocator,
    IQueueResourceAuthorizationService resourceAuthorization,
    ILogger<PrinterPhysicalActuationService> logger)
    : IPrinterPhysicalActuationService
{
    public const string EventTypeStarted = "PrintFarmer.Queue.PhysicalControlStarted.v1";
    public const string EventTypeCompleted = "PrintFarmer.Queue.PhysicalControlCompleted.v1";
    public const string EventTypeFailed = "PrintFarmer.Queue.PhysicalControlFailed.v1";
    public const string EventTypeUnknown = "PrintFarmer.Queue.PhysicalControlUnknown.v1";

    private readonly AppDbContext _db = db;
    private readonly IDbOutboxSequenceAllocator _sequenceAllocator = sequenceAllocator;
    private readonly IQueueResourceAuthorizationService _resourceAuthorization = resourceAuthorization;
    private readonly ILogger<PrinterPhysicalActuationService> _logger = logger;

    /// <inheritdoc />
    public async Task<PrinterActuationResult> AcquireDirectAsync(
        Guid printerId,
        string actorSubject,
        string operation,
        CancellationToken ct = default)
    {
        if (!await _resourceAuthorization.CanActorAccessPrinterAsync(
                actorSubject,
                printerId,
                PrinterGroupAccessLevel.Submit,
                ct))
        {
            await WriteDeniedAsync(
                printerId,
                actorSubject,
                operation,
                "printer_not_found",
                ct);
            return Denied(PrinterActuationResultCode.PrinterNotFound, "The printer was not found.");
        }

        PrinterDispatchState? state = await _db.PrinterDispatchStates
            .SingleOrDefaultAsync(candidate => candidate.PrinterId == printerId, ct);
        if (state is null)
        {
            return Denied(
                PrinterActuationResultCode.PrinterNotFound,
                "The printer dispatch state was not found.");
        }

        bool hasActiveJob = await _db.PrintJobs
            .WhereOccupiesPrinter()
            .AsNoTracking()
            .AnyAsync(
                job => job.AssignedPrinterId == printerId,
                ct);
        if (hasActiveJob || state.ActiveDispatchAttemptId.HasValue || state.ActiveJobId.HasValue)
        {
            await WriteDeniedAsync(
                printerId,
                actorSubject,
                operation,
                "active_dispatch_requires_lifecycle_route",
                ct,
                state);
            return Denied(
                PrinterActuationResultCode.PrinterBusy,
                "An active dispatch owns the printer; use an attempt-bound lifecycle route.");
        }

        if (state.PhysicalControlCommandId.HasValue)
        {
            await WriteDeniedAsync(
                printerId,
                actorSubject,
                operation,
                "physical_control_fence_conflict",
                ct,
                state);
            return Denied(
                PrinterActuationResultCode.FenceConflict,
                "Another physical operation owns the printer barrier.");
        }

        Guid commandId = Guid.NewGuid();
        DateTime now = DateTime.UtcNow;
        state.PhysicalControlCommandId = commandId;
        state.PhysicalControlAttemptId = null;
        state.PhysicalControlOperation = operation;
        state.PhysicalControlActorSubject = actorSubject;
        state.PhysicalControlStartedAtUtc = now;
        state.PhysicalControlRequiresReconciliation = false;

        await using QueueOutboxTransactionScope transaction =
            await QueueOutboxTransactionScope.BeginAsync(_db, ct);
        _ = QueueAuditWriter.Add(
            _db,
            actorSubject,
            AuditOperation(operation),
            QueueAuditOutcomes.Success,
            nameof(Printer),
            resourceId: printerId,
            printerId: printerId,
            dispatchStateRowVersion: state.RowVersion,
            detail: new { commandId, operation, barrierAcquired = true });
        await AddPrinterEventAsync(
            EventTypeStarted,
            commandId,
            printerId,
            attemptId: null,
            operation,
            failureCode: null,
            state.RowVersion,
            ct);

        try
        {
            await _db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Denied(
                PrinterActuationResultCode.ConcurrencyConflict,
                "The printer barrier changed concurrently.");
        }

        return new PrinterActuationResult(
            PrinterActuationResultCode.Accepted,
            new PrinterActuationLease(commandId, printerId, null, operation, actorSubject),
            commandId);
    }

    /// <inheritdoc />
    public async Task<PrinterActuationResult> AcquireActiveAsync(
        Guid printerId,
        string actorSubject,
        string operation,
        CancellationToken ct = default)
    {
        if (!await _resourceAuthorization.CanActorAccessPrinterAsync(
                actorSubject,
                printerId,
                PrinterGroupAccessLevel.Submit,
                ct))
        {
            await WriteDeniedAsync(
                printerId,
                actorSubject,
                operation,
                "printer_not_found",
                ct);
            return Denied(PrinterActuationResultCode.PrinterNotFound, "The printer was not found.");
        }

        PrinterDispatchState? state = await _db.PrinterDispatchStates
            .SingleOrDefaultAsync(candidate => candidate.PrinterId == printerId, ct);
        if (state?.ActiveDispatchAttemptId is not Guid attemptId ||
            !state.ActiveJobId.HasValue)
        {
            await WriteDeniedAsync(
                printerId,
                actorSubject,
                operation,
                "active_attempt_required",
                ct,
                state);
            return Denied(
                PrinterActuationResultCode.PrinterBusy,
                "The operation requires an active dispatch attempt.");
        }

        if (state.PhysicalControlCommandId.HasValue)
        {
            return Denied(
                PrinterActuationResultCode.FenceConflict,
                "Another physical operation owns the printer barrier.");
        }

        Guid commandId = Guid.NewGuid();
        DateTime now = DateTime.UtcNow;
        state.PhysicalControlCommandId = commandId;
        state.PhysicalControlAttemptId = attemptId;
        state.PhysicalControlOperation = operation;
        state.PhysicalControlActorSubject = actorSubject;
        state.PhysicalControlStartedAtUtc = now;
        state.PhysicalControlRequiresReconciliation = false;

        await using QueueOutboxTransactionScope transaction =
            await QueueOutboxTransactionScope.BeginAsync(_db, ct);
        _ = QueueAuditWriter.Add(
            _db,
            actorSubject,
            AuditOperation(operation),
            QueueAuditOutcomes.Success,
            nameof(PrintJob),
            resourceId: state.ActiveJobId,
            printerId: printerId,
            printJobId: state.ActiveJobId,
            dispatchAttemptId: attemptId,
            dispatchStateRowVersion: state.RowVersion,
            detail: new { commandId, operation, barrierAcquired = true });
        await AddPrinterEventAsync(
            EventTypeStarted,
            commandId,
            printerId,
            attemptId,
            operation,
            failureCode: null,
            state.RowVersion,
            ct);
        try
        {
            await _db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Denied(
                PrinterActuationResultCode.ConcurrencyConflict,
                "The active dispatch changed concurrently.");
        }

        return new PrinterActuationResult(
            PrinterActuationResultCode.Accepted,
            new PrinterActuationLease(
                commandId,
                printerId,
                attemptId,
                operation,
                actorSubject),
            commandId);
    }

    /// <inheritdoc />
    public Task CompleteDirectAsync(
        PrinterActuationLease lease,
        bool accepted,
        string? failureCode = null,
        CancellationToken ct = default) =>
        CompleteDirectCoreAsync(
            lease,
            accepted ? QueueAuditOutcomes.Success : QueueAuditOutcomes.Failed,
            accepted ? EventTypeCompleted : EventTypeFailed,
            accepted ? null : failureCode ?? "backend_control_rejected",
            retainBarrier: false,
            ct);

    /// <inheritdoc />
    public Task MarkDirectUnknownAsync(
        PrinterActuationLease lease,
        string failureCode,
        CancellationToken ct = default) =>
        CompleteDirectCoreAsync(
            lease,
            QueueAuditOutcomes.Unknown,
            EventTypeUnknown,
            failureCode,
            retainBarrier: true,
            ct);

    /// <inheritdoc />
    public async Task<PrinterActuationResult> QueueLifecycleAsync(
        Guid printerId,
        string actorSubject,
        string operation,
        CancellationToken ct = default)
    {
        if (operation is not ("pause" or "resume" or "cancel" or "abort" or "emergencystop"))
        {
            throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unsupported lifecycle control.");
        }

        if (!await _resourceAuthorization.CanActorAccessPrinterAsync(
                actorSubject,
                printerId,
                PrinterGroupAccessLevel.Submit,
                ct))
        {
            await WriteDeniedAsync(
                printerId,
                actorSubject,
                operation,
                "printer_not_found",
                ct);
            return Denied(PrinterActuationResultCode.PrinterNotFound, "The printer was not found.");
        }

        PrinterDispatchState? state = await _db.PrinterDispatchStates
            .SingleOrDefaultAsync(candidate => candidate.PrinterId == printerId, ct);
        bool canQueueBehindStart =
            state?.PhysicalControlCommandId.HasValue == true &&
            string.Equals(
                state.PhysicalControlOperation,
                "start",
                StringComparison.Ordinal) &&
            operation is "cancel" or "abort" or "emergencystop";
        if (state is null ||
            (state.PhysicalControlCommandId.HasValue && !canQueueBehindStart))
        {
            PrinterActuationResultCode code = state is null
                ? PrinterActuationResultCode.PrinterNotFound
                : PrinterActuationResultCode.FenceConflict;
            string detail = state is null
                ? "The printer dispatch state was not found."
                : "Another physical operation owns the printer barrier.";
            return Denied(code, detail);
        }

        PrintJob? activeJob = state.ActiveJobId.HasValue
            ? await _db.PrintJobs.SingleOrDefaultAsync(
                candidate => candidate.Id == state.ActiveJobId.Value,
                ct)
            : await _db.PrintJobs
                .WhereOccupiesPrinter()
                .Where(job => job.AssignedPrinterId == printerId)
                .OrderBy(job => job.ActualStartTime)
                .ThenBy(job => job.Id)
                .FirstOrDefaultAsync(ct);
        if (activeJob is null)
        {
            await WriteDeniedAsync(
                printerId,
                actorSubject,
                operation,
                "no_active_dispatch",
                ct,
                state);
            return Denied(
                PrinterActuationResultCode.PrinterBusy,
                "No active print is available for this lifecycle operation.");
        }

        QueueDispatchAttempt? attempt = state.ActiveDispatchAttemptId.HasValue
            ? await _db.QueueDispatchAttempts.SingleOrDefaultAsync(
                candidate =>
                    candidate.Id == state.ActiveDispatchAttemptId.Value &&
                    candidate.PrintJobId == activeJob.Id,
                ct)
            : null;
        if (attempt is null)
        {
            attempt = await CreateLegacyAttemptAsync(activeJob, state, actorSubject, ct);
        }

        bool outstanding = await _db.QueueDispatchOutbox
            .AsNoTracking()
            .AnyAsync(
                command =>
                    command.EventType == BackendControlCommandConsumerService.EventType &&
                    command.AttemptId == attempt.Id &&
                    (command.Status == QueueOutboxEventStatus.Pending ||
                     command.Status == QueueOutboxEventStatus.Processing ||
                     (command.Status == QueueOutboxEventStatus.DeadLettered &&
                      command.FailureCode == "manual_control_reconciliation_required")),
                ct);
        if (outstanding)
        {
            return Denied(
                PrinterActuationResultCode.FenceConflict,
                "A lifecycle command already owns this dispatch attempt.");
        }

        Guid commandId = Guid.NewGuid();
        DateTime now = DateTime.UtcNow;
        await using QueueOutboxTransactionScope transaction =
            await QueueOutboxTransactionScope.BeginAsync(_db, ct);
        var command = new QueueDispatchOutbox
        {
            Id = commandId,
            Sequence = await _sequenceAllocator.AllocateAsync(_db, ct),
            AggregateType = nameof(PrintJob),
            AggregateId = activeJob.Id,
            AggregateRowVersion = activeJob.RowVersion,
            PrinterId = printerId,
            ProjectId = activeJob.CalibrationProjectId ?? activeJob.ProjectId,
            CalibrationAttemptId = activeJob.CalibrationAttemptId,
            JobStatus = activeJob.Status.ToString(),
            JobKind = activeJob.JobKind?.ToString() ?? nameof(JobKind.Standard),
            DispatchStateRowVersion = state.RowVersion,
            AttemptId = attempt.Id,
            AttemptNumber = attempt.AttemptNumber,
            AttemptOutcome = attempt.Outcome.ToString(),
            EventType = BackendControlCommandConsumerService.EventType,
            SchemaVersion = QueueEventSchemaVersions.Current,
            PayloadJson = JsonSerializer.Serialize(new
            {
                jobId = activeJob.Id,
                printerId,
                attemptId = attempt.Id,
                backendJobId = attempt.BackendJobId,
                backendFileIdentity = attempt.BackendFileIdentity ?? attempt.BackendFileName,
                operation,
                actorSubject,
            }),
            Status = QueueOutboxEventStatus.Pending,
            CreatedAtUtc = now,
        };
        _db.QueueDispatchOutbox.Add(command);
        if (!state.PhysicalControlCommandId.HasValue)
        {
            state.PhysicalControlCommandId = commandId;
            state.PhysicalControlAttemptId = attempt.Id;
            state.PhysicalControlOperation = operation;
            state.PhysicalControlActorSubject = actorSubject;
            state.PhysicalControlStartedAtUtc = null;
            state.PhysicalControlRequiresReconciliation = false;
        }

        _ = QueueAuditWriter.Add(
            _db,
            actorSubject,
            AuditOperation(operation),
            QueueAuditOutcomes.Success,
            nameof(PrintJob),
            resourceId: activeJob.Id,
            printerId: printerId,
            printJobId: activeJob.Id,
            dispatchAttemptId: attempt.Id,
            jobRowVersion: activeJob.RowVersion,
            dispatchStateRowVersion: state.RowVersion,
            detail: new { commandId, operation, commandQueued = true });

        try
        {
            await _db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Denied(
                PrinterActuationResultCode.ConcurrencyConflict,
                "The active dispatch changed concurrently.");
        }

        return new PrinterActuationResult(
            PrinterActuationResultCode.Accepted,
            CommandId: commandId);
    }

    private async Task CompleteDirectCoreAsync(
        PrinterActuationLease lease,
        string outcome,
        string eventType,
        string? failureCode,
        bool retainBarrier,
        CancellationToken ct)
    {
        PrinterDispatchState? state = await _db.PrinterDispatchStates
            .SingleOrDefaultAsync(
                candidate =>
                    candidate.PrinterId == lease.PrinterId &&
                    candidate.PhysicalControlCommandId == lease.CommandId,
                ct);
        if (state is null)
        {
            _logger.LogWarning(
                "Physical control completion ignored because its barrier is no longer active: {CommandId}",
                lease.CommandId);
            return;
        }

        state.PhysicalControlRequiresReconciliation = retainBarrier;
        if (!retainBarrier)
        {
            ClearBarrier(state);
        }

        await using QueueOutboxTransactionScope transaction =
            await QueueOutboxTransactionScope.BeginAsync(_db, ct);
        _ = QueueAuditWriter.Add(
            _db,
            lease.ActorSubject,
            AuditOperation(lease.Operation),
            outcome,
            nameof(Printer),
            resourceId: lease.PrinterId,
            printerId: lease.PrinterId,
            dispatchAttemptId: lease.AttemptId,
            reasonCode: failureCode,
            dispatchStateRowVersion: state.RowVersion,
            detail: new { lease.CommandId, lease.Operation, barrierRetained = retainBarrier });
        await AddPrinterEventAsync(
            eventType,
            lease.CommandId,
            lease.PrinterId,
            lease.AttemptId,
            lease.Operation,
            failureCode,
            state.RowVersion,
            ct,
            failureRequiresReconciliation: retainBarrier);
        await _db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private async Task<QueueDispatchAttempt> CreateLegacyAttemptAsync(
        PrintJob job,
        PrinterDispatchState state,
        string actorSubject,
        CancellationToken ct)
    {
        int attemptNumber = await _db.QueueDispatchAttempts
            .CountAsync(candidate => candidate.PrintJobId == job.Id, ct) + 1;
        Guid attemptId = Guid.NewGuid();
        var attempt = new QueueDispatchAttempt
        {
            Id = attemptId,
            PrintJobId = job.Id,
            PrinterId = state.PrinterId,
            PrinterConfigRevision = await _db.Printers
                .Where(printer => printer.Id == state.PrinterId)
                .Select(printer => printer.ConfigurationRevision)
                .SingleAsync(ct),
            AttemptNumber = attemptNumber,
            ActorSubject = actorSubject,
            StartPathKind = "ExternalControlOwnership",
            ClaimedAtUtc = job.ActualStartTime ?? DateTime.UtcNow,
            BackendAcceptedAtUtc = job.ActualStartTime ?? DateTime.UtcNow,
            Outcome = DispatchAttemptOutcome.Accepted,
            BackendJobId = job.WasSeededFromHistory ? job.ExternalJobId : null,
            BackendFileIdentity = job.Name,
            BackendCommandId = $"legacy-{attemptId:N}",
            BackendCorrelationId = $"legacy-{attemptId:N}",
            BackendCallPhase = DispatchBackendCallPhase.PostAccept,
            JobRowVersionAtClaim = job.RowVersion,
            DispatchStateRowVersionAtClaim = state.RowVersion,
            UpdatedAtUtc = DateTime.UtcNow,
        };
        _db.QueueDispatchAttempts.Add(attempt);
        state.ActiveJobId = job.Id;
        state.ActiveDispatchAttemptId = attempt.Id;
        return attempt;
    }

    private async Task AddPrinterEventAsync(
        string eventType,
        Guid commandId,
        Guid printerId,
        Guid? attemptId,
        string operation,
        string? failureCode,
        byte[]? dispatchStateRowVersion,
        CancellationToken ct,
        bool? failureRequiresReconciliation = null)
    {
        QueueDispatchAttempt? attempt = attemptId.HasValue
            ? _db.QueueDispatchAttempts.Local.FirstOrDefault(
                candidate => candidate.Id == attemptId.Value)
                ?? await _db.QueueDispatchAttempts
                    .AsNoTracking()
                    .FirstOrDefaultAsync(
                        candidate => candidate.Id == attemptId.Value,
                        ct)
            : null;
        Guid? calibrationAttemptId = attempt?.PrintJobId is Guid printJobId
            ? _db.PrintJobs.Local.FirstOrDefault(job => job.Id == printJobId)
                ?.CalibrationAttemptId
                ?? await _db.PrintJobs
                    .AsNoTracking()
                    .Where(job => job.Id == printJobId)
                    .Select(job => job.CalibrationAttemptId)
                    .FirstOrDefaultAsync(ct)
            : null;
        _db.QueueDispatchOutbox.Add(new QueueDispatchOutbox
        {
            Id = Guid.NewGuid(),
            Sequence = await _sequenceAllocator.AllocateAsync(_db, ct),
            AggregateType = nameof(Printer),
            AggregateId = printerId,
            PrinterId = printerId,
            DispatchStateRowVersion = dispatchStateRowVersion,
            AttemptId = attemptId,
            AttemptNumber = attempt?.AttemptNumber,
            AttemptOutcome = attempt?.Outcome.ToString(),
            CalibrationAttemptId = calibrationAttemptId,
            EventType = eventType,
            SchemaVersion = QueueEventSchemaVersions.Current,
            FailureCode = failureCode,
            FailureRetryable = false,
            FailureRequiresReconciliation = failureRequiresReconciliation,
            PayloadJson = JsonSerializer.Serialize(new
            {
                commandId,
                printerId,
                attemptId,
                operation,
                failureCode,
            }),
            Status = QueueOutboxEventStatus.Pending,
            CreatedAtUtc = DateTime.UtcNow,
        });
    }

    private async Task WriteDeniedAsync(
        Guid printerId,
        string actorSubject,
        string operation,
        string reasonCode,
        CancellationToken ct,
        PrinterDispatchState? state = null)
    {
        _ = QueueAuditWriter.Add(
            _db,
            actorSubject,
            AuditOperation(operation),
            QueueAuditOutcomes.Denied,
            nameof(Printer),
            resourceId: printerId,
            printerId: printerId,
            dispatchAttemptId: state?.ActiveDispatchAttemptId,
            reasonCode: reasonCode,
            dispatchStateRowVersion: state?.RowVersion,
            detail: new { operation });
        await _db.SaveChangesAsync(ct);
    }

    private static void ClearBarrier(PrinterDispatchState state)
    {
        state.PhysicalControlCommandId = null;
        state.PhysicalControlAttemptId = null;
        state.PhysicalControlOperation = null;
        state.PhysicalControlActorSubject = null;
        state.PhysicalControlStartedAtUtc = null;
        state.PhysicalControlRequiresReconciliation = false;
    }

    private static PrinterActuationResult Denied(
        PrinterActuationResultCode code,
        string detail) =>
        new(code, Detail: detail);

    private static string AuditOperation(string operation) =>
        operation switch
        {
            "pause" => QueueAuditOperations.JobPause,
            "resume" => QueueAuditOperations.JobResume,
            "abort" => QueueAuditOperations.JobAbort,
            "cancel" or "emergencystop" => QueueAuditOperations.JobCancel,
            "printer_file_delete" => QueueAuditOperations.PrinterFileDelete,
            _ => QueueAuditOperations.PhysicalControl,
        };
}
