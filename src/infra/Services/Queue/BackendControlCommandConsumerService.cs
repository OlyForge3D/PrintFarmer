// <copyright file="BackendControlCommandConsumerService.cs" company="PlaceholderCompany">
// SPDX-License-Identifier: AGPL-3.0-only
// </copyright>

using System.Text.Json;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.Queue;

/// <summary>
/// Executes durable cancel/abort hardware commands and applies their lifecycle transition
/// only after the backend confirms the idempotent cancel operation.
/// </summary>
public sealed class BackendControlCommandConsumerService(
    IServiceScopeFactory scopeFactory,
    ILogger<BackendControlCommandConsumerService> logger) : BackgroundService
{
    public const string EventType = "PrintFarmer.Queue.BackendControlCommand.v1";

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StaleLeaseAge = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ManualReviewAge = TimeSpan.FromHours(24);
    private static readonly TimeSpan FenceConflictRetryDelay = TimeSpan.FromSeconds(5);
    private const int MaxFenceConflictAttempts = 120;
    private static readonly JsonSerializerOptions PayloadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RecoverStaleLeasesAsync(stoppingToken);
                await ProcessPendingAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Backend control command scan failed.");
            }

            await Task.Delay(PollInterval, stoppingToken);
        }
    }

    internal async Task RecoverStaleLeasesAsync(CancellationToken ct)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        DateTime cutoff = DateTime.UtcNow - StaleLeaseAge;
        List<Guid> stale = await db.QueueDispatchOutbox
            .AsNoTracking()
            .Where(command =>
                command.EventType == EventType &&
                command.Status == QueueOutboxEventStatus.Processing &&
                command.LastAttemptedAtUtc < cutoff)
            .Select(command => command.Id)
            .ToListAsync(ct);

        foreach (Guid commandId in stale)
        {
            await ReconcileProcessingCommandAsync(commandId, ct);
        }
    }

    internal async Task ProcessPendingAsync(CancellationToken ct)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        DateTime now = DateTime.UtcNow;
        List<Guid> commandIds = await db.QueueDispatchOutbox
            .AsNoTracking()
            .Where(command =>
                command.EventType == EventType &&
                command.Status == QueueOutboxEventStatus.Pending &&
                (command.RetryAfterUtc == null || command.RetryAfterUtc <= now))
            .OrderBy(command => command.Sequence)
            .Select(command => command.Id)
            .Take(10)
            .ToListAsync(ct);

        foreach (Guid commandId in commandIds)
        {
            await ProcessOneAsync(commandId, ct);
        }
    }

    private async Task ProcessOneAsync(Guid commandId, CancellationToken ct)
    {
        BackendControlPayload payload;
        await using (AsyncServiceScope leaseScope = scopeFactory.CreateAsyncScope())
        {
            AppDbContext leaseDb = leaseScope.ServiceProvider.GetRequiredService<AppDbContext>();
            QueueDispatchOutbox? command = await leaseDb.QueueDispatchOutbox
                .FirstOrDefaultAsync(candidate => candidate.Id == commandId, ct);
            if (command is null || command.Status != QueueOutboxEventStatus.Pending)
            {
                return;
            }

            try
            {
                payload = JsonSerializer.Deserialize<BackendControlPayload>(
                    command.PayloadJson,
                    PayloadOptions)
                    ?? throw new JsonException("Control command payload was empty.");
            }
            catch (JsonException exception)
            {
                command.Status = QueueOutboxEventStatus.DeadLettered;
                command.FailureCode = "invalid_control_command";
                command.LastError = exception.Message;
                command.CompletedAtUtc = DateTime.UtcNow;
                await leaseDb.SaveChangesAsync(ct);
                return;
            }

            if (payload.JobId == Guid.Empty ||
                payload.PrinterId == Guid.Empty ||
                !payload.AttemptId.HasValue ||
                !TryParseOperation(payload.Operation, out _))
            {
                command.Status = QueueOutboxEventStatus.DeadLettered;
                command.FailureCode = "invalid_control_command";
                command.LastError = "Control command identifiers, attempt, or operation are invalid.";
                command.CompletedAtUtc = DateTime.UtcNow;
                await leaseDb.SaveChangesAsync(ct);
                return;
            }

            PrinterDispatchState? dispatchState = await leaseDb.PrinterDispatchStates
                .SingleOrDefaultAsync(
                    state => state.PrinterId == payload.PrinterId,
                    ct);
            if (dispatchState?.ActiveJobId != payload.JobId ||
                dispatchState.ActiveDispatchAttemptId != payload.AttemptId)
            {
                command.Status = QueueOutboxEventStatus.DeadLettered;
                command.FailureCode = "control_attempt_fence_conflict";
                command.LastError = "The active dispatch attempt changed before hardware control.";
                command.CompletedAtUtc = DateTime.UtcNow;
                await leaseDb.SaveChangesAsync(ct);
                return;
            }

            if (dispatchState.PhysicalControlCommandId.HasValue &&
                dispatchState.PhysicalControlCommandId != command.Id)
            {
                await DeferFenceConflictAsync(
                    leaseDb,
                    leaseScope.ServiceProvider.GetRequiredService<IDbOutboxSequenceAllocator>(),
                    command,
                    payload,
                    ct);
                return;
            }

            dispatchState.PhysicalControlCommandId = command.Id;
            dispatchState.PhysicalControlAttemptId = payload.AttemptId;
            dispatchState.PhysicalControlOperation = payload.Operation;
            dispatchState.PhysicalControlActorSubject = payload.ActorSubject;
            dispatchState.PhysicalControlStartedAtUtc = DateTime.UtcNow;
            dispatchState.PhysicalControlRequiresReconciliation = false;
            command.Status = QueueOutboxEventStatus.Processing;
            command.AttemptCount++;
            command.LastAttemptedAtUtc = DateTime.UtcNow;
            command.LastError = null;
            try
            {
                await leaseDb.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                return;
            }
        }

        try
        {
            await using AsyncServiceScope callScope = scopeFactory.CreateAsyncScope();
            IPrintersService printers = callScope.ServiceProvider.GetRequiredService<IPrintersService>();
            if (!TryParseOperation(payload.Operation, out BackendControlOperation operation))
            {
                await ApplyRejectedAsync(
                    commandId,
                    payload,
                    "invalid_control_command",
                    "The lifecycle command operation is invalid.",
                    ct);
                return;
            }

            BackendControlOutcome outcome = await printers.ExecuteControlAsync(
                payload.PrinterId,
                operation,
                ct);
            switch (outcome.Status)
            {
                case BackendControlStatus.Accepted:
                    await ApplyAcceptedAsync(commandId, payload, ct);
                    break;
                case BackendControlStatus.Rejected:
                    await ApplyRejectedAsync(
                        commandId,
                        payload,
                        outcome.ErrorCode ?? "backend_control_rejected",
                        outcome.ErrorDetail ?? "The backend rejected the lifecycle command.",
                        ct);
                    break;
                default:
                    string unknownDetail = outcome.ErrorDetail ??
                        "The backend lifecycle command requires reconciliation.";
                    await ApplyUnknownAsync(
                        commandId,
                        unknownDetail,
                        ct);
                    break;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            await ApplyUnknownAsync(commandId, exception.Message, ct);
        }
    }

    private static async Task DeferFenceConflictAsync(
        AppDbContext db,
        IDbOutboxSequenceAllocator allocator,
        QueueDispatchOutbox command,
        BackendControlPayload payload,
        CancellationToken ct)
    {
        DateTime now = DateTime.UtcNow;
        command.AttemptCount++;
        command.LastAttemptedAtUtc = now;
        command.LastError = "Another physical command owns the printer barrier.";
        if (command.AttemptCount < MaxFenceConflictAttempts &&
            command.CreatedAtUtc > now - ManualReviewAge)
        {
            command.Status = QueueOutboxEventStatus.Pending;
            command.FailureCode = "physical_control_fence_conflict";
            command.RetryAfterUtc = now + FenceConflictRetryDelay;
            await db.SaveChangesAsync(ct);
            return;
        }

        command.Status = QueueOutboxEventStatus.DeadLettered;
        command.FailureCode = "manual_control_reconciliation_required";
        command.LastError =
            "The cancellation could not acquire the printer barrier; manual reconciliation is required.";
        command.CompletedAtUtc = now;
        command.RetryAfterUtc = null;
        PrintJob? job = await db.PrintJobs
            .FirstOrDefaultAsync(candidate => candidate.Id == payload.JobId, ct);
        if (job is not null)
        {
            await QueueLifecycleEventWriter.AddEventAsync(
                db,
                allocator,
                QueueLifecycleEventWriter.EventTypeControlRejected,
                job.Id,
                payload.PrinterId,
                payload.AttemptId,
                job.RowVersion,
                command.FailureCode,
                JsonSerializer.Serialize(new
                {
                    jobId = job.Id,
                    printerId = payload.PrinterId,
                    attemptId = payload.AttemptId,
                    operation = payload.Operation,
                    failureCode = command.FailureCode,
                }),
                failureRetryable: false,
                failureRequiresReconciliation: true,
                ct: ct);
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task ApplyAcceptedAsync(
        Guid commandId,
        BackendControlPayload payload,
        CancellationToken ct)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        IDbOutboxSequenceAllocator allocator =
            scope.ServiceProvider.GetRequiredService<IDbOutboxSequenceAllocator>();
        QueueDispatchOutbox? command = await db.QueueDispatchOutbox
            .FirstOrDefaultAsync(candidate => candidate.Id == commandId, ct);
        PrintJob? job = await db.PrintJobs
            .FirstOrDefaultAsync(candidate => candidate.Id == payload.JobId, ct);
        PrinterDispatchState? dispatchState = await db.PrinterDispatchStates
            .FirstOrDefaultAsync(candidate => candidate.PrinterId == payload.PrinterId, ct);
        if (command is null ||
            command.Status != QueueOutboxEventStatus.Processing ||
            job is null ||
            dispatchState is null)
        {
            return;
        }

        if (!payload.AttemptId.HasValue ||
            dispatchState.ActiveJobId != payload.JobId ||
            dispatchState.ActiveDispatchAttemptId != payload.AttemptId ||
            dispatchState.PhysicalControlCommandId != commandId ||
            dispatchState.PhysicalControlAttemptId != payload.AttemptId)
        {
            command.Status = QueueOutboxEventStatus.DeadLettered;
            command.FailureCode = "control_attempt_fence_conflict";
            command.LastError = "The active dispatch attempt changed before control completion.";
            command.CompletedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return;
        }

        await using QueueOutboxTransactionScope transaction =
            await QueueOutboxTransactionScope.BeginAsync(db, ct);
        DateTime now = DateTime.UtcNow;
        PrintJobStatus fromStatus = job.Status;
        bool pause = string.Equals(payload.Operation, "pause", StringComparison.Ordinal);
        bool resume = string.Equals(payload.Operation, "resume", StringComparison.Ordinal);
        bool abort = string.Equals(payload.Operation, "abort", StringComparison.Ordinal);
        bool terminalControl = !pause && !resume;
        job.Status = payload.Operation switch
        {
            "pause" => PrintJobStatus.Paused,
            "resume" => PrintJobStatus.Printing,
            "abort" => PrintJobStatus.Queued,
            _ => PrintJobStatus.Cancelled,
        };
        job.ActualStartTime = abort ? null : job.ActualStartTime;
        job.ActualEndTime = terminalControl && !abort ? now : null;
        job.UpdatedAt = now;

        if (terminalControl)
        {
            dispatchState.ActiveJobId = null;
            dispatchState.ActiveDispatchAttemptId = null;
            dispatchState.QueueRevision++;
            ClearAcknowledgement(dispatchState);
        }

        ClearPhysicalBarrier(dispatchState);

        if (payload.AttemptId.HasValue)
        {
            QueueDispatchAttempt? attempt = await db.QueueDispatchAttempts
                .FirstOrDefaultAsync(candidate => candidate.Id == payload.AttemptId.Value, ct);
            if (attempt is not null)
            {
                if (terminalControl)
                {
                    attempt.BackendCallPhase = DispatchBackendCallPhase.Terminal;
                    attempt.TerminalAtUtc = now;
                }

                attempt.RequiresReconciliation = false;
                attempt.UpdatedAtUtc = now;
            }
        }

        command.Status = QueueOutboxEventStatus.Published;
        command.CompletedAtUtc = now;
        command.LastError = null;
        command.FailureCode = null;

        db.JobStateHistories.Add(new JobStateHistory
        {
            Id = Guid.NewGuid(),
            JobId = job.Id,
            FromState = fromStatus.ToString(),
            ToState = job.Status.ToString(),
            TransitionedAtUtc = now,
            CreatedAt = now,
            Notes = $"Durable hardware {payload.Operation} accepted",
        });
        string auditOperation = payload.Operation switch
        {
            "pause" => QueueAuditOperations.JobPause,
            "resume" => QueueAuditOperations.JobResume,
            "abort" => QueueAuditOperations.JobAbort,
            _ => QueueAuditOperations.JobCancel,
        };
        _ = QueueAuditWriter.Add(
            db,
            payload.ActorSubject,
            auditOperation,
            QueueAuditOutcomes.Success,
            nameof(PrintJob),
            resourceId: job.Id,
            printerId: payload.PrinterId,
            printJobId: job.Id,
            dispatchAttemptId: payload.AttemptId,
            jobRowVersion: job.RowVersion,
            dispatchStateRowVersion: dispatchState.RowVersion,
            detail: new { commandId });
        string lifecycleEventType = payload.Operation switch
        {
            "pause" => QueueLifecycleEventWriter.EventTypeJobPaused,
            "resume" => QueueLifecycleEventWriter.EventTypeJobResumed,
            "abort" => QueueLifecycleEventWriter.EventTypeJobAborted,
            _ => QueueLifecycleEventWriter.EventTypeJobCancelled,
        };
        await QueueLifecycleEventWriter.AddEventAsync(
            db,
            allocator,
            lifecycleEventType,
            job.Id,
            payload.PrinterId,
            payload.AttemptId,
            job.RowVersion,
            payload.Operation == "cancel" ? "job_cancelled" : null,
            QueueLifecycleEventWriter.BuildTerminalPayload(
                job.Id,
                payload.PrinterId,
                payload.AttemptId,
                job.Status.ToString(),
                job.JobKind?.ToString() ?? nameof(JobKind.Standard),
                payload.Operation == "cancel" ? "job_cancelled" : null),
            ct);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private async Task ApplyRejectedAsync(
        Guid commandId,
        BackendControlPayload payload,
        string errorCode,
        string errorDetail,
        CancellationToken ct)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        IDbOutboxSequenceAllocator allocator =
            scope.ServiceProvider.GetRequiredService<IDbOutboxSequenceAllocator>();
        QueueDispatchOutbox? command = await db.QueueDispatchOutbox
            .FirstOrDefaultAsync(candidate => candidate.Id == commandId, ct);
        if (command is null || command.Status != QueueOutboxEventStatus.Processing)
        {
            return;
        }

        PrinterDispatchState? dispatchState = await db.PrinterDispatchStates
            .FirstOrDefaultAsync(candidate => candidate.PrinterId == payload.PrinterId, ct);
        if (dispatchState?.PhysicalControlCommandId == commandId &&
            dispatchState.ActiveDispatchAttemptId == payload.AttemptId)
        {
            ClearPhysicalBarrier(dispatchState);
        }

        await using QueueOutboxTransactionScope transaction =
            await QueueOutboxTransactionScope.BeginAsync(db, ct);
        command.LastError = errorDetail[..Math.Min(errorDetail.Length, 2047)];
        command.FailureCode = errorCode;
        command.Status = QueueOutboxEventStatus.DeadLettered;
        command.CompletedAtUtc = DateTime.UtcNow;
        _ = QueueAuditWriter.Add(
            db,
            payload.ActorSubject,
            QueueAuditOperation(payload.Operation),
            QueueAuditOutcomes.Denied,
            nameof(PrintJob),
            resourceId: payload.JobId,
            printerId: payload.PrinterId,
            printJobId: payload.JobId,
            dispatchAttemptId: payload.AttemptId,
            reasonCode: errorCode,
            detail: new { commandId });
        PrintJob? job = await db.PrintJobs
            .FirstOrDefaultAsync(candidate => candidate.Id == payload.JobId, ct);
        if (job is not null)
        {
            await QueueLifecycleEventWriter.AddEventAsync(
                db,
                allocator,
                QueueLifecycleEventWriter.EventTypeControlRejected,
                job.Id,
                payload.PrinterId,
                payload.AttemptId,
                job.RowVersion,
                errorCode,
                JsonSerializer.Serialize(new
                {
                    jobId = job.Id,
                    printerId = payload.PrinterId,
                    attemptId = payload.AttemptId,
                    operation = payload.Operation,
                    failureCode = errorCode,
                }),
                failureRetryable: false,
                failureRequiresReconciliation: false,
                ct: ct);
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private async Task ApplyUnknownAsync(
        Guid commandId,
        string errorDetail,
        CancellationToken ct)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        IDbOutboxSequenceAllocator allocator =
            scope.ServiceProvider.GetRequiredService<IDbOutboxSequenceAllocator>();
        QueueDispatchOutbox? command = await db.QueueDispatchOutbox
            .FirstOrDefaultAsync(candidate => candidate.Id == commandId, ct);
        if (command is null || command.Status != QueueOutboxEventStatus.Processing)
        {
            return;
        }

        DateTime now = DateTime.UtcNow;
        bool firstUnknown = command.FailureCode != "backend_control_unknown";
        PrinterDispatchState? dispatchState = await db.PrinterDispatchStates
            .FirstOrDefaultAsync(candidate => candidate.PrinterId == command.PrinterId, ct);
        if (dispatchState?.PhysicalControlCommandId == commandId)
        {
            dispatchState.PhysicalControlRequiresReconciliation = true;
        }

        command.LastAttemptedAtUtc = now;
        command.RetryAfterUtc = null;
        if (command.CreatedAtUtc <= now - ManualReviewAge)
        {
            command.Status = QueueOutboxEventStatus.DeadLettered;
            command.FailureCode = "manual_control_reconciliation_required";
            command.LastError =
                "Backend control outcome remained indeterminate for 24 hours. " +
                "The dispatch lease remains fenced for manual review.";
            command.CompletedAtUtc = now;
            logger.LogError(
                "Backend control command {CommandId} requires manual reconciliation; " +
                "the dispatch lease remains fenced",
                commandId);
        }
        else
        {
            command.Status = QueueOutboxEventStatus.Processing;
            command.FailureCode = "backend_control_unknown";
            command.LastError = errorDetail[..Math.Min(errorDetail.Length, 2047)];
        }

        await using QueueOutboxTransactionScope transaction =
            await QueueOutboxTransactionScope.BeginAsync(db, ct);
        if (firstUnknown)
        {
            PrintJob? job = await db.PrintJobs
                .FirstOrDefaultAsync(candidate => candidate.Id == command.AggregateId, ct);
            if (job is not null)
            {
                await QueueLifecycleEventWriter.AddEventAsync(
                    db,
                    allocator,
                    QueueLifecycleEventWriter.EventTypeControlUnknown,
                    job.Id,
                    command.PrinterId,
                    command.AttemptId,
                    job.RowVersion,
                    "backend_control_unknown",
                    JsonSerializer.Serialize(new
                    {
                        jobId = job.Id,
                        printerId = command.PrinterId,
                        attemptId = command.AttemptId,
                        failureCode = "backend_control_unknown",
                    }),
                    failureRetryable: false,
                    failureRequiresReconciliation: true,
                    ct: ct);
            }
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private async Task ReconcileProcessingCommandAsync(Guid commandId, CancellationToken ct)
    {
        BackendControlPayload payload;
        await using (AsyncServiceScope scope = scopeFactory.CreateAsyncScope())
        {
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            QueueDispatchOutbox? command = await db.QueueDispatchOutbox
                .AsNoTracking()
                .FirstOrDefaultAsync(candidate => candidate.Id == commandId, ct);
            if (command is null || command.Status != QueueOutboxEventStatus.Processing)
            {
                return;
            }

            try
            {
                payload = JsonSerializer.Deserialize<BackendControlPayload>(
                    command.PayloadJson,
                    PayloadOptions)
                    ?? throw new JsonException("Control command payload was empty.");
            }
            catch (JsonException exception)
            {
                command = await db.QueueDispatchOutbox
                    .FirstAsync(candidate => candidate.Id == commandId, ct);
                command.Status = QueueOutboxEventStatus.DeadLettered;
                command.FailureCode = "invalid_control_command";
                command.LastError = exception.Message;
                command.CompletedAtUtc = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
                return;
            }

            PrinterDispatchState? state = await db.PrinterDispatchStates
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    candidate => candidate.PrinterId == payload.PrinterId,
                    ct);
            if (state?.ActiveJobId != payload.JobId ||
                state.ActiveDispatchAttemptId != payload.AttemptId ||
                state.PhysicalControlCommandId != commandId ||
                state.PhysicalControlAttemptId != payload.AttemptId)
            {
                await ApplyRejectedAsync(
                    commandId,
                    payload,
                    "control_attempt_fence_conflict",
                    "The active dispatch ownership changed before reconciliation.",
                    ct);
                return;
            }
        }

        if (!TryParseOperation(payload.Operation, out BackendControlOperation operation))
        {
            await ApplyRejectedAsync(
                commandId,
                payload,
                "invalid_control_command",
                "The lifecycle command operation is invalid.",
                ct);
            return;
        }

        await using AsyncServiceScope printerScope = scopeFactory.CreateAsyncScope();
        IPrintersService printers =
            printerScope.ServiceProvider.GetRequiredService<IPrintersService>();
        PrinterStatusDto status;
        try
        {
            status = await printers.GetStatusDtoAsync(payload.PrinterId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await ApplyUnknownAsync(commandId, ex.Message, ct);
            return;
        }

        bool identityMatches = MatchesBackendIdentity(
            payload.BackendFileIdentity ?? payload.BackendIdentity,
            status);
        if (operation == BackendControlOperation.Pause &&
            IsPausedState(status.State) &&
            identityMatches)
        {
            await ApplyAcceptedAsync(commandId, payload, ct);
            return;
        }

        if (operation == BackendControlOperation.Resume &&
            IsPrintingState(status.State) &&
            identityMatches)
        {
            await ApplyAcceptedAsync(commandId, payload, ct);
            return;
        }

        if (operation is BackendControlOperation.Cancel or
            BackendControlOperation.Abort or
            BackendControlOperation.EmergencyStop &&
            !IsActiveState(status.State) &&
            !string.IsNullOrWhiteSpace(payload.BackendJobId))
        {
            try
            {
                HistoryJob history = await printers.GetHistoryJobAsync(
                    payload.PrinterId,
                    payload.BackendJobId,
                    ct);
                if (IsCancelledHistoryState(history.Status))
                {
                    await ApplyAcceptedAsync(commandId, payload, ct);
                    return;
                }

                if (IsCompletedHistoryState(history.Status))
                {
                    await ApplyRejectedAsync(
                        commandId,
                        payload,
                        "control_not_applied",
                        "Exact backend history shows the print completed instead of being cancelled.",
                        ct);
                    return;
                }
            }
            catch (KeyNotFoundException)
            {
                // Exact absence is not proof that a response-lost control command ran.
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await ApplyUnknownAsync(commandId, ex.Message, ct);
                return;
            }
        }

        await ApplyUnknownAsync(
            commandId,
            "Authoritative backend state does not yet prove the lifecycle command outcome.",
            ct);
    }

    private static bool TryParseOperation(
        string value,
        out BackendControlOperation operation) =>
        Enum.TryParse(value, ignoreCase: true, out operation) &&
        Enum.IsDefined(operation);

    private static bool MatchesBackendIdentity(
        string? expected,
        PrinterStatusDto status)
    {
        if (string.IsNullOrWhiteSpace(expected))
        {
            return false;
        }

        string? actual = status.FileName ?? status.JobName;
        return !string.IsNullOrWhiteSpace(actual) &&
            string.Equals(
                Path.GetFileName(actual),
                Path.GetFileName(expected),
                StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPausedState(string? state) =>
        string.Equals(state?.Trim(), "paused", StringComparison.OrdinalIgnoreCase);

    private static bool IsPrintingState(string? state) =>
        state?.Trim() is { } value &&
        (value.Equals("printing", StringComparison.OrdinalIgnoreCase) ||
         value.Equals("resuming", StringComparison.OrdinalIgnoreCase));

    private static bool IsActiveState(string? state) =>
        IsPrintingState(state) ||
        IsPausedState(state) ||
        string.Equals(state?.Trim(), "starting", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(state?.Trim(), "heating", StringComparison.OrdinalIgnoreCase);

    private static bool IsCancelledHistoryState(string? state) =>
        state?.Trim() is { } value &&
        (value.Contains("cancel", StringComparison.OrdinalIgnoreCase) ||
         value.Contains("abort", StringComparison.OrdinalIgnoreCase) ||
         value.Contains("fail", StringComparison.OrdinalIgnoreCase) ||
         value.Contains("error", StringComparison.OrdinalIgnoreCase));

    private static bool IsCompletedHistoryState(string? state) =>
        state?.Trim() is { } value &&
        (value.Equals("completed", StringComparison.OrdinalIgnoreCase) ||
         value.Equals("complete", StringComparison.OrdinalIgnoreCase) ||
         value.Equals("finished", StringComparison.OrdinalIgnoreCase));

    private static string QueueAuditOperation(string operation) =>
        operation switch
        {
            "pause" => QueueAuditOperations.JobPause,
            "resume" => QueueAuditOperations.JobResume,
            "abort" => QueueAuditOperations.JobAbort,
            _ => QueueAuditOperations.JobCancel,
        };

    private static void ClearPhysicalBarrier(PrinterDispatchState state)
    {
        state.PhysicalControlCommandId = null;
        state.PhysicalControlAttemptId = null;
        state.PhysicalControlOperation = null;
        state.PhysicalControlActorSubject = null;
        state.PhysicalControlStartedAtUtc = null;
        state.PhysicalControlRequiresReconciliation = false;
    }

    private static void ClearAcknowledgement(PrinterDispatchState state)
    {
        state.AcknowledgedJobId = null;
        state.AcknowledgedAtUtc = null;
        state.AcknowledgedBySubject = null;
        state.AcknowledgementIdempotencyKey = null;
        state.AcknowledgementExpiresAtUtc = null;
        state.AcknowledgedJobRowVersion = null;
        state.AcknowledgedQueueRevision = null;
        state.AcknowledgedPrinterConfigRevision = null;
    }

    private sealed record BackendControlPayload(
        Guid JobId,
        Guid PrinterId,
        Guid? AttemptId,
        string? BackendJobId,
        string? BackendFileIdentity,
        string? BackendIdentity,
        string Operation,
        string ActorSubject);
}
