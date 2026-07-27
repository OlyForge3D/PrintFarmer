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
    private static readonly JsonSerializerOptions PayloadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private const int MaxAttempts = 10;

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

    private async Task RecoverStaleLeasesAsync(CancellationToken ct)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        DateTime cutoff = DateTime.UtcNow - StaleLeaseAge;
        List<QueueDispatchOutbox> stale = await db.QueueDispatchOutbox
            .Where(command =>
                command.EventType == EventType &&
                command.Status == QueueOutboxEventStatus.Processing &&
                command.LastAttemptedAtUtc < cutoff)
            .ToListAsync(ct);
        foreach (QueueDispatchOutbox command in stale)
        {
            command.Status = QueueOutboxEventStatus.Pending;
            command.RetryAfterUtc = DateTime.UtcNow;
            command.LastError = "Recovered after a stale control-command lease.";
        }

        if (stale.Count > 0)
        {
            await db.SaveChangesAsync(ct);
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
        int attemptCount;
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
                (payload.Operation != "cancel" && payload.Operation != "abort"))
            {
                command.Status = QueueOutboxEventStatus.DeadLettered;
                command.FailureCode = "invalid_control_command";
                command.LastError = "Control command identifiers, attempt, or operation are invalid.";
                command.CompletedAtUtc = DateTime.UtcNow;
                await leaseDb.SaveChangesAsync(ct);
                return;
            }

            PrinterDispatchState? dispatchState = await leaseDb.PrinterDispatchStates
                .AsNoTracking()
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

            command.Status = QueueOutboxEventStatus.Processing;
            command.AttemptCount++;
            command.LastAttemptedAtUtc = DateTime.UtcNow;
            command.LastError = null;
            attemptCount = command.AttemptCount;
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
            bool accepted = await printers.CancelPrintAsync(payload.PrinterId, ct);
            if (!accepted)
            {
                throw new InvalidOperationException("The backend did not confirm the cancel command.");
            }

            await ApplyAcceptedAsync(commandId, payload, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            await ApplyFailureAsync(commandId, attemptCount, exception, ct);
        }
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
        if (command is null || job is null || dispatchState is null)
        {
            return;
        }

        if (payload.AttemptId.HasValue &&
            dispatchState.ActiveDispatchAttemptId != payload.AttemptId)
        {
            command.Status = QueueOutboxEventStatus.DeadLettered;
            command.FailureCode = "control_attempt_fence_conflict";
            command.LastError = "The active dispatch attempt changed before control completion.";
            command.CompletedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return;
        }

        DateTime now = DateTime.UtcNow;
        PrintJobStatus fromStatus = job.Status;
        bool abort = string.Equals(payload.Operation, "abort", StringComparison.Ordinal);
        job.Status = abort ? PrintJobStatus.Queued : PrintJobStatus.Cancelled;
        job.ActualStartTime = abort ? null : job.ActualStartTime;
        job.ActualEndTime = abort ? null : now;
        job.UpdatedAt = now;

        dispatchState.ActiveJobId = null;
        dispatchState.ActiveDispatchAttemptId = null;
        dispatchState.QueueRevision++;
        ClearAcknowledgement(dispatchState);

        if (payload.AttemptId.HasValue)
        {
            QueueDispatchAttempt? attempt = await db.QueueDispatchAttempts
                .FirstOrDefaultAsync(candidate => candidate.Id == payload.AttemptId.Value, ct);
            if (attempt is not null)
            {
                attempt.BackendCallPhase = DispatchBackendCallPhase.Terminal;
                attempt.TerminalAtUtc = now;
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
            Notes = abort ? "Durable hardware abort accepted" : "Durable hardware cancel accepted",
        });
        _ = QueueAuditWriter.Add(
            db,
            payload.ActorSubject,
            abort ? QueueAuditOperations.JobAbort : QueueAuditOperations.JobCancel,
            QueueAuditOutcomes.Success,
            nameof(PrintJob),
            resourceId: job.Id,
            printerId: payload.PrinterId,
            printJobId: job.Id,
            dispatchAttemptId: payload.AttemptId,
            jobRowVersion: job.RowVersion,
            dispatchStateRowVersion: dispatchState.RowVersion,
            detail: new { commandId });
        string lifecycleEventType = abort
            ? QueueLifecycleEventWriter.EventTypeJobAborted
            : QueueLifecycleEventWriter.EventTypeJobCancelled;
        await QueueLifecycleEventWriter.AddEventAsync(
            db,
            allocator,
            lifecycleEventType,
            job.Id,
            payload.PrinterId,
            payload.AttemptId,
            job.RowVersion,
            abort ? null : "job_cancelled",
            QueueLifecycleEventWriter.BuildTerminalPayload(
                job.Id,
                payload.PrinterId,
                payload.AttemptId,
                job.Status.ToString(),
                job.JobKind?.ToString() ?? nameof(JobKind.Standard),
                abort ? null : "job_cancelled"),
            ct);
        await db.SaveChangesAsync(ct);
    }

    private async Task ApplyFailureAsync(
        Guid commandId,
        int attemptCount,
        Exception exception,
        CancellationToken ct)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        QueueDispatchOutbox? command = await db.QueueDispatchOutbox
            .FirstOrDefaultAsync(candidate => candidate.Id == commandId, ct);
        if (command is null)
        {
            return;
        }

        command.LastError = exception.Message[..Math.Min(exception.Message.Length, 2047)];
        command.FailureCode = "backend_control_unconfirmed";
        if (attemptCount >= MaxAttempts)
        {
            command.Status = QueueOutboxEventStatus.DeadLettered;
            command.CompletedAtUtc = DateTime.UtcNow;
        }
        else
        {
            command.Status = QueueOutboxEventStatus.Pending;
            command.RetryAfterUtc = DateTime.UtcNow +
                TimeSpan.FromSeconds(5 * Math.Pow(2, attemptCount - 1));
        }

        await db.SaveChangesAsync(ct);
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
        string Operation,
        string ActorSubject);
}
