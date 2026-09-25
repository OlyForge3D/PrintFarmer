using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Farm.Infrastructure.Services.Queue.Dispatch;

/// <summary>Body of <c>POST /api/dispatch/{printerId}/reconciliation/recover</c>.</summary>
public sealed class DispatchRecoveryRequest
{
    public Guid? DispatchAttemptId { get; set; }

    public long? ClaimRevision { get; set; }

    public bool? PhysicalCheckConfirmed { get; set; }

    /// <summary>
    /// Required when the server has no evidence that the start sender has settled: the operator
    /// additionally asserts the sending process is stopped or isolated from the printer.
    /// </summary>
    public bool? SenderIsolationConfirmed { get; set; }

    public string? Note { get; set; }

    /// <summary>Client-reported assertion time. Recorded but never trusted.</summary>
    public DateTime? ClientReportedAtUtc { get; set; }
}

/// <summary>HTTP-shaped result so exact replays return the stored status, ETag, and body.</summary>
public sealed record DispatchRecoveryResult(int StatusCode, string? ETag, string BodyJson);

/// <summary>Redacted immutable recovery-evidence record.</summary>
public sealed class DispatchRecoveryAuditDto
{
    public Guid AuditId { get; set; }

    public Guid PrinterId { get; set; }

    public Guid? JobId { get; set; }

    public Guid DispatchAttemptId { get; set; }

    public long ClaimRevision { get; set; }

    public string PriorOutcome { get; set; } = string.Empty;

    public string ActorId { get; set; } = string.Empty;

    public DateTime ActorRecordedAtUtc { get; set; }

    public DateTime ServerRecordedAtUtc { get; set; }

    public int AssertionVersion { get; set; }

    public bool PhysicalCheckConfirmed { get; set; }

    public bool SenderIsolationConfirmed { get; set; }

    public DateTime? SenderSettledAtUtc { get; set; }

    public string? Note { get; set; }

    public string? CorrelationId { get; set; }

    public string Transition { get; set; } = string.Empty;
}

/// <summary>Operator escape hatch for indeterminate pre-start dispatch claims (issue #2859).</summary>
public interface IDispatchRecoveryService
{
    /// <summary>Reads the printer-scoped reconciliation resource.</summary>
    Task<DispatchRecoveryResult> GetReconciliationAsync(
        Guid printerId,
        bool recoveryPermission,
        CancellationToken ct);

    /// <summary>Applies an idempotent, revision-fenced operator recovery assertion.</summary>
    Task<DispatchRecoveryResult> RecoverAsync(
        Guid printerId,
        string actorSubject,
        long expectedStateRevision,
        string ifMatch,
        string idempotencyKey,
        DispatchRecoveryRequest request,
        string? correlationId,
        CancellationToken ct);

    /// <summary>Returns the redacted journal record, or <see langword="null"/> when absent.</summary>
    Task<DispatchRecoveryAuditDto?> GetAuditAsync(Guid printerId, Guid auditId, CancellationToken ct);

    /// <summary>Clears the post-recovery operator block so the job can dispatch normally.</summary>
    Task<DispatchRecoveryResult> ClearRecoveryBlockAsync(
        Guid jobId,
        string actorSubject,
        long expectedJobRevision,
        CancellationToken ct);
}

/// <summary>
/// Implements the documented escape hatch: an explicit, authorized, physically-checked operator
/// assertion is the only way to close an indeterminate claim. The journal row and the single
/// recovery transition commit atomically; denied decisions are journaled and replayable.
/// </summary>
public sealed class DispatchRecoveryService(
    AppDbContext db,
    IDbOutboxSequenceAllocator sequenceAllocator,
    IOptions<DispatchEscalationOptions> escalationOptions,
    TimeProvider timeProvider,
    ILogger<DispatchRecoveryService> logger) : IDispatchRecoveryService
{
    /// <summary>Version of the operator assertion text accepted by this server.</summary>
    public const int AssertionVersion = 1;

    /// <summary>Maximum operator note length.</summary>
    public const int MaxNoteLength = 1000;

    public const string TransitionAccepted = "accepted";
    public const string TransitionRejectedStale = "rejected_stale";
    public const string TransitionRejectedNotIndeterminate = "rejected_not_indeterminate";
    public const string TransitionRejectedSenderLive = "rejected_sender_live";
    public const string TransitionRejectedSenderIsolationRequired =
        "rejected_sender_isolation_required";

    private const string RecoverRoute = "POST /api/dispatch/{printerId}/reconciliation/recover";
    private const string StartBarrierOperation = "start";
    private const string OutcomeUnknownFailureCode = "backend_outcome_unknown";

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly QueueOutboxEventStatus[] PendingOutboxStatuses =
        [QueueOutboxEventStatus.Pending, QueueOutboxEventStatus.Processing];

    /// <inheritdoc />
    public async Task<DispatchRecoveryResult> GetReconciliationAsync(
        Guid printerId,
        bool recoveryPermission,
        CancellationToken ct)
    {
        Printer? printer = await db.Printers
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == printerId, ct);
        if (printer is null)
        {
            return Error(404, "printer_not_found");
        }

        PrinterDispatchState? state = await db.PrinterDispatchStates
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.PrinterId == printerId, ct);
        QueueDispatchAttempt? attempt = state?.ActiveDispatchAttemptId is Guid attemptId
            ? await db.QueueDispatchAttempts
                .AsNoTracking()
                .FirstOrDefaultAsync(candidate => candidate.Id == attemptId, ct)
            : null;
        Guid? lastAuditId = await LatestAcceptedAuditIdAsync(printerId, ct);
        object body = BuildResource(printer, state, IsIndeterminate(state, attempt) ? attempt : null, recoveryPermission, lastAuditId);
        return new DispatchRecoveryResult(200, StateETag(state), Serialize(body));
    }

    /// <inheritdoc />
    public async Task<DispatchRecoveryResult> RecoverAsync(
        Guid printerId,
        string actorSubject,
        long expectedStateRevision,
        string ifMatch,
        string idempotencyKey,
        DispatchRecoveryRequest request,
        string? correlationId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorSubject);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);

        if (request.DispatchAttemptId is not Guid attemptId || attemptId == Guid.Empty ||
            request.ClaimRevision is not long claimRevision || claimRevision < 1 ||
            request.PhysicalCheckConfirmed is null)
        {
            return Error(400, "invalid_request", "dispatchAttemptId, claimRevision, and physicalCheckConfirmed are required.");
        }

        if (request.PhysicalCheckConfirmed != true)
        {
            return Error(400, "physical_check_required", "The operator must confirm the physical check.");
        }

        string? note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        if (note is { Length: > MaxNoteLength })
        {
            return Error(400, "note_too_long", $"note must be at most {MaxNoteLength} characters.");
        }

        bool senderIsolationConfirmed = request.SenderIsolationConfirmed == true;
        DateTime? clientReportedAtUtc = NormalizeUtc(request.ClientReportedAtUtc);

        // The fingerprint covers the request body only. If-Match is a precondition header: an
        // exact replay resolves before ETag checks, so it must not change the fingerprint.
        string scopeHash = Sha256Hex(string.Join('\n', actorSubject, RecoverRoute, printerId.ToString("D"), idempotencyKey));
        string reportedToken = clientReportedAtUtc is DateTime reportedAt
            ? reportedAt.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : string.Empty;
        string fingerprint = Sha256Hex(string.Join(
            '\n',
            attemptId.ToString("D"),
            claimRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "physical=true",
            senderIsolationConfirmed ? "isolation=true" : "isolation=false",
            note ?? string.Empty,
            "reported=" + reportedToken));

        DispatchRecoveryResult? replay = await TryReplayAsync(scopeHash, fingerprint, ct);
        if (replay is not null)
        {
            return replay;
        }

        try
        {
            return await DecideAsync(
                printerId,
                actorSubject,
                expectedStateRevision,
                attemptId,
                claimRevision,
                senderIsolationConfirmed,
                note,
                clientReportedAtUtc,
                correlationId,
                scopeHash,
                fingerprint,
                ct);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            // A concurrent writer (reconciler, consumer, or another operator) changed the fenced
            // rows between our read and commit. Nothing was committed; ownership is unchanged.
            logger.LogInformation(ex, "dispatch_recovery_concurrency_conflict printer={PrinterId} attempt={AttemptId}", printerId, attemptId);
            db.ChangeTracker.Clear();
            return Error(412, TransitionRejectedStale, "The claim changed; refresh and retry with a new Idempotency-Key.");
        }
        catch (DbUpdateException ex)
        {
            // Most likely a concurrent request with the same idempotency scope won the unique
            // index race. Resolve by replaying the committed decision.
            db.ChangeTracker.Clear();
            DispatchRecoveryResult? raced = await TryReplayAsync(scopeHash, fingerprint, ct);
            if (raced is not null)
            {
                return raced;
            }

            logger.LogError(ex, "dispatch_recovery_persist_failed printer={PrinterId} attempt={AttemptId}", printerId, attemptId);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<DispatchRecoveryAuditDto?> GetAuditAsync(Guid printerId, Guid auditId, CancellationToken ct)
    {
        DispatchRecoveryJournalEntry? entry = await db.DispatchRecoveryJournalEntries
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == auditId && candidate.PrinterId == printerId, ct);
        return entry is null ? null : ToAuditDto(entry);
    }

    /// <inheritdoc />
    public async Task<DispatchRecoveryResult> ClearRecoveryBlockAsync(
        Guid jobId,
        string actorSubject,
        long expectedJobRevision,
        CancellationToken ct)
    {
        PrintJob? job = await db.PrintJobs.FirstOrDefaultAsync(candidate => candidate.Id == jobId, ct);
        if (job is null)
        {
            return Error(404, "job_not_found");
        }

        if (job.Status != PrintJobStatus.Queued ||
            job.BlockedReasonCode != JobBlockedReasonCode.OperatorRecoveryRequired)
        {
            return Error(409, "job_not_recovery_blocked", "The job is not blocked by an operator recovery.");
        }

        if (job.Revision != expectedJobRevision)
        {
            return Error(412, "job_revision_conflict", "The job changed; refresh and retry.");
        }

        await using QueueOutboxTransactionScope transaction =
            await QueueOutboxTransactionScope.BeginAsync(db, ct);
        DateTime now = timeProvider.GetUtcNow().UtcDateTime;
        string? priorDetail = job.BlockedReasonJson;
        job.BlockedReasonCode = null;
        job.BlockedReasonJson = null;
        job.UpdatedAt = now;
        _ = QueueAuditWriter.Add(
            db,
            actorSubject,
            QueueAuditOperations.Reconciliation,
            QueueAuditOutcomes.Success,
            nameof(PrintJob),
            resourceId: job.Id,
            printerId: job.AssignedPrinterId,
            printJobId: job.Id,
            reasonCode: "operator_recovery_cleared");
        await DispatchClaimService.AddLifecycleOutboxEventAsync(
            db,
            sequenceAllocator,
            DispatchClaimService.EventTypeDispatchRecoveryCleared,
            aggregateId: job.Id,
            printerId: job.AssignedPrinterId,
            attemptId: null,
            aggregateRowVersion: job.RowVersion,
            failureCode: null,
            payloadJson: JsonSerializer.Serialize(new { jobId = job.Id, printerId = job.AssignedPrinterId, priorDetail }, JsonOptions),
            ct,
            timeProvider: timeProvider);
        try
        {
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return Error(412, "job_revision_conflict", "The job changed; refresh and retry.");
        }

        return new DispatchRecoveryResult(
            200,
            RevisionETag.EncodeQuoted(job.Revision),
            Serialize(new { jobId = job.Id, status = job.Status, blockedReasonCode = (string?)null, revision = job.Revision }));
    }

    private async Task<DispatchRecoveryResult> DecideAsync(
        Guid printerId,
        string actorSubject,
        long expectedStateRevision,
        Guid attemptId,
        long claimRevision,
        bool senderIsolationConfirmed,
        string? note,
        DateTime? clientReportedAtUtc,
        string? correlationId,
        string scopeHash,
        string fingerprint,
        CancellationToken ct)
    {
        await using QueueOutboxTransactionScope transaction =
            await QueueOutboxTransactionScope.BeginAsync(db, ct);
        DateTime now = timeProvider.GetUtcNow().UtcDateTime;

        Printer? printer = await db.Printers
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == printerId, ct);
        PrinterDispatchState? state = await db.PrinterDispatchStates
            .FirstOrDefaultAsync(candidate => candidate.PrinterId == printerId, ct);
        QueueDispatchAttempt? attempt = await db.QueueDispatchAttempts
            .FirstOrDefaultAsync(candidate => candidate.Id == attemptId, ct);

        var journal = new DispatchRecoveryJournalEntry
        {
            Id = Guid.NewGuid(),
            PrinterId = printerId,
            PrintJobId = attempt?.PrinterId == printerId ? attempt.PrintJobId : null,
            DispatchAttemptId = attemptId,
            ClaimRevision = claimRevision,
            PriorOutcome = attempt?.PrinterId == printerId ? attempt.Outcome.ToString() : "NotFound",
            ActorSubject = Truncate(actorSubject, 256),
            ActorRecordedAtUtc = now,
            ClientReportedAtUtc = clientReportedAtUtc,
            AssertionVersion = AssertionVersion,
            PhysicalCheckConfirmed = true,
            SenderIsolationConfirmed = senderIsolationConfirmed,
            SenderSettledAtUtc = attempt?.PrinterId == printerId ? attempt.BackendSenderSettledAtUtc : null,
            Note = note,
            CorrelationId = correlationId is null ? null : Truncate(correlationId, 128),
            ReplayScopeHash = scopeHash,
            RequestFingerprint = fingerprint,
        };

        if (printer is null || state is null || attempt is null ||
            attempt.PrinterId != printerId || !IsIndeterminate(state, attempt))
        {
            return await CommitDecisionAsync(
                transaction,
                journal,
                TransitionRejectedNotIndeterminate,
                409,
                StateETag(state),
                new { error = TransitionRejectedNotIndeterminate, detail = "The dispatch attempt is not the printer's current indeterminate claim.", recoveryAuditId = journal.Id },
                evidence: null,
                ct);
        }

        if (state.Revision != expectedStateRevision || attempt.Revision != claimRevision)
        {
            return await CommitDecisionAsync(
                transaction,
                journal,
                TransitionRejectedStale,
                412,
                StateETag(state),
                new { error = TransitionRejectedStale, detail = "The claim revision is stale; refresh and retry with a new Idempotency-Key.", recoveryAuditId = journal.Id },
                evidence: null,
                ct);
        }

        string? liveSender = await FindLiveSenderAsync(state, attempt, ct);
        if (liveSender is not null)
        {
            return await CommitDecisionAsync(
                transaction,
                journal,
                TransitionRejectedSenderLive,
                409,
                StateETag(state),
                new { error = TransitionRejectedSenderLive, detail = "A start or control sender for this attempt may still be live.", liveSender, recoveryAuditId = journal.Id },
                evidence: new { liveSender },
                ct);
        }

        if (attempt.BackendSenderSettledAtUtc is null && !senderIsolationConfirmed)
        {
            return await CommitDecisionAsync(
                transaction,
                journal,
                TransitionRejectedSenderIsolationRequired,
                409,
                StateETag(state),
                new { error = TransitionRejectedSenderIsolationRequired, detail = "No evidence proves the start sender has settled; confirm the sender is stopped or isolated.", recoveryAuditId = journal.Id },
                evidence: null,
                ct);
        }

        object evidence = BuildEvidence(attempt);
        int deadLetteredStart = await DeadLetterStartCommandsAsync(attempt, now, ct);
        int supersededControl = await SupersedeControlCommandsAsync(attempt, now, ct);
        await RejectBedClearCommandAsync(attempt.Id, now, ct);

        attempt.Outcome = DispatchAttemptOutcome.OperatorRecovered;
        attempt.RequiresReconciliation = false;
        attempt.IsRetryable = false;
        attempt.BackendCallPhase = DispatchBackendCallPhase.Terminal;
        attempt.TerminalAtUtc = now;
        attempt.ErrorCode = "operator_recovery";
        attempt.ErrorDetail = "Operator physically checked the printer and asserted the dispatch did not start.";
        attempt.UpdatedAtUtc = now;

        PrintJob? job = attempt.PrintJobId is Guid jobId
            ? await db.PrintJobs.FirstOrDefaultAsync(candidate => candidate.Id == jobId, ct)
            : null;
        if (job is not null && job.Status == PrintJobStatus.Starting)
        {
            // AssignedPrinterId and QueuePosition are intentionally unchanged (Lead ruling):
            // calibration jobs keep their immutable printer, and the operator block prevents
            // any automatic redispatch until an explicit clear.
            job.Status = PrintJobStatus.Queued;
            job.ActualStartTime = null;
            job.BlockedReasonCode = JobBlockedReasonCode.OperatorRecoveryRequired;
            job.BlockedReasonJson = JsonSerializer.Serialize(
                new { dispatchAttemptId = attempt.Id, recoveryAuditId = journal.Id },
                JsonOptions);
            job.UpdatedAt = now;
            db.JobStateHistories.Add(new JobStateHistory
            {
                Id = Guid.NewGuid(),
                JobId = job.Id,
                FromState = nameof(PrintJobStatus.Starting),
                ToState = nameof(PrintJobStatus.Queued),
                TransitionedAtUtc = now,
                CreatedAt = now,
                Notes = $"Operator recovery {journal.Id:D}: physical check asserted the dispatch did not start.",
            });
        }

        state.ActiveJobId = null;
        state.ActiveDispatchAttemptId = null;
        ClearStartBarrier(state, attempt.Id);
        if (state.AcknowledgedJobId is not null && state.AcknowledgedJobId == attempt.PrintJobId)
        {
            ClearAcknowledgement(state);
        }

        state.QueueRevision++;

        _ = QueueAuditWriter.Add(
            db,
            actorSubject,
            QueueAuditOperations.Reconciliation,
            QueueAuditOutcomes.Success,
            attempt.PrintJobId is null ? nameof(Printer) : nameof(PrintJob),
            resourceId: attempt.PrintJobId ?? (Guid?)attempt.PrinterId,
            printerId: attempt.PrinterId,
            printJobId: attempt.PrintJobId,
            dispatchAttemptId: attempt.Id,
            reasonCode: "operator_recovery",
            detail: new { recoveryAuditId = journal.Id });

        if (job is not null)
        {
            await DispatchClaimService.AddLifecycleOutboxEventAsync(
                db,
                sequenceAllocator,
                DispatchClaimService.EventTypeDispatchOperatorRecovered,
                aggregateId: job.Id,
                printerId: attempt.PrinterId,
                attemptId: attempt.Id,
                aggregateRowVersion: job.RowVersion,
                failureCode: "operator_recovery",
                payloadJson: JsonSerializer.Serialize(
                    new { jobId = job.Id, printerId = attempt.PrinterId, attemptId = attempt.Id, recoveryAuditId = journal.Id },
                    JsonOptions),
                ct,
                timeProvider: timeProvider);
        }

        long nextStateRevision = state.Revision + 1;
        object resource = BuildResource(printer, state, indeterminate: null, recoveryPermission: true, lastAuditId: journal.Id);
        logger.LogWarning(
            "dispatch_operator_recovery_accepted printer={PrinterId} attempt={AttemptId} job={JobId} audit={AuditId} deadLetteredStart={DeadLetteredStart} supersededControl={SupersededControl}",
            printerId,
            attempt.Id,
            attempt.PrintJobId,
            journal.Id,
            deadLetteredStart,
            supersededControl);
        return await CommitDecisionAsync(
            transaction,
            journal,
            TransitionAccepted,
            200,
            RevisionETag.EncodeQuoted(nextStateRevision),
            resource,
            evidence,
            ct);
    }

    private async Task<DispatchRecoveryResult> CommitDecisionAsync(
        QueueOutboxTransactionScope transaction,
        DispatchRecoveryJournalEntry journal,
        string transition,
        int statusCode,
        string? etag,
        object body,
        object? evidence,
        CancellationToken ct)
    {
        string bodyJson = Serialize(body);
        journal.Transition = transition;
        journal.ServerRecordedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        journal.ResponseStatusCode = statusCode;
        journal.ResponseETag = etag;
        journal.ResponseBodyJson = bodyJson;
        journal.EvidenceJson = evidence is null ? null : Truncate(JsonSerializer.Serialize(evidence, JsonOptions), 4000);
        db.DispatchRecoveryJournalEntries.Add(journal);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new DispatchRecoveryResult(statusCode, etag, bodyJson);
    }

    private async Task<DispatchRecoveryResult?> TryReplayAsync(string scopeHash, string fingerprint, CancellationToken ct)
    {
        DispatchRecoveryJournalEntry? prior = await db.DispatchRecoveryJournalEntries
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.ReplayScopeHash == scopeHash, ct);
        if (prior is null)
        {
            return null;
        }

        return string.Equals(prior.RequestFingerprint, fingerprint, StringComparison.Ordinal)
            ? new DispatchRecoveryResult(prior.ResponseStatusCode, prior.ResponseETag, prior.ResponseBodyJson)
            : Error(409, "idempotency_key_reused", "The Idempotency-Key was already used with a different request.");
    }

    /// <summary>
    /// Fail closed: returns a reason when any start or control sender for this attempt may still
    /// be live. Absence of evidence is never treated as cessation.
    /// </summary>
    private async Task<string?> FindLiveSenderAsync(
        PrinterDispatchState state,
        QueueDispatchAttempt attempt,
        CancellationToken ct)
    {
        if (state.PhysicalControlCommandId.HasValue &&
            !(state.PhysicalControlCommandId == attempt.Id &&
              string.Equals(state.PhysicalControlOperation, StartBarrierOperation, StringComparison.Ordinal)))
        {
            return "physical_control_barrier";
        }

        Guid? jobId = attempt.PrintJobId;
        bool startSenderLive = await db.QueueDispatchOutbox
            .AsNoTracking()
            .AnyAsync(
                command =>
                    command.EventType == BedClearAcknowledgementService.BackendStartCommandEventType &&
                    command.Status == QueueOutboxEventStatus.Processing &&
                    (command.FailureCode == null || command.FailureCode != OutcomeUnknownFailureCode) &&
                    (command.AttemptId == attempt.Id ||
                     (jobId != null && command.AttemptId == null && command.AggregateId == jobId)),
                ct);
        if (startSenderLive)
        {
            return "start_command_processing";
        }

        bool controlSenderLive = await db.QueueDispatchOutbox
            .AsNoTracking()
            .AnyAsync(
                command =>
                    command.EventType == BackendControlCommandConsumerService.EventType &&
                    command.Status == QueueOutboxEventStatus.Processing &&
                    command.AttemptId == attempt.Id,
                ct);
        return controlSenderLive ? "control_command_processing" : null;
    }

    private async Task<int> DeadLetterStartCommandsAsync(QueueDispatchAttempt attempt, DateTime now, CancellationToken ct)
    {
        Guid? jobId = attempt.PrintJobId;
        List<QueueDispatchOutbox> commands = await db.QueueDispatchOutbox
            .Where(command =>
                command.EventType == BedClearAcknowledgementService.BackendStartCommandEventType &&
                PendingOutboxStatuses.Contains(command.Status) &&
                (command.AttemptId == attempt.Id ||
                 (jobId != null && command.AttemptId == null && command.AggregateId == jobId)))
            .ToListAsync(ct);
        foreach (QueueDispatchOutbox command in commands)
        {
            command.AttemptId = attempt.Id;
            command.Status = QueueOutboxEventStatus.DeadLettered;
            command.FailureCode = "operator_recovery";
            command.LastError = "Closed by operator recovery; the start must not be replayed.";
            command.CompletedAtUtc = now;
            command.RetryAfterUtc = null;
        }

        return commands.Count;
    }

    private async Task<int> SupersedeControlCommandsAsync(QueueDispatchAttempt attempt, DateTime now, CancellationToken ct)
    {
        // Pending cancel/abort intents are superseded, never honored: recovery is not a cancel.
        List<QueueDispatchOutbox> commands = await db.QueueDispatchOutbox
            .Where(command =>
                command.EventType == BackendControlCommandConsumerService.EventType &&
                command.Status == QueueOutboxEventStatus.Pending &&
                command.AttemptId == attempt.Id)
            .ToListAsync(ct);
        foreach (QueueDispatchOutbox command in commands)
        {
            command.Status = QueueOutboxEventStatus.DeadLettered;
            command.FailureCode = "superseded_by_operator_recovery";
            command.LastError = "Superseded by operator recovery of the indeterminate claim.";
            command.CompletedAtUtc = now;
            command.RetryAfterUtc = null;
        }

        return commands.Count;
    }

    private async Task RejectBedClearCommandAsync(Guid attemptId, DateTime now, CancellationToken ct)
    {
        BedClearCommandRecord? command = await db.BedClearCommandRecords
            .FirstOrDefaultAsync(record => record.DispatchAttemptId == attemptId, ct);
        if (command is not null &&
            command.Status is not (BedClearCommandStatus.Accepted or BedClearCommandStatus.Rejected or BedClearCommandStatus.Expired))
        {
            command.Status = BedClearCommandStatus.Rejected;
            command.UpdatedAtUtc = now;
        }
    }

    private async Task<Guid?> LatestAcceptedAuditIdAsync(Guid printerId, CancellationToken ct) =>
        await db.DispatchRecoveryJournalEntries
            .AsNoTracking()
            .Where(entry => entry.PrinterId == printerId && entry.Transition == TransitionAccepted)
            .OrderByDescending(entry => entry.ServerRecordedAtUtc)
            .Select(entry => (Guid?)entry.Id)
            .FirstOrDefaultAsync(ct);

    internal static bool IsIndeterminate(PrinterDispatchState? state, QueueDispatchAttempt? attempt) =>
        state is not null &&
        attempt is not null &&
        state.ActiveDispatchAttemptId == attempt.Id &&
        state.ActiveJobId == attempt.PrintJobId &&
        attempt.Outcome == DispatchAttemptOutcome.Unknown &&
        attempt.RequiresReconciliation;

    private object BuildResource(
        Printer? printer,
        PrinterDispatchState? state,
        QueueDispatchAttempt? indeterminate,
        bool recoveryPermission,
        Guid? lastAuditId)
    {
        if (indeterminate is null)
        {
            return new
            {
                printerId = printer?.Id ?? state?.PrinterId,
                printerName = printer?.Name,
                hasIndeterminateClaim = false,
                jobId = (Guid?)null,
                dispatchAttemptId = (Guid?)null,
                claimRevision = (long?)null,
                claimAgeSeconds = (long?)null,
                claimedAtUtc = (DateTime?)null,
                lastReconciledAtUtc = (DateTime?)null,
                lastEvidence = (object?)null,
                outcome = (string?)null,
                escalationLevel = nameof(DispatchEscalationLevel.None),
                senderSettled = (bool?)null,
                recoveryPermission,
                recoveryAuditId = lastAuditId,
            };
        }

        DateTime now = timeProvider.GetUtcNow().UtcDateTime;
        TimeSpan age = now - indeterminate.ClaimedAtUtc;
        if (age < TimeSpan.Zero)
        {
            age = TimeSpan.Zero;
        }

        return new
        {
            printerId = printer?.Id ?? indeterminate.PrinterId,
            printerName = printer?.Name,
            hasIndeterminateClaim = true,
            jobId = indeterminate.PrintJobId,
            dispatchAttemptId = indeterminate.Id,
            claimRevision = indeterminate.Revision,
            claimAgeSeconds = (long)age.TotalSeconds,
            claimedAtUtc = indeterminate.ClaimedAtUtc,
            lastReconciledAtUtc = indeterminate.LastReconciledAtUtc,
            lastEvidence = BuildEvidence(indeterminate),
            outcome = indeterminate.Outcome.ToString(),
            escalationLevel = escalationOptions.Value.Resolve(age).ToString(),
            senderSettled = indeterminate.BackendSenderSettledAtUtc is not null,
            recoveryPermission,
            recoveryAuditId = (Guid?)null,
        };
    }

    /// <summary>Redacted evidence: typed codes and timestamps only, never backend payloads or exception text.</summary>
    private static object BuildEvidence(QueueDispatchAttempt attempt) =>
        new
        {
            backendCallPhase = attempt.BackendCallPhase.ToString(),
            errorCode = attempt.ErrorCode,
            startPathKind = attempt.StartPathKind,
            reconciliationCount = attempt.ReconciliationCount,
            backendCallStartedAtUtc = attempt.BackendCallStartedAtUtc,
            backendResponseAtUtc = attempt.BackendResponseAtUtc,
            senderSettledAtUtc = attempt.BackendSenderSettledAtUtc,
            hasBackendJobId = !string.IsNullOrEmpty(attempt.BackendJobId),
        };

    internal static DispatchRecoveryAuditDto ToAuditDto(DispatchRecoveryJournalEntry entry) =>
        new()
        {
            AuditId = entry.Id,
            PrinterId = entry.PrinterId,
            JobId = entry.PrintJobId,
            DispatchAttemptId = entry.DispatchAttemptId,
            ClaimRevision = entry.ClaimRevision,
            PriorOutcome = entry.PriorOutcome,
            ActorId = entry.ActorSubject,
            ActorRecordedAtUtc = entry.ActorRecordedAtUtc,
            ServerRecordedAtUtc = entry.ServerRecordedAtUtc,
            AssertionVersion = entry.AssertionVersion,
            PhysicalCheckConfirmed = entry.PhysicalCheckConfirmed,
            SenderIsolationConfirmed = entry.SenderIsolationConfirmed,
            SenderSettledAtUtc = entry.SenderSettledAtUtc,
            Note = entry.Note,
            CorrelationId = entry.CorrelationId,
            Transition = entry.Transition,
        };

    private static void ClearStartBarrier(PrinterDispatchState state, Guid attemptId)
    {
        if (state.PhysicalControlCommandId != attemptId ||
            !string.Equals(state.PhysicalControlOperation, StartBarrierOperation, StringComparison.Ordinal))
        {
            return;
        }

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

    private static string StateETag(PrinterDispatchState? state) =>
        RevisionETag.EncodeQuoted(Math.Max(1, state?.Revision ?? 1));

    private static DispatchRecoveryResult Error(int statusCode, string error, string? detail = null) =>
        new(statusCode, null, Serialize(new { error, detail }));

    private static string Serialize(object body) => JsonSerializer.Serialize(body, JsonOptions);

    private static DateTime? NormalizeUtc(DateTime? value) => value switch
    {
        null => null,
        { Kind: DateTimeKind.Unspecified } unspecified => DateTime.SpecifyKind(unspecified, DateTimeKind.Utc),
        DateTime other => other.ToUniversalTime(),
    };

    private static string Sha256Hex(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
