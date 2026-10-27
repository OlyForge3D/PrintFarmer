using System.Data;
using System.Security.Cryptography;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Gcode.PrinterFiles;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.StorageManagement;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace Farm.Modules.Gcode.Services.Gcode;

public sealed record PrinterFileThumbnailReservationResult(Guid? ReservationId, string? Reason);

/// <summary>
/// Deployment-shared, local-only immutable images. All replicas must share both the database and
/// the configured G-code filesystem. No printer I/O or location publication belongs in this store.
/// </summary>
public sealed class PrinterFileThumbnailStore(
    IDbContextFactory<AppDbContext> dbFactory,
    IStoragePathService storagePaths,
    ILogger<PrinterFileThumbnailStore> logger,
    TimeProvider? timeProvider = null)
{
    public const string DirectoryName = ".printer-file-thumbnails";

    public static readonly TimeSpan ReservationLifetime = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan RetirementGrace = TimeSpan.FromHours(1);

    private readonly string _root = Path.GetFullPath(storagePaths.GetGcodeStorageDirectory());
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    /// <summary>Charges the maximum image size before a caller starts remote capture.</summary>
    public async Task<PrinterFileThumbnailReservationResult> ReserveAsync(CancellationToken ct)
    {
        try
        {
            await using AppDbContext db = await dbFactory.CreateDbContextAsync(ct);
            await using IDbContextTransaction transaction = await LockAsync(db, ct);
            CheckDirectory(create: true);
            await ReconcileAttemptsAsync(db, ct, validateArtifactLengths: true);
            await RecoverReservationsAsync(db, ct);
            long maximum = (await PrinterFileIndexPolicy.ReadAsync(db, ct)).ThumbnailCacheMaxBytes;
            long reserved = await db.PrinterFileThumbnailReservations.SumAsync(x => (long?)x.ReservedBytes, ct) ?? 0;
            long committed = await db.PrinterFileThumbnailArtifacts.SumAsync(x => (long?)x.SizeBytes, ct) ?? 0;
            if (reserved > maximum - committed - PrinterFileEnrichmentLimits.MaximumImageBytes)
            {
                await transaction.CommitAsync(ct);
                return new(null, "cache_full");
            }

            Guid id = Guid.NewGuid();
            DateTime now = UtcNow;
            db.PrinterFileThumbnailReservations.Add(new()
            {
                Id = id,
                ReservedBytes = PrinterFileEnrichmentLimits.MaximumImageBytes,
                CreatedAtUtc = now,
                ExpiresAtUtc = now + ReservationLifetime,
            });
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return new(id, null);
        }
        catch (Exception ex) when (IsStorageFailure(ex))
        {
            logger.LogWarning(ex, "Printer thumbnail storage is unavailable; check the shared G-code mount and permissions.");
            return new(null, "storage_unavailable");
        }
    }

    /// <summary>
    /// Consumes a reservation into an artifact, never a location pointer. A crash at any filesystem
    /// step leaves the original reservation charged and both generated attempt names recoverable.
    /// </summary>
    public async Task<Guid> WriteAsync(Guid reservationId, byte[] image, string? contentType, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Length > PrinterFileEnrichmentLimits.MaximumImageBytes)
        {
            throw new InvalidDataException("Thumbnail exceeds the encoded byte limit.");
        }

        byte[] bytes = (byte[])image.Clone();
        if (!PrinterFileImageValidator.TryValidate(bytes, out string? detectedType, out int width, out int height) ||
            (contentType is not null && !string.Equals(contentType, detectedType, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("Thumbnail is not a supported bounded raster image.");
        }

        try
        {
            await using AppDbContext db = await dbFactory.CreateDbContextAsync(ct);
            await using IDbContextTransaction transaction = await LockAsync(db, ct);
            PrinterFileThumbnailReservation? reservation = await db.PrinterFileThumbnailReservations
                .SingleOrDefaultAsync(x => x.Id == reservationId, ct);
            if (reservation is null || reservation.ExpiresAtUtc <= UtcNow ||
                await db.PrinterFileThumbnailArtifacts.AnyAsync(x => x.Id == reservationId, ct))
            {
                throw new InvalidOperationException("Thumbnail reservation is expired or already consumed.");
            }

            CheckDirectory(create: false);
            string pending = FilePath(reservationId, ".pending");
            string destination = FilePath(reservationId, ".image");

            // Never retry an ambiguous attempt by overwriting either name.
            if (Exists(destination))
            {
                throw new IOException("Thumbnail attempt already has immutable bytes; release it before retrying.");
            }

            await using (FileStream output = new(pending, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await output.WriteAsync(bytes, ct);
                await output.FlushAsync(ct);
#pragma warning disable CA1849 // Flush(true) has no asynchronous equivalent; durability requires the disk flush.
                output.Flush(flushToDisk: true);
#pragma warning restore CA1849
            }

            ct.ThrowIfCancellationRequested();
            File.Move(pending, destination, overwrite: false);
            db.PrinterFileThumbnailArtifacts.Add(new()
            {
                Id = reservationId,
                ContentSha256 = Convert.ToHexString(SHA256.HashData(bytes)),
                ContentType = detectedType!,
                Width = width,
                Height = height,
                SizeBytes = bytes.Length,
                CreatedAtUtc = UtcNow,
            });
            db.PrinterFileThumbnailReservations.Remove(reservation);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return reservationId;
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new IOException("Thumbnail storage is not writable.", ex);
        }
    }

    /// <summary>Idempotent; failure leaves the entire reservation charged for background recovery.</summary>
    public async Task ReleaseAsync(Guid reservationId, CancellationToken ct)
    {
        await using AppDbContext db = await dbFactory.CreateDbContextAsync(ct);
        await using IDbContextTransaction transaction = await LockAsync(db, ct);
        PrinterFileThumbnailReservation? reservation = await db.PrinterFileThumbnailReservations
            .SingleOrDefaultAsync(x => x.Id == reservationId, ct);
        if (reservation is not null)
        {
            // Persist retry eligibility even when physical deletion fails.
            reservation.ExpiresAtUtc = UtcNow;
            try
            {
                CheckDirectory(create: false);
                await DeleteReservationFilesAsync(db, reservation, ct);
            }
            catch (Exception ex) when (IsStorageFailure(ex))
            {
                logger.LogWarning(ex, "Thumbnail attempt cleanup failed; its reserved bytes remain charged.");
            }

            await db.SaveChangesAsync(ct);
        }

        await transaction.CommitAsync(ct);
    }

    /// <summary>Authorization belongs to the caller. Missing bytes are a repairable miss, never remote I/O.</summary>
    public async Task<PrinterFilePreviewContent?> ReadAsync(Guid artifactId, CancellationToken ct)
    {
        await using AppDbContext db = await dbFactory.CreateDbContextAsync(ct);
        PrinterFileThumbnailArtifact? artifact = await db.PrinterFileThumbnailArtifacts.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == artifactId, ct);
        if (artifact is null)
        {
            return null;
        }

        try
        {
            CheckDirectory(create: false);
            string path = FilePath(artifactId, ".image");
            try
            {
                CheckRegularFile(path);
                await using FileStream input = new(path, FileMode.Open, FileAccess.Read,
                    FileShare.Read | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (input.Length != artifact.SizeBytes || input.Length is <= 0 or > PrinterFileEnrichmentLimits.MaximumImageBytes)
                {
                    throw new InvalidDataException("Thumbnail length does not match its durable descriptor.");
                }

                byte[] bytes = new byte[(int)input.Length];
                await input.ReadExactlyAsync(bytes, ct);
                if (Convert.ToHexString(SHA256.HashData(bytes)) != artifact.ContentSha256 ||
                    !PrinterFileImageValidator.TryValidate(bytes, out string? type, out int width, out int height) ||
                    type != artifact.ContentType || width != artifact.Width || height != artifact.Height)
                {
                    throw new InvalidDataException("Thumbnail integrity validation failed.");
                }

                return new(null, null, bytes, artifact.ContentType);
            }
            catch (FileNotFoundException)
            {
                // Distinguish an absent key from disappearance of the mounted directory.
                CheckDirectory(create: false);
                return null;
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new IOException("Thumbnail storage is not readable.", ex);
        }
    }

    /// <summary>Deployment-serialized recovery and retirement; never evicts a valid referenced image.</summary>
    public async Task CleanupAsync(CancellationToken ct)
    {
        // Commit invalidation independently of filesystem availability.
        await DetachObsoleteAsync(ct);
        try
        {
            await RetireArtifactsAsync(ct);
            await using AppDbContext recoveryDb = await dbFactory.CreateDbContextAsync(ct);
            await using IDbContextTransaction recoveryTransaction = await LockAsync(recoveryDb, ct);
            CheckDirectory(create: false);
            await RecoverReservationsAsync(recoveryDb, ct);
            await recoveryTransaction.CommitAsync(ct);
        }
        catch (Exception ex) when (IsStorageFailure(ex))
        {
            logger.LogWarning(ex, "Thumbnail cleanup could not finish; undeleted bytes remain charged. Check the shared mount.");
        }
    }

    private DateTime UtcNow => _clock.GetUtcNow().UtcDateTime;

    private async Task RetireArtifactsAsync(CancellationToken ct)
    {
        await using AppDbContext db = await dbFactory.CreateDbContextAsync(ct);
        await using IDbContextTransaction transaction = await LockAsync(db, ct);
        CheckDirectory(create: false);
        await ReconcileAttemptsAsync(db, ct);
        await RecoverReservationsAsync(db, ct);
        DateTime now = UtcNow;
        List<PrinterFileThumbnailArtifact> artifacts = await db.PrinterFileThumbnailArtifacts
            .Where(x => !db.PrinterFileEnrichments.Any(e => e.ArtifactId == x.Id) || x.RetiredAtUtc != null)
            .OrderBy(x => x.CreatedAtUtc).Take(256).ToListAsync(ct);
        foreach (PrinterFileThumbnailArtifact artifact in artifacts)
        {
            // A true exclusive row lock also blocks FK publication by other callers, including
            // PostgreSQL's KEY SHARE locks (a non-key UPDATE alone does not block those).
            await LockArtifactAsync(db, artifact.Id, ct);
            if (await db.PrinterFileEnrichments.AnyAsync(x => x.ArtifactId == artifact.Id, ct))
            {
                artifact.RetiredAtUtc = null;
                continue;
            }

            if (artifact.RetiredAtUtc is null)
            {
                artifact.RetiredAtUtc = now;
                continue;
            }

            if (artifact.RetiredAtUtc > now - RetirementGrace)
            {
                continue;
            }

            // First durably remove the publishable key, transferring its entire charge to an
            // expired attempt (a deletion tombstone). Only AFTER commit may bytes be deleted.
            // A rollback/serialization failure cannot expose a key whose bytes we already removed.
            PrinterFileThumbnailReservation? tombstone = await db.PrinterFileThumbnailReservations
                .SingleOrDefaultAsync(x => x.Id == artifact.Id, ct);
            if (tombstone is null)
            {
                tombstone = new() { Id = artifact.Id, CreatedAtUtc = now };
                db.PrinterFileThumbnailReservations.Add(tombstone);
            }

            tombstone.ReservedBytes = checked(tombstone.ReservedBytes + artifact.SizeBytes);
            tombstone.ExpiresAtUtc = now;
            db.PrinterFileThumbnailArtifacts.Remove(artifact);
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private async Task DetachObsoleteAsync(CancellationToken ct)
    {
        await using AppDbContext db = await dbFactory.CreateDbContextAsync(ct);
        await using IDbContextTransaction transaction = await LockAsync(db, ct);
        List<PrinterFileEnrichment> associations = await db.PrinterFileEnrichments
            .Include(x => x.Location).ThenInclude(x => x.RegisteredPrinter)
            .Where(x => x.ArtifactId != null).ToListAsync(ct);
        Dictionary<Guid, PrinterFileScanState> scans = await db.PrinterFileScanStates.AsNoTracking()
            .ToDictionaryAsync(x => x.SourcePrinterId, ct);
        foreach (PrinterFileEnrichment enrichment in associations)
        {
            PrinterFileLocation location = enrichment.Location;
            Printer? printer = location.RegisteredPrinter;
            bool current = printer is not null && printer.IsEnabled &&
                location.Availability == PrinterFileAvailability.Available &&
                location.RegisteredPrinterId == location.SourcePrinterId &&
                enrichment.SourceIdentity == PrinterFileEnrichmentIdentity.For(location) &&
                scans.TryGetValue(location.SourcePrinterId, out PrinterFileScanState? scan) &&
                scan.RegisteredPrinterId == location.RegisteredPrinterId &&
                scan.ConfigurationGeneration == location.ConfigurationGeneration &&
                scan.ConfigurationFingerprint == PrinterFileIndexPolicy.ConfigurationFingerprint(printer);
            if (!current)
            {
                enrichment.ArtifactId = null;
                enrichment.ThumbnailState = PrinterFileEnrichmentState.Pending;
                enrichment.ThumbnailReason = "source_changed";
            }
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private static async Task<IDbContextTransaction> LockAsync(AppDbContext db, CancellationToken ct)
    {
        // SQLite's immediate transaction serializes writers; on server providers this UPDATE
        // takes a transaction-duration exclusive lock BEFORE any quota/reference snapshots.
        IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(
            db.Database.IsSqlite() ? IsolationLevel.Serializable : IsolationLevel.ReadCommitted, ct);
        try
        {
            int affected = await db.PrinterFileThumbnailStorageLocks.Where(x => x.Id == 1)
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.Revision, x => x.Revision + 1), ct);
            if (affected != 1)
            {
                throw new InvalidOperationException("Thumbnail storage schema is not initialized; apply migrations.");
            }

            return transaction;
        }
        catch
        {
            await transaction.DisposeAsync();
            throw;
        }
    }

    private static async Task LockArtifactAsync(AppDbContext db, Guid id, CancellationToken ct)
    {
        if (db.Database.IsNpgsql())
        {
            _ = await db.PrinterFileThumbnailArtifacts.FromSqlInterpolated(
                $"""SELECT * FROM "PrinterFileThumbnailArtifacts" WHERE "Id" = {id} FOR UPDATE""")
                .AsNoTracking().ToListAsync(ct);
        }
        else if (db.Database.IsSqlServer())
        {
            _ = await db.PrinterFileThumbnailArtifacts.FromSqlInterpolated(
                $"SELECT * FROM [PrinterFileThumbnailArtifacts] WITH (XLOCK, ROWLOCK) WHERE [Id] = {id}")
                .AsNoTracking().ToListAsync(ct);
        }
    }

    private async Task ReconcileAttemptsAsync(
        AppDbContext db, CancellationToken ct, bool validateArtifactLengths = false)
    {
        Dictionary<Guid, long> artifacts = await db.PrinterFileThumbnailArtifacts
            .ToDictionaryAsync(x => x.Id, x => x.SizeBytes, ct);
        Dictionary<Guid, PrinterFileThumbnailReservation> reservations = await db.PrinterFileThumbnailReservations
            .ToDictionaryAsync(x => x.Id, ct);
        Dictionary<Guid, long> attempts = [];
        foreach (string path in Directory.EnumerateFileSystemEntries(Path.Join(_root, DirectoryName)))
        {
            ct.ThrowIfCancellationRequested();
            CheckRegularFile(path);
            string extension = Path.GetExtension(path);
            if ((extension != ".pending" && extension != ".image") ||
                !Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out Guid id))
            {
                throw new IOException("Unexpected files in the isolated thumbnail directory; reconcile the shared mount.");
            }

            long length = new FileInfo(path).Length;
            if (extension == ".image" && artifacts.TryGetValue(id, out long committedLength))
            {
                if (validateArtifactLengths && length != committedLength)
                {
                    throw new IOException("Thumbnail bytes differ from their quota descriptor; repair the shared cache.");
                }

                continue;
            }

            attempts[id] = checked(attempts.GetValueOrDefault(id) + length);
        }

        foreach ((Guid id, long bytes) in attempts)
        {
            if (!reservations.TryGetValue(id, out PrinterFileThumbnailReservation? reservation))
            {
                reservation = new() { Id = id, CreatedAtUtc = UtcNow, ExpiresAtUtc = UtcNow };
                db.PrinterFileThumbnailReservations.Add(reservation);
            }

            reservation.ReservedBytes = Math.Max(
                reservation.ReservedBytes,
                Math.Max(bytes, PrinterFileEnrichmentLimits.MaximumImageBytes));
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task RecoverReservationsAsync(AppDbContext db, CancellationToken ct)
    {
        DateTime now = UtcNow;
        List<PrinterFileThumbnailReservation> expired = await db.PrinterFileThumbnailReservations
            .Where(x => x.ExpiresAtUtc <= now).ToListAsync(ct);
        bool logged = false;
        foreach (PrinterFileThumbnailReservation reservation in expired)
        {
            try
            {
                await DeleteReservationFilesAsync(db, reservation, ct);
            }
            catch (Exception ex) when (IsStorageFailure(ex))
            {
                if (!logged)
                {
                    logger.LogWarning(ex, "Expired thumbnail attempts could not be deleted; their quota remains reserved.");
                    logged = true;
                }
            }
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task DeleteReservationFilesAsync(AppDbContext db, PrinterFileThumbnailReservation reservation, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        DeleteFile(FilePath(reservation.Id, ".pending"));
        if (!await db.PrinterFileThumbnailArtifacts.AnyAsync(x => x.Id == reservation.Id, ct))
        {
            DeleteFile(FilePath(reservation.Id, ".image"));
        }

        db.PrinterFileThumbnailReservations.Remove(reservation);
    }

    private void CheckDirectory(bool create)
    {
        // Never recreate a missing configured root: that can hide a lost mount behind local disk.
        if (!File.GetAttributes(_root).HasFlag(FileAttributes.Directory))
        {
            throw new IOException("The configured G-code storage root is not a directory.");
        }

        string directory = Path.Join(_root, DirectoryName);
        if (create)
        {
            Directory.CreateDirectory(directory);
        }

        FileAttributes attributes = File.GetAttributes(directory);
        if (!attributes.HasFlag(FileAttributes.Directory) || attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new IOException("The thumbnail directory must be an isolated, non-linked directory.");
        }
    }

    private string FilePath(Guid id, string extension) => Path.Join(_root, DirectoryName, id.ToString("N") + extension);

    private static void CheckRegularFile(string path)
    {
        if ((File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
        {
            throw new IOException("Thumbnail keys must identify regular, non-linked files.");
        }
    }

    private static bool Exists(string path)
    {
        try
        {
            CheckRegularFile(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
    }

    private void DeleteFile(string path)
    {
        // File.Exists hides permission/mount failures. Only genuine absence may release quota.
        if (Exists(path))
        {
            File.Delete(path);
        }

        CheckDirectory(create: false);
    }

    private static bool IsStorageFailure(Exception ex) => ex is IOException or UnauthorizedAccessException;
}
