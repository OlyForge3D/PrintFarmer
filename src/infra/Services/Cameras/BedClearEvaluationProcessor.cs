using System.Buffers;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.Queue;
using Farm.Infrastructure.Services.StorageManagement;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.Cameras;

/// <summary>Processes persisted bed-clear samples away from the dispatch request path.</summary>
public sealed class BedClearEvaluationProcessor(
    AppDbContext db,
    IHttpClientFactory httpClientFactory,
    IStoragePathService storagePathService,
    IPrinterStatusSnapshotReader statusReader,
    IBedClearImageHashService imageHashService,
    ILogger<BedClearEvaluationProcessor> logger,
    TimeProvider? timeProvider = null)
{
    private static readonly TimeSpan CaptureWindow = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ProcessingLease = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MaximumStatusAge = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan MaximumCameraHealthAge = TimeSpan.FromMinutes(10);
    private const int MaximumImageBytes = 10 * 1024 * 1024;
    private const double PositionTolerance = 0.5;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task ProcessPendingAsync(CancellationToken ct)
    {
        DateTime now = _timeProvider.GetUtcNow().UtcDateTime;
        await db.BedClearEvaluations
            .Where(sample =>
                sample.State == BedClearEvaluationState.Processing &&
                sample.ProcessingStartedAtUtc < now - ProcessingLease)
            .ExecuteUpdateAsync(
                setters => setters
                .SetProperty(sample => sample.State, BedClearEvaluationState.Pending)
                .SetProperty(sample => sample.ProcessingStartedAtUtc, (DateTime?)null), ct);

        List<Guid> pendingIds = await db.BedClearEvaluations
            .AsNoTracking()
            .Where(sample => sample.State == BedClearEvaluationState.Pending)
            .OrderBy(sample => sample.CreatedAtUtc)
            .Select(sample => sample.Id)
            .Take(10)
            .ToListAsync(ct);

        foreach (Guid id in pendingIds)
        {
            DateTime startedAt = _timeProvider.GetUtcNow().UtcDateTime;
            int claimed = await db.BedClearEvaluations
                .Where(sample => sample.Id == id && sample.State == BedClearEvaluationState.Pending)
                .ExecuteUpdateAsync(
                    setters => setters
                    .SetProperty(sample => sample.State, BedClearEvaluationState.Processing)
                    .SetProperty(sample => sample.ProcessingStartedAtUtc, startedAt), ct);
            if (claimed == 0)
            {
                continue;
            }

            try
            {
                using var captureTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                captureTimeout.CancelAfter(CaptureWindow);
                await ProcessOneAsync(id, captureTimeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                await CompleteUnknownAsync(id, "evaluation_timeout", ct);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Bed-clear evaluation {EvaluationId} failed.", id);
                await CompleteUnknownAsync(id, "evaluation_failed", ct);
            }
        }
    }

    internal async Task ProcessOneAsync(Guid id, CancellationToken ct)
    {
        BedClearEvaluation? sample = await db.BedClearEvaluations
            .FirstOrDefaultAsync(candidate => candidate.Id == id, ct);
        if (sample is null)
        {
            return;
        }

        // The atomic claim bypasses EF's change tracker.
        await db.Entry(sample).ReloadAsync(ct);
        if (db.Entry(sample).State == EntityState.Detached ||
            sample.State != BedClearEvaluationState.Processing)
        {
            return;
        }

        DateTime now = _timeProvider.GetUtcNow().UtcDateTime;
        if (sample.AcknowledgedAtUtc > now ||
            now - sample.AcknowledgedAtUtc > CaptureWindow)
        {
            SetUnknown(sample, "capture_window_missed");
            await db.SaveChangesAsync(ct);
            return;
        }

        PrinterStatusSnapshot? status = statusReader.GetStatusSnapshot(sample.PrinterId);
        if (status is null ||
            !status.Status.IsOnline ||
            status.LastSeenAtUtc is null ||
            status.LastSeenAtUtc > now ||
            now - status.LastSeenAtUtc > MaximumStatusAge ||
            status.ObservedAtUtc is null ||
            status.ObservedAtUtc < sample.AcknowledgedAtUtc - CaptureWindow ||
            status.ObservedAtUtc > now)
        {
            SetUnknown(sample, "printer_status_stale");
            await db.SaveChangesAsync(ct);
            return;
        }

        if (!IsIdle(status) || await IsPrinterBusyAsync(sample.PrinterId, ct))
        {
            SetUnknown(sample, "printer_moved_to_dispatch");
            await db.SaveChangesAsync(ct);
            return;
        }

        Printer? printer = await db.Printers
            .AsNoTracking()
            .Include(candidate => candidate.CameraLinks)
                .ThenInclude(link => link.Camera)
            .FirstOrDefaultAsync(candidate => candidate.Id == sample.PrinterId, ct);
        if (printer?.BedTypeId is not Guid bedTypeId)
        {
            SetUnknown(sample, "bed_type_unconfigured");
            await db.SaveChangesAsync(ct);
            return;
        }

        if (sample.BedTypeId != bedTypeId)
        {
            SetUnknown(sample, "bed_type_changed");
            await db.SaveChangesAsync(ct);
            return;
        }

        Camera? camera = printer.CameraLinks
            .Where(link =>
                link.Camera.IsEnabled &&
                link.Camera.CameraType == CameraType.Bed &&
                !string.IsNullOrWhiteSpace(link.Camera.SnapshotUrl))
            .OrderByDescending(link => link.IsPrimary)
            .Select(link => link.Camera)
            .FirstOrDefault();
        if (camera is null)
        {
            SetUnknown(sample, "bed_camera_unavailable");
            await db.SaveChangesAsync(ct);
            return;
        }

        sample.CameraId = camera.Id;
        sample.CameraConfigurationAtUtc = camera.UpdatedAt ?? camera.CreatedAt;
        sample.BedTypeId = bedTypeId;
        sample.ToolheadX = status.Status.X;
        sample.ToolheadY = status.Status.Y;
        sample.ToolheadZ = status.Status.Z;

        if (camera.HealthStatus != CameraHealthStatus.Healthy ||
            camera.LastHealthCheck is null ||
            camera.LastHealthCheck > now ||
            now - camera.LastHealthCheck > MaximumCameraHealthAge)
        {
            SetUnknown(sample, "camera_health_stale");
            await db.SaveChangesAsync(ct);
            return;
        }

        if (!IsFinitePosition(status))
        {
            SetUnknown(sample, "toolhead_position_unavailable");
            await db.SaveChangesAsync(ct);
            return;
        }

        string snapshotUrl = camera.SnapshotUrl!;
        if (!CameraUrlValidator.IsUrlSafeForProbing(snapshotUrl))
        {
            SetUnknown(sample, "camera_url_unsafe");
            await db.SaveChangesAsync(ct);
            return;
        }

        byte[] currentImage;
        try
        {
            using HttpClient client = httpClientFactory.CreateClient("BedClearSnapshot");
            using var request = new HttpRequestMessage(HttpMethod.Get, snapshotUrl);
            request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue
            {
                NoCache = true,
                NoStore = true,
            };
            using HttpResponseMessage response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                ct);
            response.EnsureSuccessStatusCode();
            DateTime responseTime = _timeProvider.GetUtcNow().UtcDateTime;
            DateTime? lastModified = response.Content.Headers.LastModified?.UtcDateTime;
            if (response.Headers.Age > CaptureWindow ||
                lastModified > responseTime ||
                responseTime - lastModified > CaptureWindow)
            {
                SetUnknown(sample, "camera_frame_stale");
                await db.SaveChangesAsync(ct);
                return;
            }

            if (response.Content.Headers.ContentLength is > MaximumImageBytes)
            {
                SetUnknown(sample, "image_too_large");
                await db.SaveChangesAsync(ct);
                return;
            }

            await using Stream imageStream = await response.Content.ReadAsStreamAsync(ct);
            currentImage = await ReadBoundedAsync(imageStream, ct);
        }
        catch (HttpRequestException)
        {
            SetUnknown(sample, "camera_capture_failed");
            await db.SaveChangesAsync(ct);
            return;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            SetUnknown(sample, "camera_capture_timeout");
            await db.SaveChangesAsync(ct);
            return;
        }

        string relativePath = Path.Join("bed-clear", sample.PrinterId.ToString("N"), $"{sample.Id:N}.jpg");
        string snapshotRoot = storagePathService.GetSnapshotStorageDirectory();
        string fullPath = Path.GetFullPath(Path.Join(snapshotRoot, relativePath));
        string canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(snapshotRoot));
        if (!fullPath.StartsWith(canonicalRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            SetUnknown(sample, "image_storage_path_invalid");
            await db.SaveChangesAsync(ct);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllBytesAsync(fullPath, currentImage, ct);
        sample.ImageFilePath = relativePath;
        sample.CapturedAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
        if (sample.CapturedAtUtc - sample.AcknowledgedAtUtc > CaptureWindow)
        {
            SetUnknown(sample, "capture_window_missed");
            await db.SaveChangesAsync(ct);
            return;
        }

        PrinterStatusSnapshot? afterCaptureStatus = statusReader.GetStatusSnapshot(sample.PrinterId);
        if (afterCaptureStatus is null ||
            !afterCaptureStatus.Status.IsOnline ||
            !IsIdle(afterCaptureStatus) ||
            !IsFinitePosition(afterCaptureStatus) ||
            Math.Abs(afterCaptureStatus.Status.X!.Value - sample.ToolheadX!.Value) > PositionTolerance ||
            Math.Abs(afterCaptureStatus.Status.Y!.Value - sample.ToolheadY!.Value) > PositionTolerance ||
            Math.Abs(afterCaptureStatus.Status.Z!.Value - sample.ToolheadZ!.Value) > PositionTolerance ||
            await IsPrinterBusyAsync(sample.PrinterId, ct))
        {
            SetUnknown(sample, "printer_moved_during_capture");
            await db.SaveChangesAsync(ct);
            return;
        }

        List<BedClearEvaluation> references = await db.BedClearEvaluations
            .AsNoTracking()
            .Where(reference =>
                reference.Id != sample.Id &&
                reference.PrinterId == sample.PrinterId &&
                reference.CameraId == camera.Id &&
                reference.BedTypeId == bedTypeId &&
                reference.CameraConfigurationAtUtc == sample.CameraConfigurationAtUtc &&
                reference.ToolheadX != null &&
                reference.ToolheadY != null &&
                reference.ToolheadZ != null &&
                Math.Abs(reference.ToolheadX.Value - sample.ToolheadX!.Value) <= PositionTolerance &&
                Math.Abs(reference.ToolheadY.Value - sample.ToolheadY!.Value) <= PositionTolerance &&
                Math.Abs(reference.ToolheadZ.Value - sample.ToolheadZ!.Value) <= PositionTolerance &&
                reference.IsReferenceEligible &&
                reference.IsActiveReference &&
                reference.GroundTruth == BedClearGroundTruth.Clear &&
                reference.GroundTruthBySubject != null &&
                reference.GroundTruthBySubject != reference.AcknowledgedBySubject &&
                reference.ImageFilePath != null)
            .OrderByDescending(reference => reference.GroundTruthAtUtc)
            .Take(BedClearImageClassifier.MinimumReferences)
            .ToListAsync(ct);

        var referenceHashes = new List<ulong>(references.Count);
        foreach (BedClearEvaluation reference in references)
        {
            string referencePath = Path.GetFullPath(Path.Join(snapshotRoot, reference.ImageFilePath));
            if (referencePath.StartsWith(canonicalRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                File.Exists(referencePath))
            {
                byte[] referenceImage = await File.ReadAllBytesAsync(referencePath, ct);
                if (imageHashService.TryCreateHash(referenceImage, out ulong referenceHash, out _))
                {
                    referenceHashes.Add(referenceHash);
                }
            }
        }

        ulong? currentHash = imageHashService.TryCreateHash(
            currentImage,
            out ulong currentHashValue,
            out string? invalidImageReason)
            ? currentHashValue
            : null;
        BedClearImageResult result = currentHash is null
            ? BedClearImageResult.Unknown(invalidImageReason ?? "invalid_image")
            : BedClearImageClassifier.Classify(currentHash, referenceHashes);
        sample.IsReferenceEligible = currentHash.HasValue;
        sample.State = BedClearEvaluationState.Complete;
        sample.Verdict = result.Verdict;
        sample.Score = result.Score;
        sample.ReasonCode = result.ReasonCode;
        await db.SaveChangesAsync(ct);
    }

    private async Task CompleteUnknownAsync(Guid id, string reason, CancellationToken ct)
    {
        BedClearEvaluation? sample = await db.BedClearEvaluations
            .FirstOrDefaultAsync(candidate => candidate.Id == id, ct);
        if (sample is null)
        {
            return;
        }

        SetUnknown(sample, reason);
        await db.SaveChangesAsync(ct);
    }

    private static void SetUnknown(BedClearEvaluation sample, string reason)
    {
        sample.State = BedClearEvaluationState.Complete;
        sample.Verdict = BedClearDetectorVerdict.Unknown;
        sample.Score = null;
        sample.ReasonCode = reason;
        sample.IsReferenceEligible = false;
    }

    private static bool IsFinitePosition(PrinterStatusSnapshot status) =>
        status.Status.X is double x && double.IsFinite(x) &&
        status.Status.Y is double y && double.IsFinite(y) &&
        status.Status.Z is double z && double.IsFinite(z);

    private static bool IsIdle(PrinterStatusSnapshot status) =>
        status.Status.State is "Idle" or "Ready" or "Finished" or "Complete" or "Cancelled";

    private Task<bool> IsPrinterBusyAsync(Guid printerId, CancellationToken ct) =>
        db.PrintJobs.WhereOccupiesPrinter()
            .AnyAsync(job => job.AssignedPrinterId == printerId, ct);

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, CancellationToken ct)
    {
        await using var bounded = new MemoryStream();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
            {
                if (bounded.Length + read > MaximumImageBytes)
                {
                    throw new InvalidDataException("Camera image exceeds the maximum supported size.");
                }

                await bounded.WriteAsync(buffer.AsMemory(0, read), ct);
            }

            return bounded.ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}

/// <summary>Polls durable pending samples without participating in dispatch.</summary>
public sealed class BedClearEvaluationBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<BedClearEvaluationBackgroundService> logger,
    TimeProvider? timeProvider = null) : BackgroundService
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
                BedClearEvaluationProcessor processor =
                    scope.ServiceProvider.GetRequiredService<BedClearEvaluationProcessor>();
                await processor.ProcessPendingAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Bed-clear detector background scan failed.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), _timeProvider, stoppingToken);
        }
    }
}
