using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Cameras;
using Farm.Infrastructure.Services.StorageManagement;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services.Cameras;

/// <summary>
/// Unit tests for <see cref="SnapshotCleanupHelper"/>.
/// </summary>
public class SnapshotCleanupHelperTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly Mock<IStoragePathService> _storagePathService;
    private readonly Mock<ILogger> _logger;
    private readonly string _snapshotRoot;

    public SnapshotCleanupHelperTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new AppDbContext(options);

        _snapshotRoot = Path.Combine(Path.GetTempPath(), "pfarm-cleanup-tests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_snapshotRoot);

        _storagePathService = new Mock<IStoragePathService>(MockBehavior.Strict);
        _storagePathService.Setup(s => s.GetSnapshotStorageDirectory()).Returns(_snapshotRoot);

        _logger = new Mock<ILogger>(MockBehavior.Loose);
    }

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_snapshotRoot))
        {
            Directory.Delete(_snapshotRoot, recursive: true);
        }
    }

    private static Guid SeedCamera(AppDbContext db)
    {
        var cameraId = Guid.NewGuid();
        db.Cameras.Add(new Camera
        {
            Id = cameraId,
            Name = "Test Camera",
            Source = CameraSource.Standalone,
            CameraType = CameraType.General,
        });
        db.SaveChanges();
        return cameraId;
    }

    private CameraSnapshot SeedSnapshot(Guid cameraId, string relativePath)
    {
        var snapshot = new CameraSnapshot
        {
            Id = Guid.NewGuid(),
            CameraId = cameraId,
            PrinterId = Guid.NewGuid(),
            EventType = "PrintStarted",
            FilePath = relativePath,
            CapturedAt = DateTime.UtcNow,
        };
        _db.CameraSnapshots.Add(snapshot);
        _db.SaveChanges();
        return snapshot;
    }

    private string CreatePhysicalFile(string relativePath)
    {
        string fullPath = Path.Combine(_snapshotRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllBytes(fullPath, [0xFF, 0xD8, 0xFF]); // JPEG magic bytes
        return fullPath;
    }

    // ── Happy path ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteSnapshotsForCameraAsync_HappyPath_DeletesFileAndRow()
    {
        var cameraId = SeedCamera(_db);
        const string relativePath = "printer1/job1/snap.jpg";
        SeedSnapshot(cameraId, relativePath);
        string fullPath = CreatePhysicalFile(relativePath);

        await SnapshotCleanupHelper.DeleteSnapshotsForCameraAsync(
            cameraId, _db, _storagePathService.Object, _logger.Object, CancellationToken.None);

        await _db.SaveChangesAsync(); // persist the Remove

        Assert.Empty(_db.CameraSnapshots.Where(s => s.CameraId == cameraId));
        Assert.False(File.Exists(fullPath), "File should have been deleted from disk.");
    }

    [Fact]
    public async Task DeleteSnapshotsForCameraAsync_NoSnapshots_CompletesWithoutError()
    {
        var cameraId = SeedCamera(_db);

        // No exception, no file system interaction.
        await SnapshotCleanupHelper.DeleteSnapshotsForCameraAsync(
            cameraId, _db, _storagePathService.Object, _logger.Object, CancellationToken.None);

        Assert.Empty(_db.CameraSnapshots.Where(s => s.CameraId == cameraId));
    }

    // ── Missing file ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteSnapshotsForCameraAsync_FileAlreadyMissing_StillDeletesDbRow()
    {
        var cameraId = SeedCamera(_db);
        // Seed DB row but do NOT create the file on disk.
        SeedSnapshot(cameraId, "printer1/job2/missing.jpg");

        await SnapshotCleanupHelper.DeleteSnapshotsForCameraAsync(
            cameraId, _db, _storagePathService.Object, _logger.Object, CancellationToken.None);

        await _db.SaveChangesAsync();

        Assert.Empty(_db.CameraSnapshots.Where(s => s.CameraId == cameraId));
    }

    // ── Path traversal ────────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteSnapshotsForCameraAsync_PathTraversalRow_DoesNotDeleteOutsideRoot()
    {
        var cameraId = SeedCamera(_db);

        // Tampered FilePath that would escape the snapshot root.
        SeedSnapshot(cameraId, "../../etc/passwd");

        // Create a sentinel file at the resolved traversal target to detect any accidental deletion.
        string traversalTarget = Path.GetFullPath(Path.Combine(_snapshotRoot, "../../etc/passwd"));
        Directory.CreateDirectory(Path.GetDirectoryName(traversalTarget)!);
        File.WriteAllText(traversalTarget, "sentinel");

        try
        {
            await SnapshotCleanupHelper.DeleteSnapshotsForCameraAsync(
                cameraId, _db, _storagePathService.Object, _logger.Object, CancellationToken.None);

            await _db.SaveChangesAsync();

            // The DB row MUST be removed even when the file is skipped.
            Assert.Empty(_db.CameraSnapshots.Where(s => s.CameraId == cameraId));

            // The sentinel file outside the root must NOT have been deleted.
            Assert.True(File.Exists(traversalTarget), "File outside snapshot root must not be deleted.");
        }
        finally
        {
            if (File.Exists(traversalTarget))
            {
                File.Delete(traversalTarget);
            }
        }
    }

    // ── Multiple snapshots ────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteSnapshotsForCameraAsync_MultipleSnapshots_DeletesAllFilesAndRows()
    {
        var cameraId = SeedCamera(_db);
        var paths = new[] { "p/j/snap1.jpg", "p/j/snap2.jpg", "p/j/snap3.jpg" };
        var fullPaths = new System.Collections.Generic.List<string>();

        foreach (string rel in paths)
        {
            SeedSnapshot(cameraId, rel);
            fullPaths.Add(CreatePhysicalFile(rel));
        }

        await SnapshotCleanupHelper.DeleteSnapshotsForCameraAsync(
            cameraId, _db, _storagePathService.Object, _logger.Object, CancellationToken.None);

        await _db.SaveChangesAsync();

        Assert.Empty(_db.CameraSnapshots.Where(s => s.CameraId == cameraId));
        foreach (string full in fullPaths)
        {
            Assert.False(File.Exists(full), $"File should have been deleted: {full}");
        }
    }
}
