using System.Data;
using System.Text.Json;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Security;
using Farm.Infrastructure.Services.Authentication;
using Farm.Infrastructure.Services.Queue;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Farm.Infrastructure.Services.Printers;

public sealed class PrinterControlException : Exception
{
    public PrinterControlException()
        : this(503, "admission_unavailable", "Control persistence is unavailable.")
    {
    }

    public PrinterControlException(string message)
        : this(503, "admission_unavailable", message)
    {
    }

    public PrinterControlException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public PrinterControlException(int status, string code, string message)
        : base(message)
    {
        Status = status;
        Code = code;
    }

    public int Status { get; } = 503;

    public string Code { get; } = "admission_unavailable";
}

public sealed record PrinterEmergencyStopLease(Guid OperationId, Guid AttemptId, string ConfigurationIdentity);

/// <summary>Transactional admission, fencing, observations and explicit operator recovery.</summary>
public sealed class PrinterControlOperationService(
    AppDbContext db,
    IDbOutboxSequenceAllocator sequence,
    IQueueResourceAuthorizationService authorization,
    IAuthenticationService authentication,
    IMoonrakerMotionChannelFactory? channels = null)
{
    public const string EventType = "PrintFarmer.Printer.ControlOperationUpdated.v1";
    public static readonly TimeSpan OwnerLiveness = TimeSpan.FromSeconds(45);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<PrinterControlOperationDto> AdmitAsync(
        Guid printerId, Guid operationId, string actor, PrinterControlRequest request, CancellationToken ct)
    {
        await AuthorizeActorAsync(printerId, actor, ct);
        if (operationId == Guid.Empty || !PrinterControlIntent.IsValid(request))
        {
            throw Error(400, "invalid", "A UUID idempotency key and a valid motion intent are required.");
        }

        string intent = PrinterControlIntent.Normalize(request);
        PrinterControlOperation? replay = await db.PrinterControlOperations.AsNoTracking()
            .SingleOrDefaultAsync(operation => operation.Id == operationId, ct);
        if (replay is not null)
        {
            if (replay.PrinterId != printerId || replay.ActorSubject != actor || replay.NormalizedIntent != intent)
            {
                throw Error(409, "idempotency_conflict", "The idempotency key cannot be reused.");
            }

            return await GetAsync(printerId, operationId, ct);
        }

        Printer printer = await db.Printers.SingleOrDefaultAsync(p => p.Id == printerId, ct)
            ?? throw Error(404, "not_found", "Printer not found.");
        ValidatePrinter(printer);
        if (channels is null)
        {
            throw Error(422, "printer_operation_unsupported", "The Moonraker motion plugin is unavailable.");
        }

        PrinterDispatchState? barrier = await db.PrinterDispatchStates.SingleOrDefaultAsync(state => state.PrinterId == printerId, ct);
        if (barrier is null)
        {
            barrier = new PrinterDispatchState { PrinterId = printerId };
            db.PrinterDispatchStates.Add(barrier);
        }

        // Fence configuration edits/deletion even when this is the printer's first
        // dispatch-state row and no existing barrier row can be locked yet.
        db.Entry(printer).Property(p => p.Revision).IsModified = true;
        await RequireIdleBarrierAsync(barrier, ct);
        if (barrier.PhysicalControlCommandId.HasValue)
        {
            throw Error(409, "physical_control_barrier", "Another physical operation owns this printer.");
        }

        DateTime now = DateTime.UtcNow;
        var operation = new PrinterControlOperation
        {
            Id = operationId,
            PrinterId = printerId,
            ActorSubject = actor,
            NormalizedIntent = intent,
            Kind = request.Kind,
            X = request.X,
            Y = request.Y,
            Z = request.Z,
            F = request.F,
            PrinterConfigurationIdentity = PrinterControlIntent.ConfigurationIdentity(printer),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            State = PrinterControlState.Queued,
        };
        db.PrinterControlOperations.Add(operation);
        barrier.PhysicalControlCommandId = operationId;
        barrier.PhysicalControlAttemptId = null;
        barrier.PhysicalControlOperation = "async_motion";
        barrier.PhysicalControlActorSubject = actor;
        barrier.PhysicalControlStartedAtUtc = now;
        barrier.PhysicalControlRequiresReconciliation = false;
        try
        {
            await PersistAsync(operation, barrier, actor, "admitted", ct);
        }
        catch (DbUpdateException exception)
        {
            // Resolve concurrent admission only from fresh committed state, never from the
            // failed transaction's tracked barrier or attempted idempotency insert.
            db.ChangeTracker.Clear();
            replay = await db.PrinterControlOperations.AsNoTracking()
                .SingleOrDefaultAsync(candidate => candidate.Id == operationId, ct);
            if (replay is not null && replay.PrinterId == printerId && replay.ActorSubject == actor && replay.NormalizedIntent == intent)
            {
                return await GetAsync(printerId, operationId, ct);
            }

            if (replay is not null)
            {
                throw Error(409, "idempotency_conflict", "The idempotency key cannot be reused.");
            }

            if (exception is DbUpdateConcurrencyException ||
                await db.PrinterDispatchStates.AsNoTracking().AnyAsync(
                    state => state.PrinterId == printerId && state.PhysicalControlCommandId != null, ct))
            {
                throw Error(409, "physical_control_barrier", "The printer changed during admission; fetch current control state.");
            }

            throw Error(503, "admission_unavailable", "Control persistence is unavailable; retry with the same idempotency key.");
        }

        return Map(operation, true);
    }

    public async Task AuthorizeActorAsync(Guid printerId, string actor, CancellationToken ct)
    {
        if (!Guid.TryParse(actor, out Guid userId) ||
            !await db.Users.AsNoTracking().AnyAsync(user => user.Id == userId && user.IsActive, ct))
        {
            throw Error(403, "forbidden", "An active user is required.");
        }

        bool admin = await db.UserRoles.AsNoTracking().AnyAsync(
            role => role.UserId == userId && role.IsActive && role.Role.Name == PrintFarmerPermissions.FarmAdminRole, ct);
        if (!admin && !await authentication.HasPermissionAsync(userId, "queue", "start"))
        {
            throw Error(403, "forbidden", "Queue start permission is required.");
        }

        if (!await authorization.CanActorAccessPrinterAsync(actor, printerId, PrinterGroupAccessLevel.Submit, ct))
        {
            throw Error(404, "not_found", "Printer not found.");
        }
    }

    public async Task<PrinterControlOperationDto> GetAsync(Guid printerId, Guid operationId, CancellationToken ct)
    {
        await using IDbContextTransaction? snapshot = await BeginReadSnapshotAsync(db, ct);
        var receipt = await (
            from operation in db.PrinterControlOperations.AsNoTracking()
            where operation.Id == operationId && operation.PrinterId == printerId
            join state in db.PrinterDispatchStates.AsNoTracking() on operation.PrinterId equals state.PrinterId into states
            from barrier in states.DefaultIfEmpty()
            select new { Operation = operation, Held = barrier != null && barrier.PhysicalControlCommandId == operationId })
            .SingleOrDefaultAsync(ct) ?? throw Error(404, "not_found", "Operation not found.");
        return Map(receipt.Operation, receipt.Held);
    }

    public async Task<PrinterControlCurrentDto> GetCurrentAsync(Guid printerId, CancellationToken ct)
    {
        // Keep the cross-table read coherent on SQL Server's lock-based READ COMMITTED
        // as well as MVCC providers. A settlement cannot slip between barrier and receipt.
        await using IDbContextTransaction? snapshot = await BeginReadSnapshotAsync(db, ct);
        var current = await (
            from printer in db.Printers.AsNoTracking()
            where printer.Id == printerId
            join state in db.PrinterDispatchStates.AsNoTracking() on printer.Id equals state.PrinterId into states
            from barrier in states.DefaultIfEmpty()
            join record in db.PrinterControlOperations.AsNoTracking()
                on new { Id = barrier == null ? null : barrier.PhysicalControlCommandId, PrinterId = printer.Id }
                equals new { Id = (Guid?)record.Id, record.PrinterId } into records
            from operation in records.DefaultIfEmpty()
            select new { printer.Backend, Barrier = barrier, Operation = operation })
            .SingleOrDefaultAsync(ct) ?? throw Error(404, "not_found", "Printer not found.");
        PrinterPhysicalControlDto projection = new(
            current.Backend == (int)PrinterBackend.Moonraker ? Enum.GetValues<PrinterControlKind>() : [],
            current.Barrier?.PhysicalControlCommandId.HasValue == true,
            current.Operation?.Id, current.Operation?.State,
            current.Operation?.RequiresRecovery == true || current.Barrier?.PhysicalControlRequiresReconciliation == true);
        return new(projection, current.Operation is null ? null : Map(current.Operation, true));
    }

    private static async Task<IDbContextTransaction?> BeginReadSnapshotAsync(AppDbContext db, CancellationToken ct)
    {
        if (!db.Database.IsRelational())
        {
            return null;
        }

        if (db.Database.CurrentTransaction is not { } transaction)
        {
            return await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        }

        if (transaction.GetDbTransaction().IsolationLevel is not
            (IsolationLevel.Serializable or IsolationLevel.RepeatableRead or IsolationLevel.Snapshot))
        {
            throw Error(503, "admission_unavailable", "A consistent control snapshot is unavailable.");
        }

        return null;
    }

    public async Task<PrinterControlOperationDto> BeginRecoveryAsync(
        Guid printerId, Guid operationId, string revision, string actor, CancellationToken ct)
    {
        PrinterControlOperation operation = await LoadOperationAsync(printerId, operationId, ct);
        RequireRevision(operation, revision);
        PrinterDispatchState barrier = await LoadBarrierAsync(printerId, ct);
        RequireOwner(barrier, operation);
        if (operation.State is not (PrinterControlState.Running or PrinterControlState.Unknown))
        {
            throw Error(409, "recovery_prerequisite", "Only running or unknown operations can enter recovery.");
        }

        operation.RecoveryFromRevision = operation.Revision;
        operation.RecoveryActorSubject = actor;
        operation.RecoveryRequestedAtUtc = DateTime.UtcNow;
        operation.State = PrinterControlState.Recovering;
        operation.SenderIsolation = operation.OwnerToken.HasValue && operation.OwnerHeartbeatAtUtc > DateTime.UtcNow - OwnerLiveness
            ? PrinterSenderIsolation.Pending : PrinterSenderIsolation.ExternalVerificationRequired;
        barrier.PhysicalControlRequiresReconciliation = true;
        await PersistRecoveryAsync(operation, barrier, actor, "recovery_requested", ct);
        return Map(operation, true);
    }

    private async Task AuthorizeEmergencyActorAsync(Guid printerId, string actor, CancellationToken ct)
    {
        if (!Guid.TryParse(actor, out Guid userId) ||
            !await db.Users.AsNoTracking().AnyAsync(user => user.Id == userId && user.IsActive, ct))
        {
            throw Error(403, "forbidden", "An active user is required.");
        }

        bool admin = await db.UserRoles.AsNoTracking().AnyAsync(
            role => role.UserId == userId && role.IsActive && role.Role.Name == PrintFarmerPermissions.FarmAdminRole, ct);
        if (!admin && !await authentication.HasPermissionAsync(userId, "queue", "cancel"))
        {
            throw Error(403, "forbidden", "Queue cancel permission is required.");
        }

        if (!await authorization.CanActorAccessPrinterAsync(actor, printerId, PrinterGroupAccessLevel.Submit, ct))
        {
            throw Error(404, "not_found", "Printer not found.");
        }
    }

    public async Task<PrinterEmergencyStopLease?> PrepareEmergencyStopAsync(Guid printerId, string actor, CancellationToken ct)
    {
        Guid attemptId = Guid.NewGuid();
        for (int retry = 0; retry < 8; retry++)
        {
            db.ChangeTracker.Clear();
            await AuthorizeEmergencyActorAsync(printerId, actor, ct);
            try
            {
                await ImportLegacyAsync(printerId, ct);
                PrinterDispatchState? barrier = await db.PrinterDispatchStates.SingleOrDefaultAsync(s => s.PrinterId == printerId, ct);
                Printer? printer = await db.Printers.SingleOrDefaultAsync(p => p.Id == printerId, ct);
                if (barrier?.PhysicalControlCommandId is not Guid id || printer?.Backend != (int)PrinterBackend.Moonraker)
                {
                    return null;
                }

                PrinterControlOperation? operation = await db.PrinterControlOperations.SingleOrDefaultAsync(o => o.Id == id && o.PrinterId == printerId, ct);
                if (operation is null || operation.Settled)
                {
                    return null;
                }

                await PreserveLegacyEmergencyUncertaintyAsync(operation, ct);
                string identity = PrinterControlIntent.ConfigurationIdentity(printer);
                db.PrinterEmergencyStopAttempts.Add(new PrinterEmergencyStopAttempt
                {
                    Id = attemptId, OperationId = id, PrinterId = printerId, ActorSubject = actor,
                    ConfigurationIdentity = identity, CreatedAtUtc = DateTime.UtcNow,
                    Delivery = PrinterEmergencyStopDelivery.Pending,
                });
                operation.RecoveryFromRevision = operation.Revision;
                operation.RecoveryActorSubject = actor;
                operation.RecoveryRequestedAtUtc = DateTime.UtcNow;
                operation.State = PrinterControlState.Recovering;
                operation.SenderIsolation = PrinterSenderIsolation.ExternalVerificationRequired;
                operation.FailureCode = "emergency_stop_requested";
                operation.FailureMessage = "Emergency senders and physical clearance still require verification.";
                barrier.PhysicalControlRequiresReconciliation = true;
                db.Entry(printer).Property(p => p.Revision).IsModified = true;
                db.Entry(barrier).Property(b => b.Revision).IsModified = true;
                await PersistAsync(operation, barrier, actor, "emergency_stop_requested", ct, attemptId);
                return new(id, attemptId, identity);
            }
            catch (DbUpdateConcurrencyException)
            {
                // Retry admission/CAS only. No network request has been issued.
            }
        }

        throw Error(503, "emergency_stop_not_sent", "Emergency admission could not acquire a current fence.");
    }

    public async Task<bool> CommitEmergencyStopSendAsync(Guid printerId, PrinterEmergencyStopLease lease, string actor, CancellationToken ct)
    {
        for (int retry = 0; retry < 8; retry++)
        {
            db.ChangeTracker.Clear();
            await AuthorizeEmergencyActorAsync(printerId, actor, ct);
            PrinterControlOperation operation = await LoadOperationAsync(printerId, lease.OperationId, ct);
            PrinterDispatchState barrier = await LoadBarrierAsync(printerId, ct);
            PrinterEmergencyStopAttempt attempt = await db.PrinterEmergencyStopAttempts.SingleAsync(a => a.Id == lease.AttemptId && a.OperationId == operation.Id, ct);
            Printer? printer = await db.Printers.SingleOrDefaultAsync(p => p.Id == printerId, ct);
            if (operation.State != PrinterControlState.Recovering || barrier.PhysicalControlCommandId != operation.Id ||
                attempt.ActorSubject != actor || attempt.Delivery != PrinterEmergencyStopDelivery.Pending ||
                attempt.SendCommittedAtUtc.HasValue || printer is null ||
                PrinterControlIntent.ConfigurationIdentity(printer) != lease.ConfigurationIdentity)
            {
                return false;
            }

            attempt.SendCommittedAtUtc = DateTime.UtcNow;
            db.Entry(printer).Property(p => p.Revision).IsModified = true;
            db.Entry(barrier).Property(b => b.Revision).IsModified = true;
            try
            {
                await PersistAsync(operation, barrier, actor, "emergency_stop_send_committed", ct, lease.AttemptId);
                return true;
            }
            catch (DbUpdateConcurrencyException)
            {
            }
        }

        return false;
    }

    public async Task FinishEmergencyStopAsync(Guid printerId, PrinterEmergencyStopLease lease, PrinterEmergencyStopDelivery delivery, CancellationToken ct)
    {
        if (delivery is not (PrinterEmergencyStopDelivery.Accepted or PrinterEmergencyStopDelivery.NotSent or PrinterEmergencyStopDelivery.Unknown))
        {
            throw new ArgumentOutOfRangeException(nameof(delivery));
        }

        for (int retry = 0; retry < 8; retry++)
        {
            db.ChangeTracker.Clear();
            PrinterControlOperation? operation = await db.PrinterControlOperations.SingleOrDefaultAsync(o => o.Id == lease.OperationId && o.PrinterId == printerId, ct);
            PrinterDispatchState? barrier = await db.PrinterDispatchStates.SingleOrDefaultAsync(s => s.PrinterId == printerId, ct);
            PrinterEmergencyStopAttempt? attempt = await db.PrinterEmergencyStopAttempts.SingleOrDefaultAsync(a => a.Id == lease.AttemptId && a.OperationId == lease.OperationId, ct);
            if (operation is null || operation.Settled || barrier?.PhysicalControlCommandId != operation.Id ||
                attempt?.Delivery != PrinterEmergencyStopDelivery.Pending)
            {
                return;
            }

            attempt.Delivery = delivery;
            attempt.CompletedAtUtc = DateTime.UtcNow;
            await RefreshSenderIsolationAsync(operation, ct);
            operation.FailureCode = operation.SenderIsolation == PrinterSenderIsolation.ExternalVerificationRequired
                ? "emergency_stop_outcome_unknown" : delivery == PrinterEmergencyStopDelivery.Accepted
                    ? "emergency_stop_accepted" : "emergency_stop_not_sent";
            db.Entry(barrier).Property(b => b.Revision).IsModified = true;
            try
            {
                await PersistAsync(operation, barrier, attempt.ActorSubject, "emergency_stop_delivery_recorded", ct, lease.AttemptId);
                return;
            }
            catch (DbUpdateConcurrencyException) when (retry < 7)
            {
                // Retry evidence persistence only, never the emergency HTTP request.
            }
        }
    }

    private async Task PreserveLegacyEmergencyUncertaintyAsync(PrinterControlOperation operation, CancellationToken ct)
    {
        if (operation.FailureCode == "emergency_stop_outcome_unknown" &&
            !await db.PrinterEmergencyStopAttempts.AnyAsync(a => a.OperationId == operation.Id, ct))
        {
            operation.EmergencyStopInFlight = true;
        }
    }

    private async Task RefreshSenderIsolationAsync(PrinterControlOperation operation, CancellationToken ct)
    {
        await PreserveLegacyEmergencyUncertaintyAsync(operation, ct);
        List<PrinterEmergencyStopAttempt> attempts = await db.PrinterEmergencyStopAttempts.Where(a => a.OperationId == operation.Id).ToListAsync(ct);
        bool unresolved = operation.EmergencyStopInFlight || attempts.Any(a =>
            a.Delivery is PrinterEmergencyStopDelivery.Pending or PrinterEmergencyStopDelivery.Unknown);
        operation.SenderIsolation = unresolved ? PrinterSenderIsolation.ExternalVerificationRequired
            : operation.SenderIsolatedAtUtc.HasValue || (operation.OwnerToken is null && operation.SendCommittedAtUtc is null)
                ? PrinterSenderIsolation.Confirmed
                : operation.OwnerHeartbeatAtUtc > DateTime.UtcNow - OwnerLiveness
                    ? PrinterSenderIsolation.Pending : PrinterSenderIsolation.ExternalVerificationRequired;
    }

    public async Task<PrinterControlOperationDto> CompleteRecoveryAsync(
        Guid printerId, Guid operationId, string revision, string actor,
        PrinterControlRecoveryRequest request, CancellationToken ct)
    {
        PrinterControlOperation operation = await LoadOperationAsync(printerId, operationId, ct);
        RequireRevision(operation, revision);
        PrinterDispatchState barrier = await LoadBarrierAsync(printerId, ct);
        RequireOwner(barrier, operation);
        await RequireIdleBarrierAsync(barrier, ct);
        await PreserveLegacyEmergencyUncertaintyAsync(operation, ct);
        List<PrinterEmergencyStopAttempt> emergencyAttempts = await db.PrinterEmergencyStopAttempts.Where(a => a.OperationId == operation.Id).ToListAsync(ct);
        bool serviceConfirmed = request.SenderIsolation == "ServiceConfirmed" &&
            operation.SenderIsolation == PrinterSenderIsolation.Confirmed && !operation.EmergencyStopInFlight &&
            !emergencyAttempts.Any(a => a.Delivery is PrinterEmergencyStopDelivery.Pending or PrinterEmergencyStopDelivery.Unknown);
        bool externalVerified = request.SenderIsolation == "ExternallyVerified";
        if (operation.State != PrinterControlState.Recovering ||
            (!serviceConfirmed && !externalVerified) ||
            !request.ControllerQueueCleared || !request.PhysicallyStationary ||
            !EvidenceValid(request.Reason) || !EvidenceValid(request.SenderIsolationEvidence) ||
            !EvidenceValid(request.PhysicalEvidence))
        {
            throw Error(409, "recovery_prerequisite", "Recovery requires sender isolation, queue clearance and independent physical evidence.");
        }

        string evidence = JsonSerializer.Serialize(request, JsonOptions);
        if (evidence.Length > 8192)
        {
            throw Error(409, "recovery_prerequisite", "Encoded recovery evidence exceeds 8192 characters; provide a concise attestation.");
        }

        operation.RecoveryEvidenceJson = evidence;
        operation.RecoveryActorSubject = actor;
        operation.RecoveryFromRevision = operation.Revision;
        operation.State = PrinterControlState.Recovered;
        operation.CompletedAtUtc = DateTime.UtcNow;
        operation.CompletionEvidence = PrinterControlEvidence.OperatorVerifiedRecovery;
        if (externalVerified)
        {
            operation.EmergencyStopInFlight = false;
            foreach (PrinterEmergencyStopAttempt attempt in emergencyAttempts.Where(a =>
                a.Delivery is PrinterEmergencyStopDelivery.Pending or PrinterEmergencyStopDelivery.Unknown))
            {
                attempt.Delivery = PrinterEmergencyStopDelivery.ExternallyIsolated;
                attempt.CompletedAtUtc = DateTime.UtcNow;
            }
        }

        // ExternallyVerified is an operator attestation, not a fabricated service acknowledgement.
        ClearBarrier(barrier);
        await PersistRecoveryAsync(operation, barrier, actor, "operator_recovered", ct);
        return Map(operation, false);
    }

    public async Task<PrinterControlOperation?> ClaimAsync(Guid id, Guid owner, CancellationToken ct)
    {
        DateTime now = DateTime.UtcNow;
        DateTime cutoff = now - OwnerLiveness;

        // Claim metadata is private. Atomic ownership CAS does not change the public
        // receipt revision; the audited Running transition does so before any send.
        int claimed = await db.PrinterControlOperations.Where(o => o.Id == id &&
                o.State == PrinterControlState.Queued && o.SendCommittedAtUtc == null &&
                (o.OwnerToken == null || o.OwnerHeartbeatAtUtc == null || o.OwnerHeartbeatAtUtc < cutoff))
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(o => o.OwnerToken, owner)
                .SetProperty(o => o.OwnerHeartbeatAtUtc, now), ct);
        db.ChangeTracker.Clear();
        return claimed == 1
            ? await db.PrinterControlOperations.AsNoTracking().SingleAsync(o => o.Id == id, ct)
            : null;
    }

    public async Task<bool> CommitSendAsync(Guid id, Guid owner, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        PrinterControlOperation operation = await db.PrinterControlOperations.SingleAsync(o => o.Id == id, ct);
        if (operation.OwnerToken != owner || operation.State != PrinterControlState.Queued || operation.SendCommittedAtUtc.HasValue)
        {
            return false;
        }

        await AuthorizeActorAsync(operation.PrinterId, operation.ActorSubject, ct);
        Printer printer = await db.Printers.SingleAsync(p => p.Id == operation.PrinterId, ct);
        ValidatePrinter(printer);
        if (PrinterControlIntent.ConfigurationIdentity(printer) != operation.PrinterConfigurationIdentity)
        {
            throw Error(409, "printer_configuration_changed", "Printer configuration changed before send.");
        }

        db.Entry(printer).Property(p => p.Revision).IsModified = true;
        PrinterDispatchState barrier = await LoadBarrierAsync(operation.PrinterId, ct);
        RequireOwner(barrier, operation);
        await RequireIdleBarrierAsync(barrier, ct);

        // Force a barrier CAS even though its identity is unchanged; a concurrent lifecycle
        // claim cannot slip between revalidation and the irreversible send marker.
        barrier.PhysicalControlStartedAtUtc = DateTime.UtcNow;
        operation.State = PrinterControlState.Running;
        operation.SendCommittedAtUtc = operation.StartedAtUtc = DateTime.UtcNow;
        operation.OwnerHeartbeatAtUtc = DateTime.UtcNow;
        operation.CorrelationId = operation.Id;
        await PersistAsync(operation, barrier, operation.ActorSubject, "send_committed", ct);
        return true;
    }

    public async Task SetOutcomeAsync(Guid id, Guid owner, bool success, string? failure, CancellationToken ct)
    {
        PrinterControlOperation? operation = await db.PrinterControlOperations.SingleOrDefaultAsync(o => o.Id == id, ct);
        if (operation is null || operation.OwnerToken != owner || operation.Settled ||
            operation.State == PrinterControlState.Recovering)
        {
            return;
        }

        PrinterDispatchState barrier = await LoadBarrierAsync(operation.PrinterId, ct);
        if (barrier.PhysicalControlCommandId != operation.Id)
        {
            return;
        }

        bool notSent = !operation.SendCommittedAtUtc.HasValue;
        if (success && (notSent || operation.CorrelationId != id))
        {
            return;
        }

        operation.State = success ? PrinterControlState.Succeeded : notSent ? PrinterControlState.Failed : PrinterControlState.Unknown;
        operation.CompletionEvidence = success ? PrinterControlEvidence.MotionQueueDrained : notSent ? PrinterControlEvidence.NotSent : PrinterControlEvidence.None;
        operation.FailureCode = failure;
        operation.FailureMessage = failure is null ? null : notSent ? "The operation was not sent." : "Physical outcome is unknown; explicit recovery is required.";
        operation.CompletedAtUtc = operation.Settled ? DateTime.UtcNow : null;
        barrier.PhysicalControlRequiresReconciliation = !operation.Settled;
        if (operation.Settled)
        {
            ClearBarrier(barrier);
        }

        await PersistAsync(operation, barrier, operation.ActorSubject, success ? "succeeded" : notSent ? "not_sent" : "unknown", ct);
    }

    public async Task MaintainOwnerAsync(Guid id, Guid owner, bool isolated, CancellationToken ct)
    {
        PrinterControlOperation? operation = await db.PrinterControlOperations.SingleOrDefaultAsync(o => o.Id == id, ct);
        if (operation is null || operation.OwnerToken != owner || operation.Settled)
        {
            return;
        }

        if (isolated && operation.State == PrinterControlState.Recovering)
        {
            bool alreadyQuiesced = operation.SenderIsolatedAtUtc.HasValue;
            bool legacyUncertainty = operation.EmergencyStopInFlight;
            PrinterSenderIsolation previous = operation.SenderIsolation;
            PrinterDispatchState barrier = await LoadBarrierAsync(operation.PrinterId, ct);
            RequireOwner(barrier, operation);
            operation.SenderIsolatedAtUtc ??= DateTime.UtcNow;
            await RefreshSenderIsolationAsync(operation, ct);
            if (alreadyQuiesced && previous == operation.SenderIsolation && legacyUncertainty == operation.EmergencyStopInFlight)
            {
                return;
            }

            operation.OwnerHeartbeatAtUtc = DateTime.UtcNow;
            await PersistAsync(operation, barrier, operation.RecoveryActorSubject ?? operation.ActorSubject, "motion_sender_quiesced", ct);
        }
        else
        {
            // Liveness is private sender metadata, not a public operation revision. It must
            // not invalidate an operator's If-Match every second while they inspect evidence.
            if (db.Database.IsRelational())
            {
                await db.PrinterControlOperations.Where(o => o.Id == id && o.OwnerToken == owner)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(o => o.OwnerHeartbeatAtUtc, DateTime.UtcNow), ct);
            }
            else
            {
                operation.OwnerHeartbeatAtUtc = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
            }
        }
    }

    public async Task ReconcileOrphansAsync(CancellationToken ct)
    {
        DateTime cutoff = DateTime.UtcNow - OwnerLiveness;
        List<PrinterControlOperation> operations = await db.PrinterControlOperations.Where(o =>
            (o.State == PrinterControlState.Running ||
             (o.State == PrinterControlState.Recovering && o.SenderIsolation == PrinterSenderIsolation.Pending)) &&
            (o.OwnerHeartbeatAtUtc == null || o.OwnerHeartbeatAtUtc < cutoff)).ToListAsync(ct);
        foreach (PrinterControlOperation operation in operations)
        {
            PrinterDispatchState barrier = await LoadBarrierAsync(operation.PrinterId, ct);
            if (barrier.PhysicalControlCommandId != operation.Id)
            {
                continue;
            }

            if (operation.State == PrinterControlState.Running)
            {
                operation.State = PrinterControlState.Unknown;
                operation.FailureCode = "sender_unavailable";
                operation.FailureMessage = "The sender is unavailable; no physical command will be replayed.";
            }
            else
            {
                operation.SenderIsolation = PrinterSenderIsolation.ExternalVerificationRequired;
            }

            barrier.PhysicalControlRequiresReconciliation = true;
            await PersistAsync(operation, barrier, operation.ActorSubject, "sender_unavailable", ct);
        }
    }

    public async Task ImportLegacyAsync(Guid printerId, CancellationToken ct)
    {
        PrinterDispatchState? barrier = await db.PrinterDispatchStates.SingleOrDefaultAsync(s => s.PrinterId == printerId, ct);
        if (barrier?.PhysicalControlCommandId is not Guid id || barrier.PhysicalControlAttemptId.HasValue ||
            barrier.ActiveDispatchAttemptId.HasValue || barrier.ActiveJobId.HasValue ||
            !await db.Printers.AnyAsync(p => p.Id == printerId && p.Backend == (int)PrinterBackend.Moonraker, ct) ||
            await db.PrintJobs.WhereOccupiesPrinter().AnyAsync(j => j.AssignedPrinterId == printerId, ct) ||
            await db.PrinterControlOperations.AnyAsync(o => o.Id == id, ct))
        {
            return;
        }

        PrinterControlKind? kind = barrier.PhysicalControlOperation switch
        {
            "home" => PrinterControlKind.HomeAll,
            "home_xy" => PrinterControlKind.HomeXY,
            "home_z" => PrinterControlKind.HomeZ,
            "move" => PrinterControlKind.Jog,
            "move_to" => PrinterControlKind.MoveTo,
            _ => null,
        };
        if (kind is null)
        {
            return;
        }

        var operation = new PrinterControlOperation
        {
            Id = id,
            PrinterId = printerId,
            Kind = kind.Value,
            ActorSubject = barrier.PhysicalControlActorSubject ?? "legacy:unknown",
            NormalizedIntent = "legacy:unknown",
            State = PrinterControlState.Unknown,
            CreatedAtUtc = barrier.PhysicalControlStartedAtUtc ?? DateTime.UtcNow,
            StartedAtUtc = barrier.PhysicalControlStartedAtUtc,
            SendCommittedAtUtc = barrier.PhysicalControlStartedAtUtc ?? DateTime.UtcNow,
            SenderIsolation = PrinterSenderIsolation.ExternalVerificationRequired,
            FailureCode = "legacy_outcome_unknown",
            FailureMessage = "Retained legacy motion barrier requires externally verified recovery.",
        };
        barrier.PhysicalControlRequiresReconciliation = true;
        db.PrinterControlOperations.Add(operation);

        // Even an already-reconciling barrier must participate in the import CAS.
        // A concurrent release/replacement may not leave a fabricated orphan receipt.
        db.Entry(barrier).Property(state => state.Revision).IsModified = true;
        await PersistAsync(operation, barrier, operation.ActorSubject, "legacy_imported", ct);
    }

    public static async Task<Dictionary<Guid, PrinterPhysicalControlDto>> ProjectAsync(
        AppDbContext db, Guid[] printerIds, CancellationToken ct)
    {
        await using IDbContextTransaction? snapshot = await BeginReadSnapshotAsync(db, ct);
        var rows = await (
            from printer in db.Printers.AsNoTracking()
            where printerIds.Contains(printer.Id)
            join state in db.PrinterDispatchStates.AsNoTracking() on printer.Id equals state.PrinterId into states
            from barrier in states.DefaultIfEmpty()
            join record in db.PrinterControlOperations.AsNoTracking()
                on new { Id = barrier == null ? null : barrier.PhysicalControlCommandId, PrinterId = printer.Id }
                equals new { Id = (Guid?)record.Id, record.PrinterId } into records
            from operation in records.DefaultIfEmpty()
            select new { printer.Id, printer.Backend, Barrier = barrier, Operation = operation }).ToListAsync(ct);
        return rows.ToDictionary(row => row.Id, row => new PrinterPhysicalControlDto(
            row.Backend == (int)PrinterBackend.Moonraker ? Enum.GetValues<PrinterControlKind>() : [],
            row.Barrier?.PhysicalControlCommandId.HasValue == true, row.Operation?.Id, row.Operation?.State,
            row.Operation?.RequiresRecovery == true || row.Barrier?.PhysicalControlRequiresReconciliation == true));
    }

    private async Task PersistRecoveryAsync(PrinterControlOperation operation, PrinterDispatchState barrier, string actor, string action, CancellationToken ct)
    {
        try
        {
            await PersistAsync(operation, barrier, actor, action, ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw Error(412, "stale_recovery", "The operation changed; fetch its current revision.");
        }
    }

    private async Task PersistAsync(PrinterControlOperation operation, PrinterDispatchState barrier, string actor, string action, CancellationToken ct, Guid? emergencyAttemptId = null)
    {
        await using QueueOutboxTransactionScope transaction = await QueueOutboxTransactionScope.BeginAsync(db, ct);
        operation.UpdatedAtUtc = DateTime.UtcNow;
        long nextRevision = db.Entry(operation).State == EntityState.Added ? 1 : operation.Revision + 1;
        QueueAuditWriter.Add(db, actor, QueueAuditOperations.PhysicalControl,
            operation.RequiresRecovery ? QueueAuditOutcomes.Unknown : QueueAuditOutcomes.Success,
            nameof(PrinterControlOperation), operation.Id, operation.PrinterId,
            reasonCode: operation.FailureCode, dispatchStateRowVersion: barrier.RowVersion,
            detail: new { operationId = operation.Id, action, revision = nextRevision, operation.RecoveryFromRevision, emergencyAttemptId });
        db.QueueDispatchOutbox.Add(new QueueDispatchOutbox
        {
            Id = Guid.NewGuid(),
            Sequence = await sequence.AllocateAsync(db, ct),
            AggregateType = nameof(PrinterControlOperation),
            AggregateId = operation.Id,
            PrinterId = operation.PrinterId,
            EventType = EventType,
            PayloadJson = JsonSerializer.Serialize(
                new PrinterControlInvalidation(operation.PrinterId, operation.Id,
                Convert.ToBase64String(RevisionETag.EncodeBytes(nextRevision))), JsonOptions),
            CreatedAtUtc = operation.UpdatedAtUtc,
            Status = QueueOutboxEventStatus.Pending,
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private Task<PrinterDispatchState> LoadBarrierAsync(Guid printerId, CancellationToken ct) =>
        LoadBarrierCoreAsync(printerId, ct);

    private async Task<PrinterDispatchState> LoadBarrierCoreAsync(Guid printerId, CancellationToken ct) =>
        await db.PrinterDispatchStates.SingleOrDefaultAsync(s => s.PrinterId == printerId, ct)
        ?? throw Error(503, "admission_unavailable", "Printer dispatch state is unavailable.");

    private async Task<PrinterControlOperation> LoadOperationAsync(Guid printerId, Guid id, CancellationToken ct) =>
        await db.PrinterControlOperations.SingleOrDefaultAsync(o => o.PrinterId == printerId && o.Id == id, ct)
        ?? throw Error(404, "not_found", "Operation not found.");

    private async Task RequireIdleBarrierAsync(PrinterDispatchState barrier, CancellationToken ct)
    {
        if (barrier.ActiveDispatchAttemptId.HasValue || barrier.ActiveJobId.HasValue ||
            await db.PrintJobs.WhereOccupiesPrinter().AnyAsync(j => j.AssignedPrinterId == barrier.PrinterId, ct))
        {
            throw Error(409, "printer_busy", "An active dispatch owns this printer.");
        }
    }

    private static void ValidatePrinter(Printer printer)
    {
        if (printer.Backend != (int)PrinterBackend.Moonraker)
        {
            throw Error(422, "unsupported", "Durable motion control is supported only by Moonraker.");
        }

        if (!printer.IsEnabled || printer.InMaintenance)
        {
            throw Error(409, "printer_unavailable", "The printer is disabled or in maintenance.");
        }
    }

    private static void RequireOwner(PrinterDispatchState barrier, PrinterControlOperation operation)
    {
        if (barrier.PhysicalControlCommandId != operation.Id || barrier.PhysicalControlAttemptId.HasValue)
        {
            throw Error(409, "physical_control_barrier", "This operation does not own the current printer barrier.");
        }
    }

    private static void RequireRevision(PrinterControlOperation operation, string revision)
    {
        if (revision != $"\"{Convert.ToBase64String(RevisionETag.EncodeBytes(operation.Revision))}\"")
        {
            throw Error(412, "stale_recovery", "The operation changed; fetch its current revision.");
        }
    }

    private static bool EvidenceValid(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 2000;

    private static PrinterControlException Error(int status, string code, string message) => new(status, code, message);

    private static void ClearBarrier(PrinterDispatchState barrier)
    {
        barrier.PhysicalControlCommandId = null;
        barrier.PhysicalControlAttemptId = null;
        barrier.PhysicalControlOperation = null;
        barrier.PhysicalControlActorSubject = null;
        barrier.PhysicalControlStartedAtUtc = null;
        barrier.PhysicalControlRequiresReconciliation = false;
    }

    public static PrinterControlOperationDto Map(PrinterControlOperation operation, bool held) => new(
        operation.Id, operation.PrinterId, operation.Kind, operation.X, operation.Y, operation.Z, operation.F,
        operation.State, Convert.ToBase64String(RevisionETag.EncodeBytes(operation.Revision)),
        DateTime.SpecifyKind(operation.CreatedAtUtc, DateTimeKind.Utc), DateTime.SpecifyKind(operation.UpdatedAtUtc, DateTimeKind.Utc),
        operation.StartedAtUtc is DateTime started ? DateTime.SpecifyKind(started, DateTimeKind.Utc) : null,
        operation.CompletedAtUtc is DateTime completed ? DateTime.SpecifyKind(completed, DateTimeKind.Utc) : null,
        held, operation.RequiresRecovery, operation.CompletionEvidence,
        operation.FailureCode is null ? null : new(operation.FailureCode, operation.FailureMessage ?? "Physical outcome unknown."),
        EffectiveSenderIsolation(operation));

    private static PrinterSenderIsolation EffectiveSenderIsolation(PrinterControlOperation operation) =>
        operation.State == PrinterControlState.Recovering && operation.EmergencyStopInFlight
            ? PrinterSenderIsolation.ExternalVerificationRequired : operation.SenderIsolation;
}
