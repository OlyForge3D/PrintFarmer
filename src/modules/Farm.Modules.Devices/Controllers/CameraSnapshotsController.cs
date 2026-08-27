using Farm.Infrastructure.Authorization;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Security;
using Farm.Infrastructure.Services.Queue;
using Farm.Infrastructure.Services.StorageManagement;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Farm.Modules.Devices.Controllers;

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
    IQueueResourceAuthorizationService queueResourceAuthorization) : ControllerBase
{
    private readonly AppDbContext _db = db;
    private readonly IStoragePathService _storagePathService = storagePathService;
    private readonly ILogger<CameraSnapshotsController> _logger = logger;
    private readonly IQueueResourceAuthorizationService _queueResourceAuthorization = queueResourceAuthorization;

    /// <summary>
    /// Enforces the same PrinterGroup access rules as <c>PrintersController</c> so
    /// snapshot reads and deletes cannot be reached by a caller outside the printer's group.
    /// </summary>
    private Task<bool> CanAccessPrinterAsync(
        Guid printerId,
        PrinterGroupAccessLevel accessLevel,
        CancellationToken ct) =>
        _queueResourceAuthorization.CanAccessPrinterAsync(User, printerId, accessLevel, ct);

    /// <summary>
    /// Lists snapshots for a specific print job, scoped to printers the caller may view.
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

        // Snapshots carry PrinterId directly, so scope on that rather than resolving the
        // job's assigned printer (which may differ from where the snapshot was captured).
        Guid[] printerIds = snapshots.Select(s => s.PrinterId).Distinct().ToArray();
        IReadOnlySet<Guid> allowedPrinterIds = await _queueResourceAuthorization.FilterAccessiblePrinterIdsAsync(
            User,
            printerIds,
            PrinterGroupAccessLevel.View,
            ct);
        snapshots = snapshots.Where(s => allowedPrinterIds.Contains(s.PrinterId)).ToList();

        return Ok(snapshots);
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
        if (!await CanAccessPrinterAsync(printerId, PrinterGroupAccessLevel.View, ct))
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
#pragma warning disable CA3003 // Path resolved from a Guid DB lookup then canonicalized and containment-checked below — no injection risk
    public async Task<IActionResult> GetImageAsync(Guid snapshotId, CancellationToken ct)
    {
        CameraSnapshot? snapshot = await _db.CameraSnapshots.FindAsync([snapshotId], ct);
        if (snapshot is null)
        {
            return NotFound();
        }

        if (!await CanAccessPrinterAsync(snapshot.PrinterId, PrinterGroupAccessLevel.View, ct))
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

        if (!System.IO.File.Exists(canonicalFull))
        {
            _logger.LogWarning("[CameraSnapshots] Snapshot file not found on disk: {Path}", canonicalFull);
            return NotFound("Snapshot file not found on disk.");
        }

        FileStream fileStream = new(canonicalFull, FileMode.Open, FileAccess.Read, FileShare.Read);
        return File(fileStream, "image/jpeg");
    }
#pragma warning restore CA3003

    /// <summary>
    /// Deletes a snapshot and its file from disk. This is irreversible and destroys print-event
    /// evidence, so it requires a write-level permission and a write-level (Submit or higher)
    /// PrinterGroup scope check, distinct from the View-level check used by the read endpoints.
    /// </summary>
    [HttpDelete("{snapshotId:guid}")]
    [RequirePermission(PrintFarmerPermissions.Queue.Write)]
#pragma warning disable CA3003 // Path resolved from a Guid DB lookup then canonicalized and containment-checked below — no injection risk
    public async Task<IActionResult> DeleteAsync(Guid snapshotId, CancellationToken ct)
    {
        CameraSnapshot? snapshot = await _db.CameraSnapshots.FindAsync([snapshotId], ct);
        if (snapshot is null)
        {
            return NotFound();
        }

        if (!await CanAccessPrinterAsync(snapshot.PrinterId, PrinterGroupAccessLevel.Submit, ct))
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

        if (System.IO.File.Exists(canonicalFull))
        {
            System.IO.File.Delete(canonicalFull);
        }

        _db.CameraSnapshots.Remove(snapshot);
        await _db.SaveChangesAsync(ct);

        return NoContent();
    }
#pragma warning restore CA3003
}
