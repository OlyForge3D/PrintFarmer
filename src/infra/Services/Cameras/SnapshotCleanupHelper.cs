using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.StorageManagement;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.Cameras;

/// <summary>
/// Shared helper that deletes both on-disk snapshot files and their DB rows for a given camera.
/// Used by <see cref="CameraService"/> and <see cref="Farm.Infrastructure.Services.Printers.PrintersService"/>
/// to ensure the two deletion paths stay consistent.
/// </summary>
public static class SnapshotCleanupHelper
{
    /// <summary>
    /// Deletes all snapshot files stored on disk and removes the corresponding
    /// <see cref="CameraSnapshot"/> rows from the database for the given camera.
    /// </summary>
    /// <remarks>
    /// Each stored <see cref="CameraSnapshot.FilePath"/> is a relative path from the snapshot root
    /// directory returned by <see cref="IStoragePathService.GetSnapshotStorageDirectory"/>.
    /// Before touching the filesystem the method validates that the resolved absolute path is
    /// contained within that root (path-traversal defence).  A missing file is silently skipped;
    /// any other <see cref="IOException"/> is logged but does not abort the remainder of the
    /// cleanup so that the database rows are always removed.
    /// </remarks>
    /// <param name="cameraId">Camera whose snapshots should be purged.</param>
    /// <param name="db">EF Core context (must have <c>CameraSnapshots</c> tracked).</param>
    /// <param name="storagePathService">Resolves the snapshot storage root on this host.</param>
    /// <param name="logger">Logger for diagnostics.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task DeleteSnapshotsForCameraAsync(
        Guid cameraId,
        AppDbContext db,
        IStoragePathService storagePathService,
        ILogger logger,
        CancellationToken ct)
    {
        List<CameraSnapshot> snapshots = await db.CameraSnapshots
            .Where(s => s.CameraId == cameraId)
            .ToListAsync(ct);

        if (snapshots.Count == 0)
        {
            return;
        }

        string snapshotRoot = storagePathService.GetSnapshotStorageDirectory();

        // Normalise once — avoids repeated allocations in the loop.
        string canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(snapshotRoot));

        int filesDeleted = 0;
        foreach (CameraSnapshot snapshot in snapshots)
        {
            string fullPath = Path.Combine(snapshotRoot, snapshot.FilePath);
            string canonicalFull = Path.GetFullPath(fullPath);

            bool isContained =
                canonicalFull.StartsWith(canonicalRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || canonicalFull.Equals(canonicalRoot, StringComparison.OrdinalIgnoreCase);

            if (!isContained)
            {
                logger.LogWarning(
                    "[SnapshotCleanup] Path traversal blocked — skipping file for snapshot {SnapshotId} (camera {CameraId})",
                    snapshot.Id, cameraId);
                continue;
            }

            try
            {
                if (File.Exists(fullPath))
                {
                    File.Delete(fullPath);
                    filesDeleted++;
                }
            }
            catch (FileNotFoundException)
            {
                // Already gone — nothing to do.
            }
            catch (UnauthorizedAccessException ex)
            {
                logger.LogError(
                    ex,
                    "[SnapshotCleanup] Access denied deleting snapshot file {Path} for camera {CameraId} — DB row will still be removed",
                    fullPath, cameraId);
            }
            catch (IOException ex)
            {
                logger.LogError(
                    ex,
                    "[SnapshotCleanup] IO error deleting snapshot file {Path} for camera {CameraId} — DB row will still be removed",
                    fullPath, cameraId);
            }
        }

        db.CameraSnapshots.RemoveRange(snapshots);

        logger.LogInformation(
            "[SnapshotCleanup] Removed {DbCount} snapshot record(s) and {FileCount} file(s) for camera {CameraId}",
            snapshots.Count, filesDeleted, cameraId);
    }
}
