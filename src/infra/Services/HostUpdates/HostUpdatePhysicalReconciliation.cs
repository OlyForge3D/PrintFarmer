using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Services.HostUpdates;

/// <summary>
/// Stable codes for the physical printer command reconciliation gate (issue #2999, runbook step 5).
/// Recovery never replays, cancels, or issues a printer command; it only refuses to reopen admission
/// until an operator has recorded that every affected printer was physically reconciled.
/// </summary>
public static class HostUpdatePhysicalReconciliationCodes
{
    /// <summary>Detail suffix on a <see cref="HostUpdateRecoveryOutcome.FenceReleasePending"/> outcome: no reconciliation is recorded.</summary>
    public const string Pending = "physical_reconciliation_pending";

    /// <summary>Detail suffix prefix when the reconciliation record could not be read or failed integrity.</summary>
    public const string Unreadable = "physical_reconciliation_unreadable";

    /// <summary>A print job the database still reports as assigned, starting, printing or paused on the printer.</summary>
    public const string PrintJobActive = "print_job_active";

    /// <summary>A queued or leased physical command (start, control, bed-clear) whose delivery outcome is not settled.</summary>
    public const string PhysicalCommandInFlight = "physical_command_in_flight";

    /// <summary>A dispatch claim that is in progress, has an unknown outcome, or requires reconciliation.</summary>
    public const string DispatchOutcomeUncertain = "dispatch_outcome_uncertain";

    /// <summary>
    /// A durable physical-I/O barrier (move, control or start) still held on the printer's dispatch
    /// state, including one retained for manual reconciliation after its command row was dead-lettered.
    /// </summary>
    public const string PhysicalControlBarrier = "physical_control_barrier";

    /// <summary>The fixed replay policy reported to operators.</summary>
    public const string ReplayPolicy = "recovery_never_replays_or_issues_printer_commands";

    /// <summary>True when a fence-release-pending detail is blocked on the reconciliation gate.</summary>
    public static bool IsBlockedDetail(string? detail) =>
        detail is not null
        && (detail.Contains("|" + Pending, StringComparison.Ordinal) || detail.Contains("|" + Unreadable, StringComparison.Ordinal));
}

/// <summary>One unsettled physical outcome the database reports for a printer.</summary>
public sealed record HostUpdateUncertainPhysicalOutcome(string Kind, string Reference, string State);

/// <summary>A printer and every unsettled physical outcome the database reports for it (possibly none).</summary>
public sealed record HostUpdatePrinterReconciliationItem(
    Guid PrinterId,
    string? PrinterName,
    IReadOnlyList<HostUpdateUncertainPhysicalOutcome> UncertainOutcomes);

/// <summary>
/// Read-only, per-printer inventory of unsettled queue/start/move/control outcomes. It is evidence
/// for the operator's physical check, not proof of physical state: a database cannot observe what a
/// printer actually did, which is why the gate requires an explicit operator record.
/// </summary>
public sealed record HostUpdatePrinterCommandInventory(IReadOnlyList<HostUpdatePrinterReconciliationItem> Printers)
{
    public const string TokenPrefix = "physical-";

    public int UncertainOutcomeCount => Printers.Sum(printer => printer.UncertainOutcomes.Count);

    /// <summary>Canonical SHA-256 over the inventory bound to one recovery request.</summary>
    public string Hash(string releaseId, string requestId)
    {
        var canonical = new
        {
            schema = 1,
            releaseId,
            requestId,
            printers = Canonical(Printers).Select(printer => new
            {
                printerId = printer.PrinterId.ToString("D"),
                printerName = printer.PrinterName ?? string.Empty,
                outcomes = printer.UncertainOutcomes.Select(outcome => new { kind = outcome.Kind, reference = outcome.Reference, state = outcome.State }),
            }),
        };
        return Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(canonical)));
    }

    /// <summary>The token an operator must echo to record reconciliation of exactly this inventory.</summary>
    public string Token(string releaseId, string requestId) => TokenPrefix + Hash(releaseId, requestId)[..32];

    /// <summary>Printers ordered by id, each with outcomes ordered by kind then reference.</summary>
    public static IReadOnlyList<HostUpdatePrinterReconciliationItem> Canonical(IEnumerable<HostUpdatePrinterReconciliationItem> printers) =>
        [.. printers
            .OrderBy(printer => printer.PrinterId)
            .Select(printer => printer with
            {
                UncertainOutcomes = [.. printer.UncertainOutcomes
                    .OrderBy(outcome => outcome.Kind, StringComparer.Ordinal)
                    .ThenBy(outcome => outcome.Reference, StringComparer.Ordinal)
                    .ThenBy(outcome => outcome.State, StringComparer.Ordinal)],
            })];
}

/// <summary>Reads the per-printer inventory of unsettled physical outcomes. Implementations never write.</summary>
public interface IHostUpdatePrinterCommandInventoryReader
{
    Task<HostUpdatePrinterCommandInventory> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Builds the inventory from <see cref="AppDbContext"/> with no-tracking queries only. It never calls
/// <c>SaveChanges</c>, never touches a printer backend, and never changes a command's lease or status,
/// so an uncertain command is neither replayed nor cleared by recovery.
/// </summary>
public sealed class DbHostUpdatePrinterCommandInventoryReader(AppDbContext db) : IHostUpdatePrinterCommandInventoryReader
{
    private static readonly PrintJobStatus[] ActiveJobStatuses =
    [
        PrintJobStatus.Assigned,
        PrintJobStatus.Starting,
        PrintJobStatus.Printing,
        PrintJobStatus.Paused,
    ];

    public async Task<HostUpdatePrinterCommandInventory> ReadAsync(CancellationToken cancellationToken)
    {
        var printers = await db.Printers.AsNoTracking()
            .Select(printer => new { printer.Id, printer.Name })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var jobs = await db.PrintJobs.AsNoTracking()
            .Where(job => job.AssignedPrinterId != null && ActiveJobStatuses.Contains(job.Status))
            .Select(job => new { PrinterId = job.AssignedPrinterId!.Value, job.Id, job.Status })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var commands = await db.QueueDispatchOutbox.AsNoTracking()
            .Where(evt => evt.PrinterId != null
                && (evt.Status == QueueOutboxEventStatus.Pending || evt.Status == QueueOutboxEventStatus.Processing))
            .Select(evt => new { PrinterId = evt.PrinterId!.Value, evt.Id, evt.EventType, evt.Status })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var attempts = await db.QueueDispatchAttempts.AsNoTracking()
            .Where(attempt => attempt.RequiresReconciliation
                || attempt.Outcome == DispatchAttemptOutcome.InProgress
                || attempt.Outcome == DispatchAttemptOutcome.Unknown)
            .Select(attempt => new { attempt.PrinterId, attempt.Id, attempt.Outcome })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var barriers = await db.PrinterDispatchStates.AsNoTracking()
            .Where(state => state.PhysicalControlCommandId != null)
            .Select(state => new
            {
                state.PrinterId,
                CommandId = state.PhysicalControlCommandId!.Value,
                Operation = state.PhysicalControlOperation,
                state.PhysicalControlRequiresReconciliation,
            })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var outcomes = new Dictionary<Guid, List<HostUpdateUncertainPhysicalOutcome>>();
        void Add(Guid printerId, HostUpdateUncertainPhysicalOutcome outcome)
        {
            if (!outcomes.TryGetValue(printerId, out List<HostUpdateUncertainPhysicalOutcome>? list))
            {
                outcomes[printerId] = list = [];
            }

            list.Add(outcome);
        }

        foreach (var job in jobs)
        {
            Add(job.PrinterId, new(HostUpdatePhysicalReconciliationCodes.PrintJobActive, job.Id.ToString("D"), job.Status.ToString()));
        }

        foreach (var command in commands)
        {
            Add(command.PrinterId, new(HostUpdatePhysicalReconciliationCodes.PhysicalCommandInFlight, command.Id.ToString("D"), $"{command.EventType}:{command.Status}"));
        }

        foreach (var attempt in attempts)
        {
            Add(attempt.PrinterId, new(HostUpdatePhysicalReconciliationCodes.DispatchOutcomeUncertain, attempt.Id.ToString("D"), attempt.Outcome.ToString()));
        }

        foreach (var barrier in barriers)
        {
            string retention = barrier.PhysicalControlRequiresReconciliation ? "requires_reconciliation" : "held";
            Add(barrier.PrinterId, new(HostUpdatePhysicalReconciliationCodes.PhysicalControlBarrier, barrier.CommandId.ToString("D"), $"{barrier.Operation ?? "unknown"}:{retention}"));
        }

        var names = printers.ToDictionary(printer => printer.Id, printer => printer.Name);
        IEnumerable<HostUpdatePrinterReconciliationItem> items = names.Keys
            .Union(outcomes.Keys)
            .Select(id => new HostUpdatePrinterReconciliationItem(
                id,
                names.GetValueOrDefault(id),
                outcomes.TryGetValue(id, out List<HostUpdateUncertainPhysicalOutcome>? list) ? list : []));
        return new HostUpdatePrinterCommandInventory(HostUpdatePrinterCommandInventory.Canonical(items));
    }
}

/// <summary>
/// Durable operator record that every printer in the recorded inventory was physically reconciled
/// before admission reopens. Bound to one release and recovery request; <see cref="RecordHash"/>
/// covers every other field so a hand-edited record fails integrity instead of opening the fence.
/// </summary>
public sealed record HostUpdatePhysicalReconciliationRecord(
    string ReleaseId,
    string RequestId,
    string InventoryHash,
    IReadOnlyList<HostUpdatePrinterReconciliationItem> Printers,
    DateTimeOffset RecordedAt,
    string RecordHash)
{
    public static HostUpdatePhysicalReconciliationRecord Create(
        string releaseId,
        string requestId,
        HostUpdatePrinterCommandInventory inventory,
        DateTimeOffset recordedAt)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        IReadOnlyList<HostUpdatePrinterReconciliationItem> printers = HostUpdatePrinterCommandInventory.Canonical(inventory.Printers);
        string inventoryHash = new HostUpdatePrinterCommandInventory(printers).Hash(releaseId, requestId);
        return new(releaseId, requestId, inventoryHash, printers, recordedAt, ComputeRecordHash(releaseId, requestId, inventoryHash, recordedAt));
    }

    /// <summary>True when the stored hashes match the stored content.</summary>
    public bool IsIntact() =>
        Printers is not null
        && !string.IsNullOrEmpty(ReleaseId)
        && RequestId is not null
        && string.Equals(InventoryHash, new HostUpdatePrinterCommandInventory(Printers).Hash(ReleaseId, RequestId), StringComparison.Ordinal)
        && string.Equals(RecordHash, ComputeRecordHash(ReleaseId, RequestId, InventoryHash, RecordedAt), StringComparison.Ordinal);

    private static string ComputeRecordHash(string releaseId, string requestId, string inventoryHash, DateTimeOffset recordedAt) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"physical-reconciliation-v1\n{releaseId}\n{requestId}\n{inventoryHash}\n{recordedAt.UtcDateTime:O}")));
}

/// <summary>
/// Consulted by <see cref="HostUpdateRecoveryCoordinator"/> immediately before it would release the
/// admission fence after a rollback. Admission stays closed until this reports a matching record.
/// </summary>
public interface IHostUpdatePhysicalReconciliationGate
{
    /// <summary>
    /// True only when an intact record exists for exactly this release and request. Throws
    /// <see cref="InvalidDataException"/> when a record exists but fails integrity.
    /// </summary>
    Task<bool> IsRecordedAsync(string releaseId, string requestId, CancellationToken cancellationToken);
}

/// <summary>Persists and reads <see cref="HostUpdatePhysicalReconciliationRecord"/>s.</summary>
public interface IHostUpdatePhysicalReconciliationStore : IHostUpdatePhysicalReconciliationGate
{
    Task<HostUpdatePhysicalReconciliationRecord?> ReadAsync(string releaseId, CancellationToken cancellationToken);

    Task WriteAsync(HostUpdatePhysicalReconciliationRecord record, CancellationToken cancellationToken);
}

/// <summary>One atomically written JSON file per release under the protected state directory.</summary>
public sealed class FileHostUpdatePhysicalReconciliationStore(string rootDirectory) : IHostUpdatePhysicalReconciliationStore
{
    public const string InvalidRecord = "physical_reconciliation_record_invalid";

    public async Task<HostUpdatePhysicalReconciliationRecord?> ReadAsync(string releaseId, CancellationToken cancellationToken)
    {
        string path = PathFor(releaseId);
        if (!File.Exists(path))
        {
            return null;
        }

        string json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        HostUpdatePhysicalReconciliationRecord? record;
        try
        {
            record = JsonSerializer.Deserialize<HostUpdatePhysicalReconciliationRecord>(json);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(InvalidRecord, exception);
        }

        if (record is null || !record.IsIntact() || !string.Equals(record.ReleaseId, releaseId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(InvalidRecord);
        }

        return record;
    }

    public async Task<bool> IsRecordedAsync(string releaseId, string requestId, CancellationToken cancellationToken)
    {
        HostUpdatePhysicalReconciliationRecord? record = await ReadAsync(releaseId, cancellationToken).ConfigureAwait(false);
        return record is not null && string.Equals(record.RequestId, requestId, StringComparison.Ordinal);
    }

    public async Task WriteAsync(HostUpdatePhysicalReconciliationRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!record.IsIntact())
        {
            throw new InvalidDataException(InvalidRecord);
        }

        Directory.CreateDirectory(rootDirectory);
        string path = PathFor(record.ReleaseId);
        string json = JsonSerializer.Serialize(record);
        await Task.Run(() => HostUpdateDurableFile.WriteAllTextAtomic(path, json), cancellationToken).ConfigureAwait(false);
    }

    public const string PathOutsideRoot = "physical_reconciliation_path_outside_root";

    /// <summary>
    /// Maps a release id to a single file name inside <c>rootDirectory</c>. Every character outside
    /// <c>[A-Za-z0-9.-]</c> (including directory separators, drive colons and NUL) becomes <c>_</c>, so
    /// the segment can never be rooted or name a parent directory; the full path is then proven to
    /// stay directly under the root. Two ids that sanitize alike fail the record's release-id check
    /// on read, which keeps the fence closed rather than opening it.
    /// </summary>
    internal string PathFor(string releaseId)
    {
        ArgumentException.ThrowIfNullOrEmpty(releaseId);
        string safeReleaseId = new([.. releaseId.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' ? c : '_')]);
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));
        string path = Path.GetFullPath(Path.Join(root, $"{safeReleaseId}.physical-reconciliation.json"));
        if (!string.Equals(Path.GetDirectoryName(path), root, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(PathOutsideRoot);
        }

        return path;
    }
}

/// <summary>No root configured: nothing is ever recorded, so the gate keeps admission closed.</summary>
public sealed class UnconfiguredHostUpdatePhysicalReconciliationStore : IHostUpdatePhysicalReconciliationStore
{
    public Task<HostUpdatePhysicalReconciliationRecord?> ReadAsync(string releaseId, CancellationToken cancellationToken) =>
        Task.FromResult<HostUpdatePhysicalReconciliationRecord?>(null);

    public Task<bool> IsRecordedAsync(string releaseId, string requestId, CancellationToken cancellationToken) =>
        Task.FromResult(false);

    public Task WriteAsync(HostUpdatePhysicalReconciliationRecord record, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("root_directory_not_configured");
}
