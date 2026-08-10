using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Services.Queue;
using Farm.Infrastructure.Services.StorageManagement;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Endpoints for viewing camera snapshots captured on print events.
/// </summary>
[ApiController]
[Route("api/snapshots")]
[Tags("Camera Snapshots")]
[Authorize]
public class CameraSnapshotsController(
    AppDbContext db,
    IStoragePathService storagePathService,
    ILogger<CameraSnapshotsController> logger,
    IQueueResourceAuthorizationService? queueResourceAuthorization = null) : ControllerBase
{
    private readonly AppDbContext _db = db;
    private readonly IStoragePathService _storagePathService = storagePathService;
    private readonly ILogger<CameraSnapshotsController> _logger = logger;
    private readonly IQueueResourceAuthorizationService? _queueResourceAuthorization = queueResourceAuthorization;

    /// <summary>
    /// Enforces the same PrinterGroup access rules as <see cref="Farm.Web.Api.Controllers.PrintersController"/>
    /// on the printer that captured a snapshot, so a caller excluded from a printer's group cannot
    /// enumerate or view its snapshots (mirrors issue #1421's fix for CamerasController).
    /// </summary>
    private async Task<bool> CanAccessSnapshotPrinterAsync(Guid printerId, PrinterGroupAccessLevel accessLevel, CancellationToken ct)
    {
        return _queueResourceAuthorization is not null &&
            await _queueResourceAuthorization.CanAccessPrinterAsync(User, printerId, accessLevel, ct);
    }

    /// <summary>
    /// Lists snapshots for a specific print job.
    /// </summary>
    [HttpGet("by-job/{printJobId:guid}")]
    public async Task<IActionResult> GetByPrintJobAsync(Guid printJobId, CancellationToken ct)
    {
        List<CameraSnapshotDto> snapshots = await _db.CameraSnapshots
            .Where(s => s.PrintJobId == printJobId)
            .OrderBy(s => s.CapturedAt)
            .Select(s => new CameraSnapshotDto
            {
                Id = s.Id,
                PrinterId = s.PrinterId,
                CameraId = s.CameraId,
                PrintJobId = s.PrintJobId,
                EventType = s.EventType,
                CapturedAt = s.CapturedAt,
                FileSizeBytes = s.FileSizeBytes,
            })
            .ToListAsync(ct);

        if (snapshots.Count == 0)
        {
            return Ok(snapshots);
        }

        Guid[] printerIds = snapshots.Select(s => s.PrinterId).Distinct().ToArray();
        IReadOnlySet<Guid> allowed = _queueResourceAuthorization is null
            ? new HashSet<Guid>()
            : await _queueResourceAuthorization.FilterAccessiblePrinterIdsAsync(User, printerIds, PrinterGroupAccessLevel.View, ct);

        return Ok(snapshots.Where(s => allowed.Contains(s.PrinterId)).ToList());
    }

    /// <summary>
    /// Lists snapshots for a specific printer.
    /// </summary>
    [HttpGet("by-printer/{printerId:guid}")]
    public async Task<IActionResult> GetByPrinterAsync(
        Guid printerId,
        [FromQuery] int limit = 50,
        [FromQuery] int offset = 0,
        CancellationToken ct = default)
    {
        if (!await CanAccessSnapshotPrinterAsync(printerId, PrinterGroupAccessLevel.View, ct))
        {
            return NotFound();
        }

        limit = Math.Clamp(limit, 1, 200);
        offset = Math.Max(offset, 0);
        List<CameraSnapshotDto> snapshots = await _db.CameraSnapshots
            .Where(s => s.PrinterId == printerId)
            .OrderByDescending(s => s.CapturedAt)
            .Skip(offset)
            .Take(limit)
            .Select(s => new CameraSnapshotDto
            {
                Id = s.Id,
                PrinterId = s.PrinterId,
                CameraId = s.CameraId,
                PrintJobId = s.PrintJobId,
                EventType = s.EventType,
                CapturedAt = s.CapturedAt,
                FileSizeBytes = s.FileSizeBytes,
            })
            .ToListAsync(ct);

        return Ok(snapshots);
    }

    /// <summary>
    /// Serves the actual snapshot image file.
    /// </summary>
    [HttpGet("{snapshotId:guid}/image")]
    public async Task<IActionResult> GetImageAsync(Guid snapshotId, CancellationToken ct)
    {
        CameraSnapshot? snapshot = await _db.CameraSnapshots.FindAsync([snapshotId], ct);
        if (snapshot is null)
        {
            return NotFound();
        }

        if (!await CanAccessSnapshotPrinterAsync(snapshot.PrinterId, PrinterGroupAccessLevel.View, ct))
        {
            return NotFound();
        }

        string snapshotRoot = _storagePathService.GetSnapshotStorageDirectory();
        string fullPath = Path.Join(snapshotRoot, snapshot.FilePath);

        // Prevent path traversal: canonicalize and verify containment.
        // TrimEndingDirectorySeparator avoids double-separator when config path has a trailing slash.
        string canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(snapshotRoot));
        string canonicalFull = Path.GetFullPath(fullPath);
        if (!canonicalFull.StartsWith(canonicalRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !canonicalFull.Equals(canonicalRoot, StringComparison.Ordinal))
        {
            _logger.LogWarning("[CameraSnapshots] Path traversal attempt blocked for snapshot {SnapshotId}", snapshotId);
            return BadRequest("Invalid snapshot path.");
        }

        if (!System.IO.File.Exists(fullPath))
        {
            _logger.LogWarning("[CameraSnapshots] Snapshot file not found on disk: {Path}", fullPath);
            return NotFound("Snapshot file not found on disk.");
        }

        FileStream fileStream = new(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return File(fileStream, "image/jpeg");
    }

    /// <summary>
    /// Deletes a snapshot and its file from disk.
    /// </summary>
    [HttpDelete("{snapshotId:guid}")]
    public async Task<IActionResult> DeleteAsync(Guid snapshotId, CancellationToken ct)
    {
        CameraSnapshot? snapshot = await _db.CameraSnapshots.FindAsync([snapshotId], ct);
        if (snapshot is null)
        {
            return NotFound();
        }

        if (!await CanAccessSnapshotPrinterAsync(snapshot.PrinterId, PrinterGroupAccessLevel.Manage, ct))
        {
            return NotFound();
        }

        // Delete file from disk
        string snapshotRoot = _storagePathService.GetSnapshotStorageDirectory();
        string fullPath = Path.Join(snapshotRoot, snapshot.FilePath);

        // Prevent path traversal: canonicalize and verify containment.
        // TrimEndingDirectorySeparator avoids double-separator when config path has a trailing slash.
        string canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(snapshotRoot));
        string canonicalFull = Path.GetFullPath(fullPath);
        if (!canonicalFull.StartsWith(canonicalRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !canonicalFull.Equals(canonicalRoot, StringComparison.Ordinal))
        {
            _logger.LogWarning("[CameraSnapshots] Path traversal attempt blocked for snapshot {SnapshotId}", snapshotId);
            return BadRequest("Invalid snapshot path.");
        }

        if (System.IO.File.Exists(fullPath))
        {
            System.IO.File.Delete(fullPath);
        }

        _db.CameraSnapshots.Remove(snapshot);
        await _db.SaveChangesAsync(ct);

        return NoContent();
    }
}
