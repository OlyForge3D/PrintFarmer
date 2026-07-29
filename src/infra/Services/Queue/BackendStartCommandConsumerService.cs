using System.Text.Json;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.Queue;

/// <summary>
/// Dedicated durable hosted consumer for <c>BackendStartCommand.v1</c> outbox events.
///
/// Each event corresponds to a persisted bed-clear acknowledgement whose execution must
/// be crash-safe. The consumer:
/// <list type="bullet">
///   <item>Atomically acquires a row lease (sets <see cref="QueueOutboxEventStatus.Processing"/>)
///         before any network I/O so concurrent instances cannot double-execute.</item>
///   <item>Awaits <c>DispatchJobWithAckAsync</c> synchronously within the processor loop
///         (not fire-and-forget) so a process crash during upload leaves the event in
///         <see cref="QueueOutboxEventStatus.Processing"/>, recovered on restart.</item>
///   <item>Applies exponential back-off retries up to <see cref="MaxAttempts"/> before
///         dead-lettering.</item>
///   <item>On every poll, resets stale <see cref="QueueOutboxEventStatus.Processing"/> events
///         older than <see cref="StaleLeaseAge"/> back to
///         <see cref="QueueOutboxEventStatus.Pending"/> for re-execution.</item>
/// </list>
///
/// The <see cref="QueueOutboxPublisherService"/> is SignalR-hints only and skips
/// <c>BackendStartCommand.v1</c> events entirely — this service owns them end-to-end.
/// </summary>
public sealed class BackendStartCommandConsumerService(
    IServiceScopeFactory scopeFactory,
    ILogger<BackendStartCommandConsumerService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StaleLeaseAge = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RetryBackoffBase = TimeSpan.FromSeconds(15);
    private static readonly JsonSerializerOptions PayloadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private const int MaxAttempts = 10;
    private const string CommandEventType = BedClearAcknowledgementService.BackendStartCommandEventType;

    /// <summary>
    /// Failure code stamped on rows whose backend outcome could not be determined.
    /// Such rows are excluded from stale-lease recovery so they are never retried blindly.
    /// </summary>
    private const string UnknownOutcomeFailureCode = "backend_outcome_unknown";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("[BackendStartConsumer] Durable backend-start command consumer started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessPendingCommandsAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[BackendStartConsumer] Error processing backend-start commands");
            }

            await Task.Delay(PollInterval, stoppingToken);
        }

        logger.LogInformation("[BackendStartConsumer] Durable backend-start command consumer stopped");
    }

    /// <summary>
    /// On every poll, reset any <see cref="QueueOutboxEventStatus.Processing"/> events that are
    /// older than <see cref="StaleLeaseAge"/>. These were claimed by a process that crashed
    /// mid-execution and must be retried.
    /// </summary>
    internal async Task RecoverStaleLeasesAsync(CancellationToken ct)
    {
        try
        {
            await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            DateTime staleCutoff = DateTime.UtcNow - StaleLeaseAge;

            // Rows whose backend outcome is UNKNOWN are deliberately excluded: they may have
            // been delivered, so re-running them could double-start a printer. They stay
            // leased until the reconciler resolves the attempt.
            List<QueueDispatchOutbox> stale = await db.QueueDispatchOutbox
                .Where(e =>
                    e.EventType == CommandEventType &&
                    e.Status == QueueOutboxEventStatus.Processing &&
                    e.FailureCode != UnknownOutcomeFailureCode &&
                    e.LastAttemptedAtUtc < staleCutoff)
                .ToListAsync(ct);

            if (stale.Count > 0)
            {
                logger.LogWarning(
                    "[BackendStartConsumer] Recovering {Count} stale Processing lease(s) from previous process",
                    stale.Count);

                foreach (QueueDispatchOutbox evt in stale)
                {
                    evt.Status = QueueOutboxEventStatus.Pending;
                    evt.LastError = "Recovered from stale lease (previous process crash).";
                    evt.RetryAfterUtc = DateTime.UtcNow + PollInterval;

                    logger.LogWarning(
                        "[BackendStartConsumer] Stale lease recovered: EventId={EventId} Job={JobId} AttemptCount={Count}",
                        evt.Id,
                        evt.AggregateId,
                        evt.AttemptCount);
                }

                await db.SaveChangesAsync(ct);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[BackendStartConsumer] Error recovering stale leases");
        }
    }

    internal async Task ProcessPendingCommandsAsync(CancellationToken ct)
    {
        await RecoverStaleLeasesAsync(ct);
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        DateTime now = DateTime.UtcNow;
        List<QueueDispatchOutbox> pending = await db.QueueDispatchOutbox
            .Where(e =>
                e.EventType == CommandEventType &&
                e.Status == QueueOutboxEventStatus.Pending &&
                (e.RetryAfterUtc == null || e.RetryAfterUtc <= now))
            .OrderBy(e => e.Sequence)
            .Take(10)
            .ToListAsync(ct);

        if (pending.Count == 0)
        {
            return;
        }

        foreach (QueueDispatchOutbox evt in pending)
        {
            await ProcessSingleCommandAsync(scope, db, evt, ct);
        }
    }

    private async Task ProcessSingleCommandAsync(
        AsyncServiceScope scope,
        AppDbContext db,
        QueueDispatchOutbox evt,
        CancellationToken ct)
    {
        // ===================================================================
        // Step 1: Deserialize payload. Dead-letter on corrupt payload.
        // ===================================================================
        BackendStartPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<BackendStartPayload>(
                evt.PayloadJson,
                PayloadOptions);
        }
        catch (JsonException jsonEx)
        {
            logger.LogError(
                jsonEx,
                "[BackendStartConsumer] Cannot deserialize payload for event {EventId} — dead-lettering",
                evt.Id);
            evt.Status = QueueOutboxEventStatus.DeadLettered;
            evt.LastError = $"Invalid payload JSON: {jsonEx.Message}"[..Math.Min(jsonEx.Message.Length + 24, 2047)];
            evt.CompletedAtUtc = DateTime.UtcNow;
            await SetBedClearCommandStatusAsync(
                db, evt.Id, BedClearCommandStatus.Rejected, ct);
            await db.SaveChangesAsync(ct);
            return;
        }

        if (payload is null ||
            payload.JobId == Guid.Empty ||
            string.IsNullOrWhiteSpace(payload.AcknowledgementKey) ||
            string.IsNullOrWhiteSpace(payload.ActorSubject))
        {
            evt.Status = QueueOutboxEventStatus.DeadLettered;
            evt.LastError =
                "BackendStartCommand payload is missing required fields " +
                "(jobId, actorSubject, acknowledgementKey).";
            evt.CompletedAtUtc = DateTime.UtcNow;
            await SetBedClearCommandStatusAsync(
                db, evt.Id, BedClearCommandStatus.Rejected, ct);
            logger.LogError(
                "[BackendStartConsumer] Event {EventId} has incomplete payload — dead-lettered",
                evt.Id);
            await db.SaveChangesAsync(ct);
            return;
        }

        // ===================================================================
        // Step 2: Atomically acquire the row lease (Pending → Processing).
        // This prevents concurrent consumer instances from double-executing.
        // We save before the network call so a crash after save leaves the
        // event in Processing, which is recovered by RecoverStaleLeasesAsync.
        // ===================================================================
        evt.Status = QueueOutboxEventStatus.Processing;
        evt.AttemptCount++;
        evt.LastAttemptedAtUtc = DateTime.UtcNow;
        evt.LastError = null;

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another consumer instance claimed this event first — skip it.
            logger.LogDebug(
                "[BackendStartConsumer] Concurrency conflict claiming event {EventId} — skipped (other consumer won)",
                evt.Id);
            return;
        }

        logger.LogInformation(
            "[BackendStartConsumer] Executing backend start: EventId={EventId} Job={JobId} Actor={Actor}",
            evt.Id,
            payload.JobId,
            payload.ActorSubject);

        // ===================================================================
        // Step 3: Awaited backend execution (NOT fire-and-forget).
        // The event remains in Processing until success/failure is recorded.
        // A crash here leaves the event in Processing; RecoverStaleLeasesAsync
        // resets it to Pending on the next process start.
        // ===================================================================
        try
        {
            IPrintJobManagementService mgmt = scope.ServiceProvider.GetRequiredService<IPrintJobManagementService>();
            BackendStartOutcome outcome = await mgmt.DispatchJobWithAckAsync(
                payload.JobId.ToString(),
                payload.ActorSubject,
                payload.AcknowledgementKey,
                ct);

            await ApplyOutcomeAsync(evt.Id, payload.JobId, outcome, ct);
        }
        catch (OperationCanceledException)
        {
            // Shutdown — leave event in Processing for recovery on next start.
            logger.LogInformation(
                "[BackendStartConsumer] Execution cancelled (shutdown) for EventId={EventId} — will recover on restart",
                evt.Id);
            throw; // Propagate cancellation to stop the loop.
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "[BackendStartConsumer] Backend start threw for EventId={EventId} Job={JobId} (attempt {Count})",
                evt.Id,
                payload.JobId,
                evt.AttemptCount);

            // An unexpected exception is an UNKNOWN outcome: never mark it Published,
            // never retry blindly. Keep the row leased for the reconciler.
            await ApplyOutcomeAsync(
                evt.Id,
                payload.JobId,
                BackendStartOutcome.Unknown(ex.Message, attemptId: null),
                ct);
        }
    }

    /// <summary>
    /// Applies a typed backend-start outcome to the durable outbox row.
    ///
    /// <c>Published</c> means "a backend command was confirmed accepted" — nothing else.
    /// Unknown outcomes stay in <see cref="QueueOutboxEventStatus.Processing"/> (leased and
    /// reconcilable) and are never retried blindly. Permanent rejections are dead-lettered
    /// so an operator can act; transient rejections get bounded exponential retries.
    /// </summary>
    private async Task ApplyOutcomeAsync(
        Guid eventId,
        Guid jobId,
        BackendStartOutcome outcome,
        CancellationToken ct)
    {
        await using AsyncServiceScope outcomeScope = scopeFactory.CreateAsyncScope();
        AppDbContext outcomeDb = outcomeScope.ServiceProvider.GetRequiredService<AppDbContext>();

        QueueDispatchOutbox? row = await outcomeDb.QueueDispatchOutbox
            .FirstOrDefaultAsync(e => e.Id == eventId, ct);

        if (row is null)
        {
            return;
        }

        switch (outcome.Status)
        {
            case BackendStartStatus.Accepted:
                row.Status = QueueOutboxEventStatus.Published;
                row.CompletedAtUtc = DateTime.UtcNow;
                row.LastError = null;
                row.FailureCode = null;
                row.AttemptId = outcome.AttemptId ?? row.AttemptId;
                await SetBedClearCommandStatusAsync(
                    outcomeDb, eventId, BedClearCommandStatus.Accepted, ct);

                logger.LogInformation(
                    "[BackendStartConsumer] Backend start confirmed ({Status}): EventId={EventId} Job={JobId}",
                    outcome.Status, eventId, jobId);
                break;
            case BackendStartStatus.AlreadyStarted when outcome.BackendAcceptanceProven:
                row.Status = QueueOutboxEventStatus.Published;
                row.CompletedAtUtc = DateTime.UtcNow;
                row.LastError = null;
                row.FailureCode = null;
                row.AttemptId = outcome.AttemptId ?? row.AttemptId;
                await SetBedClearCommandStatusAsync(
                    outcomeDb, eventId, BedClearCommandStatus.Accepted, ct);
                break;
            case BackendStartStatus.AlreadyStarted:
                row.Status = QueueOutboxEventStatus.Processing;
                row.FailureCode = UnknownOutcomeFailureCode;
                row.LastError = Truncate(
                    "Database state alone does not prove backend acceptance; reconciliation is required.");
                row.AttemptId = outcome.AttemptId ?? row.AttemptId;
                row.RetryAfterUtc = null;
                await SetBedClearCommandStatusAsync(
                    outcomeDb, eventId, BedClearCommandStatus.Unknown, ct);
                break;

            case BackendStartStatus.Unknown:
                // Leave the row in Processing: the command may have been delivered.
                // The reconciler owns the resolution; blind retry could double-start.
                row.Status = QueueOutboxEventStatus.Processing;
                row.FailureCode = outcome.ErrorCode;
                row.LastError = Truncate(outcome.ErrorDetail);
                row.AttemptId = outcome.AttemptId ?? row.AttemptId;
                row.RetryAfterUtc = null;
                await SetBedClearCommandStatusAsync(
                    outcomeDb, eventId, BedClearCommandStatus.Unknown, ct);

                logger.LogError(
                    "[BackendStartConsumer] UNKNOWN backend outcome: EventId={EventId} Job={JobId} — " +
                    "row remains leased for reconciliation and is NOT retried",
                    eventId, jobId);
                break;

            case BackendStartStatus.Rejected:
            case BackendStartStatus.FailedBeforeStart:
                row.FailureCode = outcome.ErrorCode;
                row.LastError = Truncate(outcome.ErrorDetail);
                row.AttemptId = outcome.AttemptId ?? row.AttemptId;

                if (!outcome.IsRetryable || row.AttemptCount >= MaxAttempts)
                {
                    row.Status = QueueOutboxEventStatus.DeadLettered;
                    row.CompletedAtUtc = DateTime.UtcNow;
                    await SetBedClearCommandStatusAsync(
                        outcomeDb, eventId, BedClearCommandStatus.Rejected, ct);
                    logger.LogError(
                        "[BackendStartConsumer] Event {EventId} ended as {Status} after {Count} attempt(s)",
                        eventId,
                        outcome.Status,
                        row.AttemptCount);
                }
                else
                {
                    double backoffSeconds = RetryBackoffBase.TotalSeconds * Math.Pow(2, row.AttemptCount - 1);
                    row.Status = QueueOutboxEventStatus.Pending;
                    row.RetryAfterUtc = DateTime.UtcNow + TimeSpan.FromSeconds(backoffSeconds);
                    await SetBedClearCommandStatusAsync(
                        outcomeDb, eventId, BedClearCommandStatus.Pending, ct);
                }

                break;
        }

        _ = await outcomeDb.SaveChangesAsync(ct);
    }

    private static async Task SetBedClearCommandStatusAsync(
        AppDbContext db,
        Guid outboxEventId,
        BedClearCommandStatus status,
        CancellationToken ct)
    {
        BedClearCommandRecord? command = await db.BedClearCommandRecords
            .FirstOrDefaultAsync(
                candidate => candidate.OutboxEventId == outboxEventId,
                ct);
        if (command is not null)
        {
            command.Status = status;
            command.UpdatedAtUtc = DateTime.UtcNow;
        }
    }

    private static string? Truncate(string? value) =>
        value is null ? null : value[..Math.Min(value.Length, 2047)];

    /// <summary>Payload shape for <c>BackendStartCommand.v1</c> outbox events.</summary>
    private sealed record BackendStartPayload(
        Guid JobId,
        Guid PrinterId,
        string? ActorSubject,
        string AcknowledgementKey);
}
