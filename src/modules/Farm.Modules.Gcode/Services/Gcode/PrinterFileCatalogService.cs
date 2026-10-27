using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Printers;
using Farm.Infrastructure.Services.Gcode.PrinterFiles;
using Farm.Infrastructure.Services.Printers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Farm.Modules.Gcode.Services.Gcode;

/// <summary>Metadata-only discovery with explicit per-scope absence authority.</summary>
public sealed class PrinterFileCatalogService(
    IDbContextFactory<AppDbContext> dbFactory,
    IPrintersRepository printers,
    IBackendCapabilityFactory capabilities,
    IPrinterFileLeaseCoordinator leases,
    IPrinterStatusCacheReader statusCache,
    ILogger<PrinterFileCatalogService> logger)
{
    public async Task ScanAsync(Guid printerId, CancellationToken ct = default) =>
        _ = await ScanCoreAsync(printerId, false, ct);

    /// <summary>
    /// Operator-requested scan that ignores the schedule, failure backoff, and automatic-discovery switches,
    /// and advances every observed file's revision so cached previews are rebuilt. The confirmed-idle check,
    /// exclusive per-printer lease, and deployment I/O slots still apply.
    /// </summary>
    public async Task<PrinterFileForceRefreshResult> ForceRefreshAsync(Guid printerId, CancellationToken ct = default)
    {
        if (!PrinterFileIndexPolicy.IsConfirmedIdle(statusCache.GetSnapshot(printerId), DateTime.UtcNow))
        {
            return new(PrinterFileForceRefreshStatus.NotIdle, "waiting_for_confirmed_idle");
        }

        return await ScanCoreAsync(printerId, true, ct);
    }

    private async Task<PrinterFileForceRefreshResult> ScanCoreAsync(Guid printerId, bool force, CancellationToken ct)
    {
        await using IPrinterFileLease? lease = force
            ? await leases.TryAcquireForRefreshAsync(printerId, ct)
            : await leases.TryAcquireAsync(printerId, false, ct);
        if (lease is null)
        {
            return new(PrinterFileForceRefreshStatus.Busy, "source_busy");
        }

        Printer? printer = await printers.FindByIdWithIncludesAsync(printerId, lease.Token);
        if (printer is null || !await lease.IsCurrentAsync(ct))
        {
            return new(PrinterFileForceRefreshStatus.Superseded, "scan_state_changed");
        }

        PrinterFileInventoryResult result;
        if (!capabilities.TryGetFileInventoryClientTyped((PrinterBackend)printer.Backend, out ISupportsFileInventory? client) ||
            client is null)
        {
            result = PrinterFileInventoryResult.Unsupported();
        }
        else
        {
            await using AppDbContext policyDb = await dbFactory.CreateDbContextAsync(ct);
            var settings = await PrinterFileIndexPolicy.ReadAsync(policyDb, ct);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lease.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(settings.ScanTimeoutSeconds));
            if (!PrinterFileIndexPolicy.IsConfirmedIdle(statusCache.GetSnapshot(printerId), DateTime.UtcNow))
            {
                return new(PrinterFileForceRefreshStatus.NotIdle, "waiting_for_confirmed_idle");
            }

            try
            {
                Dictionary<string, PrinterFileInventoryEntry> cachedEntries = [];
                if (!force)
                {
                    var previous = await policyDb.PrinterFileLocations.AsNoTracking()
                        .Where(x => x.SourcePrinterId == printerId &&
                            x.Availability == PrinterFileAvailability.Available &&
                            x.ConfigurationGeneration == lease.ConfigurationGeneration &&
                            x.MutationGeneration == lease.MutationGeneration &&
                            x.DetailsFetchedAtUtc != null)
                        .ToArrayAsync(deadline.Token);
                    cachedEntries = previous.ToDictionary(x => x.IdentityKey, x => new PrinterFileInventoryEntry(
                        x.StorageRoot, x.RelativePath, x.SizeBytes, ProviderVersion: x.ProviderVersion,
                        ProviderHash: x.ProviderDigest, ModifiedValue: x.ModifiedValue, DisplayName: x.DisplayName,
                        Metadata: x.Metadata, DetailsFetchedAtUtc: x.DetailsFetchedAtUtc));
                }

                result = await client.GetFileInventoryAsync(
                    printer.BackendUrl,
                    printer.Credential,
                    new PrinterFileInventoryOptions
                    {
                        Deadline = TimeSpan.FromSeconds(settings.ScanTimeoutSeconds),
                        IncludeFileDetails = true,
                        CachedEntries = cachedEntries,
                        MaxEntries = settings.MaxEntries,
                        MaxDepth = settings.MaxDepth,
                    },
                    deadline.Token);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested && !lease.Token.IsCancellationRequested)
            {
                result = new PrinterFileInventoryResult(
                    PrinterFileInventoryOutcome.Unavailable, PrinterFileInventoryReason.Timeout, [], []);
            }
            catch (OperationCanceledException) when (lease.Token.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                return new(PrinterFileForceRefreshStatus.Superseded, "scan_interrupted");
            }

            if (result.Outcome == PrinterFileInventoryOutcome.Cancelled &&
                deadline.IsCancellationRequested && !lease.Token.IsCancellationRequested)
            {
                result = new PrinterFileInventoryResult(
                    PrinterFileInventoryOutcome.Unavailable, PrinterFileInventoryReason.Timeout, [], []);
            }
        }

        if (result.Outcome == PrinterFileInventoryOutcome.Cancelled || lease.Token.IsCancellationRequested)
        {
            return new(PrinterFileForceRefreshStatus.Superseded, "scan_interrupted");
        }

        if (!await PublishAsync(lease, result, force, ct))
        {
            return new(PrinterFileForceRefreshStatus.Superseded, "scan_state_changed");
        }

        return result.Outcome == PrinterFileInventoryOutcome.Complete
            ? new(PrinterFileForceRefreshStatus.Completed, result.Reason.ToString(), result.Entries.Length)
            : new(PrinterFileForceRefreshStatus.Incomplete, result.Reason.ToString());
    }

    public Task<bool> PublishAsync(
        IPrinterFileLease lease, PrinterFileInventoryResult result, CancellationToken ct = default) =>
        PublishAsync(lease, result, false, ct);

    /// <summary>
    /// Forced publication advances the revision of every observed file so revision-keyed preview caches miss, except files with an
    /// in-flight operation that is pinned to the current revision.
    /// </summary>
    public async Task<bool> PublishAsync(
        IPrinterFileLease lease, PrinterFileInventoryResult result, bool force, CancellationToken ct = default)
    {
        if (result.Outcome == PrinterFileInventoryOutcome.Cancelled || ct.IsCancellationRequested)
        {
            return false;
        }

        await using AppDbContext db = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (!await lease.FencePublicationAsync(db, ct))
        {
            logger.LogInformation("Discarded superseded printer file inventory for {PrinterId}", lease.PrinterId);
            return false;
        }

        DateTime now = DateTime.UtcNow;
        PrinterFileScanState state = await db.PrinterFileScanStates.SingleAsync(
            x => x.SourcePrinterId == lease.PrinterId, ct);
        var settings = await PrinterFileIndexPolicy.ReadAsync(db, ct);
        HashSet<Guid> pinned = force
            ? (await db.PrinterFileOperations
                .Where(x => x.SourcePrinterId == lease.PrinterId &&
                    x.State != PrinterFileOperationState.Queued &&
                    x.State != PrinterFileOperationState.Deleted &&
                    x.State != PrinterFileOperationState.Failed &&
                    x.State != PrinterFileOperationState.Cancelled)
                .Select(x => x.LocationId).ToListAsync(ct)).ToHashSet()
            : [];
        Dictionary<string, PrinterFileLocation> locations = await db.PrinterFileLocations
            .Where(x => x.SourcePrinterId == lease.PrinterId).ToDictionaryAsync(x => x.IdentityKey, ct);
        HashSet<string> observed = new(StringComparer.Ordinal);
        foreach (PrinterFileInventoryEntry entry in result.Entries)
        {
            if (!PrinterInventoryPath.TryCanonicalizeRoot(entry.StorageRoot, out string root) ||
                !PrinterInventoryPath.TryCanonicalizeRelativePath(entry.RelativePath, out string path) ||
                root != entry.StorageRoot || path != entry.RelativePath || entry.SizeBytes < 0 ||
                (entry.Metadata is { } metadata && (!metadata.IsValid() || !entry.DetailsFetchedAtUtc.HasValue)))
            {
                throw new InvalidOperationException("Inventory adapter returned a noncanonical observation.");
            }

            string key = PrinterInventoryPath.ComputeIdentity(root, path);
            if (!observed.Add(key))
            {
                throw new InvalidOperationException("Inventory adapter returned duplicate identity observations.");
            }

            bool isNew = !locations.TryGetValue(key, out PrinterFileLocation? location);
            if (location is null)
            {
                location = new PrinterFileLocation
                {
                    Id = Guid.NewGuid(),
                    SourcePrinterId = lease.PrinterId,
                    RegisteredPrinterId = lease.PrinterId,
                    StorageRoot = root,
                    RelativePath = path,
                    IdentityKey = key,
                    FirstSeenAtUtc = now,
                };
                db.PrinterFileLocations.Add(location);
                locations.Add(key, location);
            }

            bool changed = location.SizeBytes != entry.SizeBytes ||
                location.ModifiedValue != entry.ModifiedValue ||
                location.ProviderVersion != entry.ProviderVersion ||
                location.ProviderDigest != entry.ProviderHash ||
                JsonSerializer.Serialize(location.Metadata) != JsonSerializer.Serialize(entry.Metadata) ||
                location.ConfigurationGeneration != lease.ConfigurationGeneration;
            if (!isNew && ((force && !pinned.Contains(location.Id)) || changed ||
                    location.Availability == PrinterFileAvailability.Deleted))
            {
                location.ObservationRevision++;
            }

            location.DisplayName = entry.DisplayName is { Length: > 0 and <= 512 } displayName &&
                !string.IsNullOrWhiteSpace(displayName) &&
                !displayName.Any(c => char.IsControl(c) || c is '/' or '\\')
                ? displayName : path[(path.LastIndexOf('/') + 1)..];
            location.SizeBytes = entry.SizeBytes;
            location.Metadata = entry.Metadata;
            location.DetailsFetchedAtUtc = entry.DetailsFetchedAtUtc;
            location.ModifiedValue = entry.ModifiedValue;
            location.ProviderVersion = entry.ProviderVersion;
            location.ProviderDigest = entry.ProviderHash;
            location.ConfigurationGeneration = lease.ConfigurationGeneration;
            location.MutationGeneration = lease.MutationGeneration;
            location.LastSeenAtUtc = now;
            location.Availability = PrinterFileAvailability.Available;
        }

        List<PrinterFileRootScan> roots = await db.PrinterFileRootScans
            .Where(x => x.ScanStateId == state.Id).ToListAsync(ct);
        foreach (PrinterFileInventoryCoverage coverage in result.Coverage.Where(x =>
                     x.IsComplete && x.Evidence != PrinterFileInventoryEvidence.None &&
                     result.Outcome is PrinterFileInventoryOutcome.Complete or PrinterFileInventoryOutcome.Partial))
        {
            if (!PrinterInventoryPath.TryCanonicalizeRoot(coverage.StorageRoot, out string root) ||
                root != coverage.StorageRoot ||
                (coverage.Subtree.Length != 0 &&
                 (!PrinterInventoryPath.TryCanonicalizeRelativePath(coverage.Subtree, out string subtree) ||
                  subtree != coverage.Subtree)))
            {
                throw new InvalidOperationException("Inventory adapter returned invalid coverage.");
            }

            string identity = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(root + "\0" + coverage.Subtree)));
            PrinterFileRootScan? rootScan = roots.SingleOrDefault(x => x.IdentityKey == identity);
            if (rootScan is null)
            {
                rootScan = new PrinterFileRootScan
                {
                    Id = Guid.NewGuid(),
                    ScanStateId = state.Id,
                    StorageRoot = root,
                    Subtree = coverage.Subtree,
                    IdentityKey = identity,
                };
                db.PrinterFileRootScans.Add(rootScan);
                roots.Add(rootScan);
            }

            rootScan.ConfigurationGeneration = lease.ConfigurationGeneration;
            rootScan.MutationGeneration = lease.MutationGeneration;
            rootScan.LastCompleteScanAtUtc = now;
            foreach (PrinterFileLocation location in locations.Values.Where(x =>
                         x.StorageRoot == root && InSubtree(x.RelativePath, coverage.Subtree)))
            {
                location.LastCompleteScanAtUtc = now;
                if (!observed.Contains(location.IdentityKey) && location.Availability == PrinterFileAvailability.Available)
                {
                    location.Availability = PrinterFileAvailability.Missing;
                    location.ObservationRevision++;
                }
            }
        }

        state.LastAttemptAtUtc = now;
        state.Outcome = result.Reason == PrinterFileInventoryReason.AuthenticationRequired
            ? PrinterFileScanOutcome.AuthenticationRequired
            : Enum.Parse<PrinterFileScanOutcome>(result.Outcome.ToString());
        state.ReasonCode = result.Reason.ToString();
        state.ConsecutiveFailures = result.Outcome == PrinterFileInventoryOutcome.Complete
            ? 0 : Math.Min(30, state.ConsecutiveFailures + 1);
        if (result.Outcome == PrinterFileInventoryOutcome.Complete)
        {
            state.LastCompleteScanAtUtc = now;
        }

        state.NextScanAtUtc = state.Outcome is PrinterFileScanOutcome.AuthenticationRequired or PrinterFileScanOutcome.Unsupported
            ? new DateTime(9999, 12, 31, 0, 0, 0, DateTimeKind.Utc)
            : now + PrinterFileIndexPolicy.NextDelay(lease.PrinterId, state.ConsecutiveFailures, settings);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    private static bool InSubtree(string path, string subtree) =>
        subtree.Length == 0 || path.StartsWith(subtree + "/", StringComparison.Ordinal);
}
