using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
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
    ILogger<CameraSnapshotsController> logger) : ControllerBase
{
    private readonly AppDbContext _db = db;
    private readonly IStoragePathService _storagePathService = storagePathService;
    private readonly ILogger<CameraSnapshotsController> _logger = logger;

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

        string snapshotRoot = _storagePathService.GetSnapshotStorageDirectory();
        string fullPath = Path.Combine(snapshotRoot, snapshot.FilePath);

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

        // Delete file from disk
        string snapshotRoot = _storagePathService.GetSnapshotStorageDirectory();
        string fullPath = Path.Combine(snapshotRoot, snapshot.FilePath);
        if (System.IO.File.Exists(fullPath))
        {
            System.IO.File.Delete(fullPath);
        }

        _db.CameraSnapshots.Remove(snapshot);
        await _db.SaveChangesAsync(ct);

        return NoContent();
    }
}

/// <summary>
/// DTO for camera snapshot metadata (excludes file path for security).
/// </summary>
public class CameraSnapshotDto
{
    public Guid Id { get; set; }

    public Guid PrinterId { get; set; }

    public Guid CameraId { get; set; }

    public Guid? PrintJobId { get; set; }

    public string EventType { get; set; } = string.Empty;

    public DateTime CapturedAt { get; set; }

    public long? FileSizeBytes { get; set; }
}
