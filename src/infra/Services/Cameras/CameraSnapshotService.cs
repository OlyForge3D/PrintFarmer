using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Cameras;
using Farm.Infrastructure.Services.StorageManagement;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.Cameras;

/// <summary>
/// Captures camera snapshots on print events, stores them to the filesystem,
/// and creates tracking records in the database.
/// </summary>
public class CameraSnapshotService : ICameraSnapshotService
{
    private readonly AppDbContext _db;
    private readonly ICameraRepository _cameraRepository;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IStoragePathService _storagePathService;
    private readonly ILogger<CameraSnapshotService> _logger;

    public CameraSnapshotService(
        AppDbContext db,
        ICameraRepository cameraRepository,
        IHttpClientFactory httpClientFactory,
        IStoragePathService storagePathService,
        ILogger<CameraSnapshotService> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _cameraRepository = cameraRepository ?? throw new ArgumentNullException(nameof(cameraRepository));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _storagePathService = storagePathService ?? throw new ArgumentNullException(nameof(storagePathService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task CaptureSnapshotAsync(Guid printerId, string eventType, Guid? printJobId = null, CancellationToken ct = default)
    {
        List<Camera> cameras = await _cameraRepository.GetByPrinterIdAsync(printerId, ct);

        if (cameras.Count == 0)
        {
            _logger.LogDebug(
                "[CameraSnapshot] No cameras associated with printer {PrinterId}. Skipping snapshot.",
                printerId);
            return;
        }

        List<Camera> snapshotCameras = cameras
            .Where(c => c.IsEnabled && !string.IsNullOrWhiteSpace(c.SnapshotUrl))
            .ToList();

        if (snapshotCameras.Count == 0)
        {
            _logger.LogDebug(
                "[CameraSnapshot] No cameras with snapshot URLs found for printer {PrinterId}. Skipping.",
                printerId);
            return;
        }

        string snapshotRoot = _storagePathService.GetSnapshotStorageDirectory();
        string timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");

        using HttpClient httpClient = _httpClientFactory.CreateClient("CameraSnapshot");

        foreach (Camera camera in snapshotCameras)
        {
            try
            {
                await CaptureFromCameraAsync(
                    httpClient, camera, printerId, printJobId, eventType,
                    snapshotRoot, timestamp, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(
                    ex,
                    "[CameraSnapshot] Failed to capture snapshot from camera {CameraId} ({CameraName}) for printer {PrinterId} on event {EventType}",
                    camera.Id, camera.Name, printerId, eventType);
            }
        }
    }

    private async Task CaptureFromCameraAsync(
        HttpClient httpClient,
        Camera camera,
        Guid printerId,
        Guid? printJobId,
        string eventType,
        string snapshotRoot,
        string timestamp,
        CancellationToken ct)
    {
        _logger.LogDebug(
            "[CameraSnapshot] Capturing snapshot from camera {CameraId} ({CameraName}) on event {EventType}",
            camera.Id, camera.Name, eventType);

        // Build the storage path: snapshots/{printerId}/{jobId}/{timestamp}_{eventType}_{cameraId}.jpg
        string printerDir = printerId.ToString();
        string jobDir = printJobId?.ToString() ?? "no-job";
        string fileName = $"{timestamp}_{eventType}_{camera.Id:N}.jpg";
        string relativePath = Path.Combine(printerDir, jobDir, fileName);
        string fullPath = Path.Combine(snapshotRoot, relativePath);

        // Ensure directory exists
        string? directory = Path.GetDirectoryName(fullPath);
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        // Fetch the snapshot JPEG
        using HttpResponseMessage response = await httpClient.GetAsync(camera.SnapshotUrl, ct);
        response.EnsureSuccessStatusCode();

        // Write to filesystem
        await using FileStream fileStream = new(fullPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await response.Content.CopyToAsync(fileStream, ct);
        long fileSize = fileStream.Length;

        // Create tracking record
        var snapshot = new CameraSnapshot
        {
            Id = Guid.NewGuid(),
            PrinterId = printerId,
            CameraId = camera.Id,
            PrintJobId = printJobId,
            EventType = eventType,
            FilePath = relativePath,
            CapturedAt = DateTime.UtcNow,
            FileSizeBytes = fileSize,
        };

        _db.CameraSnapshots.Add(snapshot);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "[CameraSnapshot] Captured snapshot from camera {CameraName} for printer {PrinterId} on {EventType} ({FileSize} bytes). Path: {FilePath}",
            camera.Name, printerId, eventType, fileSize, relativePath);
    }
}
