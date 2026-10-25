using System.Data;
using System.Diagnostics.CodeAnalysis;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Printers;
using Farm.Infrastructure.Services.FileManagement;
using Farm.Infrastructure.Services.FolderManagement;
using Farm.Infrastructure.Services.Gcode.PrinterFiles;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.StorageManagement;
using Farm.Infrastructure.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Farm.Modules.Gcode.Services.Gcode;

/// <summary>
/// One restartable, fenced unit of work. Network transfer and local parsing run outside
/// database transactions. A per-attempt physical filename prevents a superseded worker
/// from overwriting/deleting the successor's bytes.
/// </summary>
public sealed class PrinterFileCopyProcessor(
    AppDbContext db,
    IPrinterFileAccessService access,
    IPrinterFileLeaseCoordinator leases,
    IBackendClientFactory backends,
    IPrinterFileLibraryIngest ingest,
    IStoragePathService storage,
    IStoredFileOperationsService files,
    IPrintersRepository printers,
    PrinterFileCopyService commands,
    IServiceScopeFactory scopes,
    IOptions<BackendTimeoutSettings> timeouts)
{
    private static readonly TimeSpan WorkerDuration = TimeSpan.FromSeconds(45);

    /// <summary>Claims and processes one durable command without request-thread transfer work.</summary>
    /// <returns>False when no durable work is due.</returns>
    [SuppressMessage("Reliability", "CA2025", Justification = "The heartbeat task is joined in finally before either cancellation source is disposed.")]
    public async Task<bool> ProcessNextAsync(CancellationToken ct)
    {
        DateTime now = DateTime.UtcNow;
        PrinterFileOperation? operation = await db.PrinterFileOperations
            .Where(x => (x.Action == PrinterFileAction.Copy || x.Action == PrinterFileAction.Queue) &&
                (x.State == PrinterFileOperationState.Pending ||
                 x.State == PrinterFileOperationState.Copying || x.State == PrinterFileOperationState.Validating) &&
                (x.WorkerId == null || x.WorkerLeaseExpiresAtUtc <= now))
            .OrderBy(x => x.CreatedAtUtc).FirstOrDefaultAsync(ct);
        if (operation is null)
        {
            return false;
        }

        Guid workerId = Guid.NewGuid();
        operation.WorkerId = workerId;
        operation.WorkerFence++;
        operation.WorkerLeaseExpiresAtUtc = now + WorkerDuration;
        operation.State = PrinterFileOperationState.Copying;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return true;
        }

        long workerFence = operation.WorkerFence;
        string? stagedPath = null;
        string? stagedThumbnailPath = null;
        string? candidatePath = null;
        string? candidateThumbnailPath = null;
        bool published = false;
        try
        {
            if (operation.CancellationRequested)
            {
                throw new PrinterFileCommandException("cancelled");
            }

            if (!await access.CanActAsync(operation.ActorId, operation.SourcePrinterId, operation.Action,
                    operation.DestinationPrinterId, ct))
            {
                throw new PrinterFileCommandException("source_authority_changed");
            }

            await using IPrinterFileLease? lease = await leases.TryAcquireAsync(
                operation.SourcePrinterId, interactive: true, ct);
            if (lease is null)
            {
                await FinishAttemptAsync(operation.Id, workerId, workerFence,
                    PrinterFileOperationState.Pending, "source_busy", releaseQuota: false, ct);
                return true;
            }

            // A yesterday reservation cannot spend yesterday's budget on today's owned bytes.
            await commands.ReserveForAttemptAsync(operation.Id, workerId, workerFence, ct);
            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.Token);
            deadline.CancelAfter(timeouts.Value.FileDownloadTimeout);
            using CancellationTokenSource monitorStop = new();
            Task monitor = MonitorAsync(operation.Id, workerId, workerFence, deadline, monitorStop.Token);
            try
            {
                CancellationToken token = deadline.Token;
                PrinterFileCopyService.CopySnapshot snapshot = PrinterFileCopyService.ReadSnapshot(operation);
                await ValidateAuthorityAsync(operation, lease, token);
                await commands.ValidateExtensionAsync(snapshot.RelativePath, token);
                if (operation.ReservedBytes <= 0)
                {
                    throw new PrinterFileCommandException("reservation_missing");
                }

                // Repository populates/decrypts the transient credential container. Direct DbSet
                // materialization would silently issue credentialless requests.
                Printer printer = await printers.FindByIdWithIncludesAsync(operation.SourcePrinterId, token)
                    ?? throw new PrinterFileCommandException("source_removed");
                if (!backends.IsBackendSupported((PrinterBackend)printer.Backend))
                {
                    throw new PrinterFileCommandException("copy_unsupported");
                }

                object backend = backends.GetClient(printer.Backend);
                if (backend is not ISupportsPrinterFileCopy copy || backend is not ISupportsFileInventory inventory)
                {
                    throw new PrinterFileCommandException("copy_unsupported");
                }

                await ValidateObservationAsync(inventory, printer, snapshot, token);
                await ValidateAuthorityAsync(operation, lease, token);
                string root = storage.GetGcodeStorageDirectory();
                string stageDirectory = GetControlledDirectory(root, "/.printer-file-staging");
                await CleanupPreviousAttemptsAsync(operation.Id, stageDirectory, root, token);
                stagedPath = Path.Join(stageDirectory, $"{operation.Id:N}-{workerFence}-{workerId:N}.part");
                PrinterFileCopyBytes received;
                await using (FileStream destination = new(stagedPath, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough))
                {
                    received = await copy.CopyFileToAsync(
                        printer.BackendUrl,
                        snapshot.StorageRoot, snapshot.RelativePath, printer.Credential, destination,
                        Math.Min(operation.ReservedBytes, PrinterFileCopyService.MaximumBytes), token);

                    // WriteThrough writes and async flush precede the durable database binding.
                    await destination.FlushAsync(token);
                }

                if (snapshot.SizeBytes is long expected && received.SizeBytes != expected)
                {
                    throw new PrinterFileCommandException("source_size_changed");
                }

                await ValidateObservationAsync(inventory, printer, snapshot, token);
                await ValidateAuthorityAsync(operation, lease, token);
                string fileName = snapshot.RelativePath.Split('/')[^1];
                stagedThumbnailPath = stagedPath + ".png";
                GcodeFile candidate = await ingest.PrepareAsync(operation.Id, stagedPath, fileName,
                    snapshot.Command.VirtualDirectory!, received.Sha256, received.SizeBytes, token);
                await commands.ValidateExtensionAsync(fileName, token);
                string targetDirectory = GetControlledDirectory(root, snapshot.Command.VirtualDirectory!);
                candidate.FileName = $"{operation.Id:N}-{workerFence}-{workerId:N}{Path.GetExtension(fileName)}";
                candidatePath = Path.Join(targetDirectory, candidate.FileName);
                if (candidate.ThumbnailFileName is not null && File.Exists(stagedThumbnailPath))
                {
                    candidate.ThumbnailFileName = Path.GetFileNameWithoutExtension(candidate.FileName) + "_thumb.png";
                    candidateThumbnailPath = Path.Join(targetDirectory, candidate.ThumbnailFileName);
                    File.Move(stagedThumbnailPath, candidateThumbnailPath, overwrite: false);
                    stagedThumbnailPath = null;
                }

                File.Move(stagedPath, candidatePath, overwrite: false);
                stagedPath = null;

                // Stop this worker's heartbeat before taking the transaction's operation barrier.
                // Other commands still compete through the operation Revision concurrency token.
                await monitorStop.CancelAsync();
                await monitor;
                token.ThrowIfCancellationRequested();
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    try
                    {
                        Guid? validatedWinner = await ValidateDedupWinnerAsync(operation, received, token);
                        published = await PublishAsync(operation.Id, workerId, workerFence, lease,
                            snapshot, candidate, received, validatedWinner, token);
                        break;
                    }
                    catch (DbUpdateException) when (attempt < 2)
                    {
                        // Adopt a committed hash winner without another network capture.
                        db.ChangeTracker.Clear();
                        await Task.Delay(TimeSpan.FromMilliseconds(100 * (attempt + 1)), token);
                    }
                }
            }
            finally
            {
                await monitorStop.CancelAsync();
                await monitor;
            }
        }
        catch (OperationCanceledException)
        {
            // Host shutdown leaves the reservation and durable intent for a successor. Other
            // cancellations are visible and refundable; no silently repeated capture binding.
            if (!ct.IsCancellationRequested)
            {
                await FinishAttemptAsync(operation.Id, workerId, workerFence,
                    PrinterFileOperationState.Failed, "transfer_interrupted", true, CancellationToken.None);
            }
        }
        catch (PrinterFileCommandException failure)
        {
            await FinishAttemptAsync(operation.Id, workerId, workerFence,
                failure.Reason == "cancelled" ? PrinterFileOperationState.Cancelled : PrinterFileOperationState.Blocked,
                failure.Reason, true, CancellationToken.None);
        }
        catch (PrinterFileCopyException failure)
        {
            await FinishAttemptAsync(operation.Id, workerId, workerFence,
                PrinterFileOperationState.Failed, failure.Reason, true, CancellationToken.None);
        }
        catch (DbUpdateException)
        {
            // Includes dedup/serialization races. Nothing was bound; a durable retry revalidates
            // the original observation rather than silently substituting a new source.
            await FinishAttemptAsync(operation.Id, workerId, workerFence,
                PrinterFileOperationState.Pending, "publication_retry", false, CancellationToken.None);
        }
        catch (Exception)
        {
            // Do not persist/log upstream exception messages: they can contain credentials/URLs.
            await FinishAttemptAsync(operation.Id, workerId, workerFence,
                PrinterFileOperationState.Failed, "copy_failed", true, CancellationToken.None);
        }
        finally
        {
            DeleteAttemptFile(stagedPath);
            DeleteAttemptFile(stagedThumbnailPath);
            if (!published && candidatePath is not null &&
                await CanDeleteCandidateAsync(operation.Id, Path.GetFileName(candidatePath)))
            {
                DeleteAttemptFile(candidatePath);
                DeleteAttemptFile(candidateThumbnailPath);
            }
        }

        return true;
    }

    /// <summary>Reconciles application-owned leftovers after a crash, including post-commit dedup leftovers.</summary>
    public async Task CleanupCompletedAttemptsAsync(CancellationToken ct)
    {
        const int pageSize = 100;
        int offset = 0;
        while (true)
        {
            List<PrinterFileOperation> page = await db.PrinterFileOperations.AsNoTracking()
                .Where(x => (x.Action == PrinterFileAction.Copy || x.Action == PrinterFileAction.Queue) &&
                    x.State != PrinterFileOperationState.Pending &&
                    x.State != PrinterFileOperationState.Copying && x.State != PrinterFileOperationState.Validating)
                .OrderBy(x => x.CreatedAtUtc).ThenBy(x => x.Id)
                .Skip(offset).Take(pageSize).ToListAsync(ct);
            foreach (PrinterFileOperation operation in page)
            {
                string root = storage.GetGcodeStorageDirectory();
                string stage = GetControlledDirectory(root, "/.printer-file-staging");
                List<string> leftovers = await GetAttemptLeftoversAsync(operation, stage, root, ct);
                if (leftovers.Count == 0)
                {
                    continue;
                }

                // Local-only housekeeping takes an operation CAS barrier, not a live-printer
                // dependency. This works after printer removal and cannot race an explicit retry.
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                int changed = await db.PrinterFileOperations
                    .Where(x => x.Id == operation.Id && x.Revision == operation.Revision &&
                        x.State == operation.State)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.Revision, x => x.Revision + 1), ct);
                if (changed != 1)
                {
                    continue;
                }

                foreach (string path in leftovers)
                {
                    DeleteAttemptFile(path);
                }

                await transaction.CommitAsync(ct);
            }

            if (page.Count < pageSize)
            {
                return;
            }

            offset += pageSize;
        }
    }

    private async Task<bool> CanDeleteCandidateAsync(Guid id, string fileName)
    {
        try
        {
            // A failed CommitAsync response is ambiguous. Never delete bytes merely because
            // the client did not observe the commit acknowledgement.
            await using AsyncServiceScope scope = scopes.CreateAsyncScope();
            AppDbContext currentDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return !await currentDb.GcodeFiles.AsNoTracking()
                .AnyAsync(x => x.Id == id && x.FileName == fileName);
        }
        catch (Exception)
        {
            return false; // Leave an orphan for reconciliation instead of destroying possibly owned bytes.
        }
    }

    private async Task<bool> PublishAsync(
        Guid operationId, Guid workerId, long workerFence, IPrinterFileLease lease,
        PrinterFileCopyService.CopySnapshot snapshot, GcodeFile candidate,
        PrinterFileCopyBytes received, Guid? validatedWinner, CancellationToken ct)
    {
        await using AsyncServiceScope publication = scopes.CreateAsyncScope();
        AppDbContext publicationDb = publication.ServiceProvider.GetRequiredService<AppDbContext>();
        IPrinterFileAccessService publicationAccess =
            publication.ServiceProvider.GetRequiredService<IPrinterFileAccessService>();
        IFolderManagementService publicationFolders =
            publication.ServiceProvider.GetRequiredService<IFolderManagementService>();
        PrinterFileCopyService publicationCommands =
            publication.ServiceProvider.GetRequiredService<PrinterFileCopyService>();
        await using var transaction = await publicationDb.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);

        // The coordinator performs raw CAS revision updates. Fence before loading tracked
        // publication rows, and never call its separate-context IsCurrentAsync under these locks.
        if (!await lease.FencePublicationAsync(publicationDb, ct))
        {
            throw new PrinterFileCommandException("source_ownership_lost");
        }

        PrinterFileOperation operation = await publicationDb.PrinterFileOperations.SingleAsync(x => x.Id == operationId, ct);
        if (operation.WorkerId != workerId || operation.WorkerFence != workerFence ||
            operation.WorkerLeaseExpiresAtUtc <= DateTime.UtcNow || operation.CancellationRequested ||
            operation.ContentSha256 is not null)
        {
            throw new PrinterFileCommandException(operation.CancellationRequested ? "cancelled" : "worker_superseded");
        }

        await ValidateAuthorityAsync(operation, lease, ct, publicationFenced: true, publicationDb, publicationAccess);
        GcodeFile? existing = await publicationDb.GcodeFiles.SingleOrDefaultAsync(x => x.FileHash == received.Sha256, ct);
        if (existing?.Id != validatedWinner)
        {
            throw new DbUpdateConcurrencyException("dedup_winner_changed");
        }

        if (existing is not null && !await publicationAccess.CanAccessLocalAsync(
                operation.ActorId, existing.Id, LocalAccessRequired(operation.Action), ct))
        {
            throw new PrinterFileCommandException("local_asset_access_denied", 403);
        }

        if (existing is not null && !File.Exists(files.GetFullFilePath(existing)))
        {
            throw new PrinterFileCommandException("dedup_asset_missing");
        }

        await publicationCommands.ValidateExtensionAsync(snapshot.RelativePath, ct);
        DateTime capturedAtUtc = await publicationCommands.CheckQuotaAsync(
            operation, existing is null ? received.SizeBytes : 0, ct);
        if (existing is null)
        {
            FolderNode folder = await publicationFolders.GetOrCreateFolderAsync(snapshot.Command.VirtualDirectory!, "gcode", ct);
            candidate.FolderId = folder.Id;

            // Deliberately no SourcePrinter FK: source deletion cannot delete/restrict owned bytes.
            candidate.SourcePrinterId = null;
            publicationDb.GcodeFiles.Add(candidate);

            // The access service checks live persisted assets. Insert within this transaction
            // before authorization, without publishing an operation/origin binding yet.
            await publicationDb.SaveChangesAsync(ct);
        }

        GcodeFile asset = existing ?? candidate;
        if (!await publicationAccess.CanAccessLocalAsync(
                operation.ActorId, asset.Id, LocalAccessRequired(operation.Action), ct))
        {
            throw new PrinterFileCommandException("local_asset_access_denied", 403);
        }

        // Native adapters currently cannot prove a version-bound read. Even matching pre/post
        // metadata is not snapshot proof. The actual digest proves only our captured local bytes.
        operation.Verification = PrinterFileVerification.UnverifiedSnapshot;
        operation.GcodeFileId = asset.Id;
        operation.ContentSha256 = received.Sha256;
        operation.SizeBytes = received.SizeBytes;
        operation.ReservedBytes = existing is null ? received.SizeBytes : 0;
        operation.State = PrinterFileOperationState.LocalReady;
        operation.ReasonCode = "unverified_source_snapshot";
        operation.WorkerId = null;
        operation.WorkerLeaseExpiresAtUtc = null;
        operation.UpdatedAtUtc = capturedAtUtc;
        publicationDb.PrinterFileOrigins.Add(new PrinterFileOrigin
        {
            Id = Guid.NewGuid(),
            OperationId = operation.Id,
            SourcePrinterId = operation.SourcePrinterId,
            ActorId = operation.ActorId,
            LocationId = operation.LocationId,
            GcodeFileId = asset.Id,
            StorageRoot = snapshot.StorageRoot,
            RelativePath = snapshot.RelativePath,
            ObservationRevision = operation.ObservationRevision,
            ConfigurationGeneration = operation.ConfigurationGeneration,
            ProviderVersion = snapshot.ProviderVersion,
            ProviderDigest = snapshot.ProviderDigest,
            ContentSha256 = received.Sha256,
            SizeBytes = received.SizeBytes,
            Verification = operation.Verification,
            CapturedAtUtc = capturedAtUtc,
        });
        await publicationDb.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return existing is null;
    }

    private async Task<Guid?> ValidateDedupWinnerAsync(
        PrinterFileOperation operation, PrinterFileCopyBytes received, CancellationToken ct)
    {
        GcodeFile? existing = await db.GcodeFiles.AsNoTracking()
            .SingleOrDefaultAsync(x => x.FileHash == received.Sha256, ct);
        if (existing is null)
        {
            return null;
        }

        if (!await access.CanAccessLocalAsync(
                operation.ActorId, existing.Id, LocalAccessRequired(operation.Action), ct))
        {
            throw new PrinterFileCommandException("local_asset_access_denied", 403);
        }

        string path = files.GetFullFilePath(existing);
        if (!File.Exists(path))
        {
            throw new PrinterFileCommandException("dedup_asset_missing");
        }

        await using FileStream content = new(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        PrinterFileCopyBytes actual = await PrinterFileCopyTransport.CopyBoundedAsync(
            content, Stream.Null, PrinterFileCopyService.MaximumBytes, received.SizeBytes, ct);
        if (actual.Sha256 != received.Sha256)
        {
            throw new PrinterFileCommandException("dedup_asset_integrity_failed");
        }

        return existing.Id;
    }

    private static PrinterGroupAccessLevel LocalAccessRequired(PrinterFileAction action) =>
        action == PrinterFileAction.Queue ? PrinterGroupAccessLevel.Submit : PrinterGroupAccessLevel.View;

    private async Task ValidateAuthorityAsync(
        PrinterFileOperation operation, IPrinterFileLease lease, CancellationToken ct, bool publicationFenced = false,
        AppDbContext? authorityDb = null, IPrinterFileAccessService? currentAccess = null)
    {
        if (operation.ConfigurationGeneration != lease.ConfigurationGeneration ||
            operation.MutationGeneration != lease.MutationGeneration ||
            lease.Token.IsCancellationRequested ||
            (!publicationFenced && !await lease.IsCurrentAsync(ct)) ||
            !await (currentAccess ?? access).CanActAsync(operation.ActorId, operation.SourcePrinterId, operation.Action,
                operation.DestinationPrinterId, ct))
        {
            throw new PrinterFileCommandException("source_authority_changed");
        }

        PrinterFileLocation? location = await (authorityDb ?? db).PrinterFileLocations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == operation.LocationId, ct);
        if (location is null || location.RegisteredPrinterId != operation.SourcePrinterId ||
            location.ObservationRevision != operation.ObservationRevision ||
            location.ConfigurationGeneration != operation.ConfigurationGeneration ||
            location.MutationGeneration != operation.MutationGeneration ||
            location.Availability != PrinterFileAvailability.Available)
        {
            throw new PrinterFileCommandException("source_observation_changed");
        }
    }

    private static async Task ValidateObservationAsync(
        ISupportsFileInventory inventory, Printer printer,
        PrinterFileCopyService.CopySnapshot snapshot, CancellationToken ct)
    {
        PrinterFileInventoryResult result = await inventory.GetFileInventoryAsync(
            printer.BackendUrl, printer.Credential, ct: ct);
        if (result.Outcome != PrinterFileInventoryOutcome.Complete ||
            !result.Coverage.Any(x => x.StorageRoot == snapshot.StorageRoot && x.Subtree.Length == 0 &&
                x.IsComplete && x.Evidence != PrinterFileInventoryEvidence.None))
        {
            throw new PrinterFileCommandException("source_restat_unavailable");
        }

        PrinterFileInventoryEntry? observed = result.Entries.SingleOrDefault(x =>
            x.StorageRoot == snapshot.StorageRoot && x.RelativePath == snapshot.RelativePath);
        if (observed is null)
        {
            throw new PrinterFileCommandException("source_missing");
        }

        if (observed.SizeBytes != snapshot.SizeBytes || observed.ModifiedValue != snapshot.ModifiedValue ||
            observed.ProviderVersion != snapshot.ProviderVersion || observed.ProviderHash != snapshot.ProviderDigest)
        {
            throw new PrinterFileCommandException("source_changed");
        }
    }

    private async Task MonitorAsync(
        Guid operationId, Guid workerId, long fence, CancellationTokenSource work, CancellationToken stop)
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(3), stop);
                await using AsyncServiceScope scope = scopes.CreateAsyncScope();
                AppDbContext monitorDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                PrinterFileOperation? operation = await monitorDb.PrinterFileOperations
                    .SingleOrDefaultAsync(x => x.Id == operationId, stop);
                IPrinterFileAccessService currentAccess =
                    scope.ServiceProvider.GetRequiredService<IPrinterFileAccessService>();
                if (operation is null || operation.WorkerId != workerId || operation.WorkerFence != fence ||
                    operation.CancellationRequested ||
                    !await currentAccess.CanActAsync(operation.ActorId, operation.SourcePrinterId,
                        operation.Action, operation.DestinationPrinterId, stop))
                {
                    await work.CancelAsync();
                    return;
                }

                operation.WorkerLeaseExpiresAtUtc = DateTime.UtcNow + WorkerDuration;
                await monitorDb.SaveChangesAsync(stop);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // Publication or shutdown explicitly stopped the heartbeat.
        }
        catch (Exception)
        {
            // An unverifiable worker lease is lost, not assumed current.
            await work.CancelAsync();
        }
    }

    private async Task FinishAttemptAsync(
        Guid id, Guid workerId, long fence, PrinterFileOperationState state,
        string reason, bool releaseQuota, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        PrinterFileOperation? operation = await db.PrinterFileOperations.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (operation is null || operation.WorkerId != workerId || operation.WorkerFence != fence ||
            operation.ContentSha256 is not null)
        {
            return;
        }

        bool cancelled = operation.CancellationRequested;
        operation.State = cancelled ? PrinterFileOperationState.Cancelled : state;
        operation.ReasonCode = cancelled ? "cancelled" : reason;
        if (releaseQuota || cancelled)
        {
            operation.ReservedBytes = 0;
        }

        operation.WorkerId = operation.State == PrinterFileOperationState.Pending ? workerId : null;
        operation.WorkerLeaseExpiresAtUtc = operation.State == PrinterFileOperationState.Pending
            ? DateTime.UtcNow.AddSeconds(2) : null;
        if (releaseQuota || cancelled)
        {
            operation.UpdatedAtUtc = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task CleanupPreviousAttemptsAsync(
        Guid operationId, string stageDirectory, string libraryRoot, CancellationToken ct)
    {
        PrinterFileOperation operation = await db.PrinterFileOperations.AsNoTracking()
            .SingleAsync(x => x.Id == operationId, ct);
        foreach (string path in await GetAttemptLeftoversAsync(operation, stageDirectory, libraryRoot, ct))
        {
            DeleteAttemptFile(path);
        }
    }

    private async Task<List<string>> GetAttemptLeftoversAsync(
        PrinterFileOperation operation, string stageDirectory, string libraryRoot, CancellationToken ct)
    {
        // Only application-owned GUID prefixes; never wildcard-delete unrelated library uploads.
        List<string> leftovers = [.. Directory.EnumerateFiles(stageDirectory, $"{operation.Id:N}-*.part*")];
        var owned = await db.GcodeFiles.Where(x => x.Id == operation.Id)
            .Select(x => new { x.FileName, x.ThumbnailFileName }).SingleOrDefaultAsync(ct);
        var snapshot = PrinterFileCopyService.ReadSnapshot(operation);
        string target = GetControlledDirectory(libraryRoot, snapshot.Command.VirtualDirectory!);
        foreach (string path in Directory.EnumerateFiles(target, $"{operation.Id:N}-*"))
        {
            if (Path.GetFileName(path) != owned?.FileName && Path.GetFileName(path) != owned?.ThumbnailFileName)
            {
                leftovers.Add(path);
            }
        }

        return leftovers;
    }

    private static void DeleteAttemptFile(string? path)
    {
        if (path is null)
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // A successor holding the exclusive source lease retries cleanup.
        }
        catch (UnauthorizedAccessException)
        {
            // Never delete outside our application-owned prefix.
        }
    }

    private static string GetControlledDirectory(string root, string virtualDirectory)
    {
        string directory = Path.GetFullPath(root);
        Directory.CreateDirectory(directory);
        foreach (string segment in virtualDirectory.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            directory = Path.Join(directory, segment);
            Directory.CreateDirectory(directory);
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            {
                throw new PrinterFileCommandException("unsafe_library_directory");
            }
        }

        return directory;
    }
}
