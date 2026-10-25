using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Printers;
using Farm.Infrastructure.Services.Gcode.PrinterFiles;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.Queue;
using Microsoft.EntityFrameworkCore;

namespace Farm.Modules.Gcode.Services.Gcode;

/// <summary>
/// Single-location, durable, non-replayable destructive command.
/// Lock order: shared printer-file lease (scan/copy/delete), THEN physical actuation barrier,
/// THEN short database publication transaction. Release in reverse order. Never network I/O
/// inside a transaction. No caller may acquire a printer-file lease while holding the physical
/// barrier. External clients are outside both fences: weak consent explicitly accepts that race.
/// </summary>
public sealed class PrinterFileDeleteService(
    AppDbContext db,
    IPrinterFileAccessService access,
    IPrinterFileLeaseCoordinator leases,
    IBackendClientFactory backends,
    IPrintersRepository printers,
    IPrinterStatusClientFactory statuses,
    IPrinterPhysicalActuationService physical) : IPrinterFileDeleteService
{
    public async Task<PrinterFileDeleteOperation> CreateAsync(
        Guid actorId, string idempotencyKey, PrinterFileDeleteCommand command, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 200 ||
            idempotencyKey.Any(char.IsControl))
        {
            throw new PrinterFileCommandException("invalid_idempotency_key", 400);
        }

        // Same namespace as Copy/Queue prevents laundering a key across command kinds.
        string key = Hash(idempotencyKey);
        string payload = Hash(JsonSerializer.Serialize(new { Action = PrinterFileAction.Delete, Command = command }));
        PrinterFileOperation? existing = await db.PrinterFileOperations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.ActorId == actorId && x.CommandKeyHash == key, ct);
        if (existing is not null)
        {
            await RequireAuthorityAsync(actorId, existing.SourcePrinterId, ct);
            if (existing.Action != PrinterFileAction.Delete || existing.PayloadSha256 != payload)
            {
                throw new PrinterFileCommandException("idempotency_payload_mismatch");
            }

            return View(existing);
        }

        PrinterFileLocation location = await db.PrinterFileLocations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == command.LocationId, ct)
            ?? throw new PrinterFileCommandException("location_not_found", 404);
        await RequireAuthorityAsync(actorId, location.SourcePrinterId, ct);
        ValidateLocation(location, command.ObservationRevision);
        if (await db.PrinterFileOperations.AsNoTracking().AnyAsync(
                x =>
                x.LocationId == location.Id && x.Action == PrinterFileAction.Delete &&
                x.DeleteSentAtUtc != null && x.ObservationRevision >= location.ObservationRevision &&
                (x.State == PrinterFileOperationState.SendingDelete ||
                 x.State == PrinterFileOperationState.ReconcilingDelete), ct))
        {
            throw new PrinterFileCommandException("delete_reconciliation_required");
        }

        Printer printer = await RegisteredAsync(location.SourcePrinterId, ct);
        ISupportsSafePrinterFileDelete client = DeleteClient(printer);
        PrinterFileDeleteGuarantee guarantee = client.GetDeleteGuarantee(location.StorageRoot, location.RelativePath);
        if (guarantee == PrinterFileDeleteGuarantee.Unsupported ||
            (guarantee == PrinterFileDeleteGuarantee.NativeConditional && location.ProviderVersion is null))
        {
            throw new PrinterFileCommandException("delete_unsupported");
        }

        Snapshot snapshot = new(location.StorageRoot, location.RelativePath, location.SizeBytes,
            location.ModifiedValue, location.ProviderVersion, location.ProviderDigest, guarantee);
        PrinterFileOperation operation = new()
        {
            Id = Guid.NewGuid(),
            ActorId = actorId,
            CommandKeyHash = key,
            PayloadSha256 = payload,
            SourcePrinterId = location.SourcePrinterId,
            LocationId = location.Id,
            ObservationRevision = location.ObservationRevision,
            ConfigurationGeneration = location.ConfigurationGeneration,
            MutationGeneration = location.MutationGeneration,
            Action = PrinterFileAction.Delete,
            State = PrinterFileOperationState.AwaitingDeleteConfirmation,
            CommandJson = JsonSerializer.Serialize(snapshot),
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
        };
        db.PrinterFileOperations.Add(operation);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // A concurrent identical key may have won. Never execute its mutation here.
            db.Entry(operation).State = EntityState.Detached;
            existing = await db.PrinterFileOperations.AsNoTracking()
                .SingleOrDefaultAsync(x => x.ActorId == actorId && x.CommandKeyHash == key, ct);
            if (existing is null)
            {
                throw;
            }

            if (existing.Action != PrinterFileAction.Delete || existing.PayloadSha256 != payload)
            {
                throw new PrinterFileCommandException("idempotency_payload_mismatch");
            }

            return View(existing);
        }

        return View(operation);
    }

    public async Task<PrinterFileDeleteOperation?> GetAsync(
        Guid actorId, Guid operationId, CancellationToken ct = default)
    {
        PrinterFileOperation? operation = await AuthorizedOperationAsync(actorId, operationId, ct);
        return operation is null ? null : View(operation);
    }

    public async Task<PrinterFileDeleteOperation?> ConfirmAsync(
        Guid actorId, Guid operationId, PrinterFileDeleteConfirmation confirmation, CancellationToken ct = default)
    {
        PrinterFileOperation? operation = await AuthorizedOperationAsync(actorId, operationId, ct);
        if (operation is null)
        {
            return null;
        }

        Snapshot snapshot = Read(operation);
        string binding = Binding(operation);
        if (confirmation.ConfirmationBinding != binding || !confirmation.AcceptIrreversibleDelete ||
            (snapshot.Guarantee == PrinterFileDeleteGuarantee.CurrentExactPath && !confirmation.AcceptCurrentPathRace))
        {
            throw new PrinterFileCommandException("delete_confirmation_required");
        }

        if (operation.DeleteConfirmationSha256 is not null &&
            (operation.DeleteConfirmationSha256 != binding ||
             operation.CurrentPathRaceAccepted != confirmation.AcceptCurrentPathRace))
        {
            throw new PrinterFileCommandException("delete_confirmation_mismatch");
        }

        // CRITICAL: a persisted send boundary can NEVER lead to another mutation, even after a
        // process restart or if a client repeats this POST. Failure before boundary requires a new intent.
        if (operation.State != PrinterFileOperationState.AwaitingDeleteConfirmation || operation.DeleteSentAtUtc.HasValue)
        {
            return View(operation);
        }

        await using IPrinterFileLease? lease = await leases.TryAcquireAsync(operation.SourcePrinterId, true, ct);
        if (lease is null)
        {
            throw new PrinterFileCommandException("source_busy");
        }

        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(120));
        CancellationToken token = deadline.Token;
        PrinterActuationLease? barrier = null;
        bool crossedBoundary = false;
        bool completedBarrier = false;
        try
        {
            // Re-read after acquiring the exclusive slot: a second confirmer may have waited.
            await db.Entry(operation).ReloadAsync(token);
            if (operation.State != PrinterFileOperationState.AwaitingDeleteConfirmation ||
                operation.DeleteSentAtUtc.HasValue)
            {
                return View(operation);
            }

            await ValidateCurrentAsync(operation, lease, token);
            Printer printer = await RegisteredAsync(operation.SourcePrinterId, token);
            ISupportsSafePrinterFileDelete client = DeleteClient(printer);
            if (client.GetDeleteGuarantee(snapshot.StorageRoot, snapshot.RelativePath) != snapshot.Guarantee)
            {
                throw new PrinterFileCommandException("delete_guarantee_changed");
            }

            PrinterActuationResult acquired = await physical.AcquireDirectAsync(
                operation.SourcePrinterId, actorId.ToString(), "printer_file_delete", token);
            if (!acquired.Success || acquired.Lease is null)
            {
                throw new PrinterFileCommandException("physical_fence_conflict");
            }

            barrier = acquired.Lease;

            // Read metadata first, status LAST, while holding both application fences.
            PrinterFileInventoryEntry? entry = await ObserveAsync(printer, snapshot, token);
            if (entry is null)
            {
                operation.State = PrinterFileOperationState.Blocked;
                operation.ReasonCode = "observed_absent_no_send";
                operation.UpdatedAtUtc = DateTime.UtcNow;
                await db.SaveChangesAsync(token);
                return View(operation);
            }

            if (!Matches(snapshot, entry))
            {
                throw new PrinterFileCommandException("source_changed_reconfirm");
            }

            DateTime observedAt = DateTime.UtcNow;
            PrinterStatusDto status = await statuses.GetStatusClient(printer.Backend)
                .GetPrinterStatusAsync(printer, token);
            if (!PrinterFileIndexPolicy.IsConfirmedIdle(new(status, observedAt), DateTime.UtcNow))
            {
                throw new PrinterFileCommandException("fresh_confirmed_idle_required");
            }

            // Conservative: a reported current path (even with a contradictory idle flag) is
            // known in-use. Do not strip/case-fold a provider path and accidentally miss an alias.
            if (!string.IsNullOrEmpty(status.JobName) || !string.IsNullOrEmpty(status.FileName))
            {
                throw new PrinterFileCommandException("source_path_in_use");
            }

            await ValidateCurrentAsync(operation, lease, token);
            if (!(await physical.RevalidateDirectAsync(barrier, token)).Success)
            {
                throw new PrinterFileCommandException("physical_fence_lost");
            }

            // Durable irreversible consent and send boundary commit BEFORE any mutation.
            await using (var transaction = await db.Database.BeginTransactionAsync(token))
            {
                if (!await lease.FencePublicationAsync(db, token))
                {
                    throw new PrinterFileCommandException("source_ownership_lost");
                }

                // The transaction now owns the write barriers. A second connection's
                // IsCurrentAsync could wait on our own transaction; authority uses this context.
                await RequireAuthorityAsync(actorId, operation.SourcePrinterId, token);
                operation.DeleteConfirmationSha256 = binding;
                operation.DeleteConfirmedAtUtc = DateTime.UtcNow;
                operation.CurrentPathRaceAccepted = confirmation.AcceptCurrentPathRace;
                operation.DeleteSentAtUtc = DateTime.UtcNow;
                operation.WorkerId = barrier.CommandId;
                operation.State = PrinterFileOperationState.SendingDelete;
                operation.UpdatedAtUtc = DateTime.UtcNow;
                QueueAuditWriter.Add(db, actorId.ToString(), QueueAuditOperations.PrinterFileDelete, QueueAuditOutcomes.Success,
                    nameof(PrinterFileLocation), operation.LocationId, operation.SourcePrinterId,
                    reasonCode: "delete_send_boundary",
                    detail: new
                    {
                        operation.Id,
                        snapshot.Guarantee,
                        operation.ObservationRevision,
                        operation.ConfigurationGeneration,
                        idleObservedAtUtc = observedAt,
                        rawActivity = status.RawActivityState,
                    });

                // Treat an interrupted commit as a possible boundary too; never "undo" it into
                // a retryable command when the database might have committed.
                crossedBoundary = true;
                await db.SaveChangesAsync(token);
                await transaction.CommitAsync(token);
            }

            PrinterFileDeleteResult result;
            try
            {
                // Ownership loss/cancellation after boundary is uncertainty, not permission to retry.
                if (!await lease.IsCurrentAsync(token) ||
                    !await access.CanActAsync(actorId, operation.SourcePrinterId, PrinterFileAction.Delete, ct: token) ||
                    !(await physical.RevalidateDirectAsync(barrier, token)).Success)
                {
                    result = new(PrinterFileDeleteOutcome.OutcomeUnknown);
                }
                else
                {
                    result = await client.DeleteExactFileAsync(printer.BackendUrl, snapshot.StorageRoot,
                        snapshot.RelativePath, snapshot.ProviderVersion, printer.Credential, token);
                }
            }
            catch (Exception failure) when (failure is not OutOfMemoryException)
            {
                // The boundary, not exception type or a legacy bool, decides retry safety.
                result = new(PrinterFileDeleteOutcome.OutcomeUnknown);
            }

            await FinishAsync(operation, lease, result.Outcome, CancellationToken.None);
            if (result.Outcome == PrinterFileDeleteOutcome.OutcomeUnknown)
            {
                await physical.MarkDirectUnknownAsync(barrier, "delete_outcome_unknown", CancellationToken.None);
            }
            else
            {
                await physical.CompleteDirectAsync(
                    barrier,
                    result.Outcome == PrinterFileDeleteOutcome.Acknowledged,
                    result.Outcome == PrinterFileDeleteOutcome.Acknowledged ? null : "delete_not_acknowledged",
                    CancellationToken.None);
            }

            completedBarrier = true;
            return View(operation);
        }
        catch (PrinterFileCommandException failure) when (!crossedBoundary)
        {
            // A fresh intent and confirmation are required for detected changes or rejected guards.
            operation.State = PrinterFileOperationState.Blocked;
            operation.ReasonCode = failure.Reason;
            operation.UpdatedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
            return View(operation);
        }
        finally
        {
            if (barrier is not null && !completedBarrier)
            {
                if (crossedBoundary)
                {
                    // A failed ACK/catalog commit remains SendingDelete => OutcomeUnknown on restart.
                    await physical.MarkDirectUnknownAsync(barrier, "delete_outcome_unknown", CancellationToken.None);
                }
                else
                {
                    await physical.CompleteDirectAsync(barrier, false, "delete_not_sent", CancellationToken.None);
                }
            }
        }
    }

    public async Task<PrinterFileDeleteOperation?> ReconcileAsync(
        Guid actorId, Guid operationId, CancellationToken ct = default)
    {
        PrinterFileOperation? operation = await AuthorizedOperationAsync(actorId, operationId, ct);
        if (operation is null)
        {
            return null;
        }

        if (!operation.DeleteSentAtUtc.HasValue ||
            operation.State is not (PrinterFileOperationState.SendingDelete or PrinterFileOperationState.ReconcilingDelete))
        {
            return View(operation);
        }

        await using IPrinterFileLease? lease = await leases.TryAcquireAsync(operation.SourcePrinterId, true, ct);
        if (lease is null)
        {
            throw new PrinterFileCommandException("source_busy");
        }

        if (lease.ConfigurationGeneration != operation.ConfigurationGeneration)
        {
            throw new PrinterFileCommandException("source_configuration_changed");
        }

        Printer printer = await RegisteredAsync(operation.SourcePrinterId, ct);
        Snapshot snapshot = Read(operation);
        PrinterFileInventoryEntry? current = await ObserveAsync(printer, snapshot, lease.Token);
        await RequireAuthorityAsync(actorId, operation.SourcePrinterId, ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (!await lease.FencePublicationAsync(db, ct))
        {
            throw new PrinterFileCommandException("source_ownership_lost");
        }

        await db.Entry(operation).ReloadAsync(ct);
        if (operation.State is not (PrinterFileOperationState.SendingDelete or PrinterFileOperationState.ReconcilingDelete))
        {
            return View(operation);
        }

        // Current presence/absence cannot establish who deleted the historic version.
        // A present/recreated file gets a NEW observation and never inherits old consent.
        string reason = current is null ? "outcome_unknown_observed_absent" : "outcome_unknown_observed_present";
        if (current is not null && operation.ReasonCode != reason)
        {
            PrinterFileLocation location = await db.PrinterFileLocations.SingleAsync(x => x.Id == operation.LocationId, ct);
            location.ObservationRevision++;
            location.SizeBytes = current.SizeBytes;
            location.ModifiedValue = current.ModifiedValue;
            location.ProviderVersion = current.ProviderVersion;
            location.ProviderDigest = current.ProviderHash;
            location.LastSeenAtUtc = DateTime.UtcNow;
        }

        operation.State = PrinterFileOperationState.ReconcilingDelete;
        operation.ReasonCode = reason;
        operation.UpdatedAtUtc = DateTime.UtcNow;
        QueueAuditWriter.Add(db, actorId.ToString(), QueueAuditOperations.PrinterFileDelete, QueueAuditOutcomes.Unknown,
            nameof(PrinterFileLocation), operation.LocationId, operation.SourcePrinterId, reasonCode: reason,
            detail: new { operation.Id, snapshot.Guarantee, mutationReplayed = false });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return View(operation);
    }

    private async Task FinishAsync(PrinterFileOperation operation, IPrinterFileLease lease,
        PrinterFileDeleteOutcome outcome, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (!await lease.FencePublicationAsync(db, ct))
        {
            // ACK without a valid publication fence is not a committed tombstone.
            throw new PrinterFileCommandException("delete_publication_fence_lost");
        }

        await db.Entry(operation).ReloadAsync(ct);
        operation.State = outcome switch
        {
            PrinterFileDeleteOutcome.Acknowledged => PrinterFileOperationState.Deleted,
            PrinterFileDeleteOutcome.OutcomeUnknown => PrinterFileOperationState.ReconcilingDelete,
            _ => PrinterFileOperationState.Blocked,
        };
        operation.ReasonCode = outcome switch
        {
            PrinterFileDeleteOutcome.Acknowledged => "delete_acknowledged",
            PrinterFileDeleteOutcome.ObservedAbsent => "observed_absent_after_send",
            PrinterFileDeleteOutcome.Rejected => "delete_rejected",
            _ => "delete_outcome_unknown",
        };
        operation.UpdatedAtUtc = DateTime.UtcNow;
        if (outcome == PrinterFileDeleteOutcome.Acknowledged)
        {
            PrinterFileScanState scan = await db.PrinterFileScanStates
                .SingleAsync(x => x.SourcePrinterId == operation.SourcePrinterId, ct);

            // FencePublication uses ExecuteUpdate, so read the current Revision rather than a tracked old row.
            await db.Entry(scan).ReloadAsync(ct);
            scan.MutationGeneration++;
            PrinterFileLocation location = await db.PrinterFileLocations.SingleAsync(x => x.Id == operation.LocationId, ct);
            location.Availability = PrinterFileAvailability.Deleted;
            location.ObservationRevision++;
            location.MutationGeneration = scan.MutationGeneration;
        }

        string auditOutcome = outcome switch
        {
            PrinterFileDeleteOutcome.Acknowledged => QueueAuditOutcomes.Success,
            PrinterFileDeleteOutcome.Rejected => QueueAuditOutcomes.Denied,
            _ => QueueAuditOutcomes.Unknown,
        };
        QueueAuditWriter.Add(db, operation.ActorId.ToString(), QueueAuditOperations.PrinterFileDelete,
            auditOutcome,
            nameof(PrinterFileLocation), operation.LocationId, operation.SourcePrinterId,
            reasonCode: operation.ReasonCode, detail: new { operation.Id, outcome });

        // No GcodeFile, origin, other location or job is ever attached/removed by this service.
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private async Task ValidateCurrentAsync(PrinterFileOperation operation, IPrinterFileLease lease, CancellationToken ct)
    {
        await RequireAuthorityAsync(operation.ActorId, operation.SourcePrinterId, ct);
        if (!await lease.IsCurrentAsync(ct) || lease.ConfigurationGeneration != operation.ConfigurationGeneration)
        {
            throw new PrinterFileCommandException("source_ownership_lost");
        }

        PrinterFileLocation location = await db.PrinterFileLocations.AsNoTracking()
            .SingleAsync(x => x.Id == operation.LocationId, ct);
        ValidateLocation(location, operation.ObservationRevision);
        if (location.ConfigurationGeneration != operation.ConfigurationGeneration ||
            location.MutationGeneration != operation.MutationGeneration)
        {
            throw new PrinterFileCommandException("source_changed_reconfirm");
        }
    }

    private async Task<PrinterFileOperation?> AuthorizedOperationAsync(Guid actor, Guid id, CancellationToken ct)
    {
        PrinterFileOperation? operation = await db.PrinterFileOperations.SingleOrDefaultAsync(
            x => x.Id == id && x.ActorId == actor && x.Action == PrinterFileAction.Delete, ct);
        return operation is not null &&
            await access.CanActAsync(actor, operation.SourcePrinterId, PrinterFileAction.Delete, ct: ct)
            ? operation : null;
    }

    private async Task RequireAuthorityAsync(Guid actor, Guid source, CancellationToken ct)
    {
        if (!await access.CanActAsync(actor, source, PrinterFileAction.Delete, ct: ct))
        {
            throw new PrinterFileCommandException("location_not_found", 404);
        }
    }

    private async Task<Printer> RegisteredAsync(Guid id, CancellationToken ct) =>
        await printers.FindByIdWithIncludesAsync(id, ct)
        ?? throw new PrinterFileCommandException("location_not_found", 404);

    private ISupportsSafePrinterFileDelete DeleteClient(Printer printer)
    {
        if (!backends.IsBackendSupported((PrinterBackend)printer.Backend) ||
            backends.GetClient(printer.Backend) is not ISupportsSafePrinterFileDelete client ||
            backends.GetClient(printer.Backend) is not ISupportsFileInventory)
        {
            throw new PrinterFileCommandException("delete_unsupported");
        }

        return client;
    }

    private async Task<PrinterFileInventoryEntry?> ObserveAsync(Printer printer, Snapshot snapshot, CancellationToken ct)
    {
        if (backends.GetClient(printer.Backend) is not ISupportsFileInventory inventory)
        {
            throw new PrinterFileCommandException("delete_unsupported");
        }

        PrinterFileInventoryResult result = await inventory.GetFileInventoryAsync(
            printer.BackendUrl, printer.Credential, new() { Deadline = TimeSpan.FromSeconds(60) }, ct);

        // Full root completion is deliberately required; partial/failed scans prove nothing here.
        if (result.Outcome != PrinterFileInventoryOutcome.Complete ||
            !result.Coverage.Any(x => x.StorageRoot == snapshot.StorageRoot && x.Subtree.Length == 0 &&
                x.IsComplete && x.Evidence != PrinterFileInventoryEvidence.None))
        {
            throw new PrinterFileCommandException("source_observation_unavailable");
        }

        PrinterFileInventoryEntry[] entries = result.Entries.Where(x =>
            x.StorageRoot == snapshot.StorageRoot && x.RelativePath == snapshot.RelativePath).ToArray();
        if (entries.Length > 1)
        {
            throw new PrinterFileCommandException("source_observation_ambiguous");
        }

        return entries.SingleOrDefault();
    }

    private static bool Matches(Snapshot snapshot, PrinterFileInventoryEntry entry) =>
        snapshot.SizeBytes == entry.SizeBytes && snapshot.ModifiedValue == entry.ModifiedValue &&
        snapshot.ProviderVersion == entry.ProviderVersion && snapshot.ProviderDigest == entry.ProviderHash;

    private static void ValidateLocation(PrinterFileLocation location, long revision)
    {
        if (location.RegisteredPrinterId != location.SourcePrinterId ||
            location.Availability != PrinterFileAvailability.Available ||
            location.ObservationRevision != revision ||
            !PrinterInventoryPath.TryCanonicalizeRoot(location.StorageRoot, out string root) ||
            root != location.StorageRoot ||
            !PrinterInventoryPath.TryCanonicalizeRelativePath(location.RelativePath, out string path) ||
            path != location.RelativePath || path.Contains('%') ||
            location.IdentityKey != PrinterInventoryPath.ComputeIdentity(root, path))
        {
            throw new PrinterFileCommandException("source_changed_reconfirm");
        }
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static Snapshot Read(PrinterFileOperation operation) =>
        JsonSerializer.Deserialize<Snapshot>(operation.CommandJson)
        ?? throw new InvalidOperationException("Invalid durable delete snapshot.");

    private static string Binding(PrinterFileOperation operation) => Hash(JsonSerializer.Serialize(new
    {
        Purpose = "irreversible-printer-file-delete-v1",
        operation.ActorId,
        operation.Id,
        operation.SourcePrinterId,
        operation.LocationId,
        operation.ConfigurationGeneration,
        operation.ObservationRevision,
        operation.MutationGeneration,
        Snapshot = Read(operation),
    }));

    private static PrinterFileDeleteOperation View(PrinterFileOperation operation)
    {
        Snapshot snapshot = Read(operation);
        string outcome = operation.State switch
        {
            PrinterFileOperationState.Deleted => "Acknowledged",
            PrinterFileOperationState.SendingDelete or PrinterFileOperationState.ReconcilingDelete => "OutcomeUnknown",
            _ when operation.ReasonCode is "observed_absent_no_send" or "observed_absent_after_send" => "ObservedAbsent",
            _ when operation.State == PrinterFileOperationState.Blocked => "Blocked",
            _ => "AwaitingConfirmation",
        };
        return new(operation.Id, operation.Revision, operation.State, operation.LocationId,
            operation.SourcePrinterId, snapshot.StorageRoot, snapshot.RelativePath, operation.ObservationRevision,
            operation.ConfigurationGeneration, snapshot.Guarantee, Binding(operation), operation.CurrentPathRaceAccepted,
            operation.DeleteSentAtUtc, outcome, operation.ReasonCode);
    }

    private sealed record Snapshot(string StorageRoot, string RelativePath, long? SizeBytes,
        string? ModifiedValue, string? ProviderVersion, string? ProviderDigest, PrinterFileDeleteGuarantee Guarantee);
}
