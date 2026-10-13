using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.UnitOfWork;
using Farm.Infrastructure.Security;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.SignalR;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.Cameras;

/// <summary>
/// Discovers cameras through the printer's backend plugin and upserts <see cref="Camera"/> rows
/// linked to the printer. Cameras that are user-modified or standalone are never overwritten,
/// cameras the backend stops reporting are marked offline rather than deleted, and cameras a user
/// removed from the printer (see <see cref="DismissedPrinterCamera"/>) are not re-created.
/// </summary>
public sealed class PrinterCameraSynchronizer(
    AppDbContext db,
    IUnitOfWork unitOfWork,
    IBackendCapabilityFactory capabilityFactory,
    IBackendClientFactory backendFactory,
    IPrinterStatusCacheReader statusCache,
    IHubContext<PrinterHub> hubContext,
    ILogger<PrinterCameraSynchronizer> logger,
    TimeProvider? timeProvider = null) : IPrinterCameraSynchronizer
{
    /// <summary>Lowercase SignalR event raised when a sync changes a printer's cameras.</summary>
    public const string CamerasUpdatedEventName = "camerasupdated";

    /// <summary>Health message recorded on cameras the backend no longer reports.</summary>
    public const string NotReportedHealthMessage = "No longer reported by the printer backend";

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <inheritdoc/>
    public async Task<IReadOnlyList<PrinterCameraSyncResult>> SyncAllAsync(CancellationToken ct)
    {
        List<Guid> printerIds = await db.Printers
            .AsNoTracking()
            .Where(p => p.IsEnabled)
            .Select(p => p.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var results = new List<PrinterCameraSyncResult>(printerIds.Count);
        foreach (Guid printerId in printerIds)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                results.Add(await SyncPrinterAsync(printerId, explicitRequest: false, ct).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Camera sync failed for printer {PrinterId}", printerId);
                db.ChangeTracker.Clear();
            }
        }

        return results;
    }

    /// <inheritdoc/>
    public async Task<int> ClearDismissalsAsync(Guid printerId, CancellationToken ct)
    {
        List<DismissedPrinterCamera> dismissals = await db.DismissedPrinterCameras
            .Where(d => d.PrinterId == printerId)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        if (dismissals.Count == 0)
        {
            return 0;
        }

        db.DismissedPrinterCameras.RemoveRange(dismissals);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        logger.LogInformation("Cleared {Count} dismissed camera(s) for printer {PrinterId}", dismissals.Count, printerId);
        return dismissals.Count;
    }

    /// <inheritdoc/>
    public async Task<PrinterCameraSyncResult> SyncPrinterAsync(Guid printerId, bool explicitRequest, CancellationToken ct)
    {
        Printer? printer = await unitOfWork.Printers.FindByIdWithIncludesAsync(printerId, ct).ConfigureAwait(false);
        if (printer is null)
        {
            return PrinterCameraSyncResult.NotFound(printerId);
        }

        (bool discoverySucceeded, DiscoveredCamera? discovered) = await DiscoverAsync(printer, ct).ConfigureAwait(false);
        bool discoveredIsDismissed = discovered is not null && await db.DismissedPrinterCameras
            .AsNoTracking()
            .AnyAsync(d => d.PrinterId == printerId && d.Source == discovered.Source, ct)
            .ConfigureAwait(false);

        List<Camera> linked = await db.Cameras
            .Include(c => c.PrinterLinks)
            .Where(c => c.PrinterLinks.Any(l => l.PrinterId == printerId))
            .OrderBy(c => c.CreatedAt)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        DateTime now = _time.GetUtcNow().UtcDateTime;
        var touched = new HashSet<Guid>();
        Camera? discoveredCamera = null;
        int created = 0;
        int updated = 0;
        int markedOffline = 0;

        if (discovered is not null)
        {
            (discoveredCamera, bool wasCreated, bool wasUpdated) = ReconcileDiscovered(printer, discovered, discoveredIsDismissed, linked, now);
            if (discoveredCamera is not null)
            {
                touched.Add(discoveredCamera.Id);
            }

            created += wasCreated ? 1 : 0;
            updated += wasUpdated ? 1 : 0;
        }

        bool printerOnline = statusCache.GetStatus(printerId)?.IsOnline == true;
        if (discoverySucceeded && (explicitRequest || printerOnline))
        {
            foreach (Camera camera in linked.Where(c => !touched.Contains(c.Id) && IsSyncManaged(c) && c.PrinterLinks.Count == 1))
            {
                if (string.Equals(camera.HealthMessage, NotReportedHealthMessage, StringComparison.Ordinal))
                {
                    continue;
                }

                camera.HealthStatus = CameraHealthStatus.Unhealthy;
                camera.HealthMessage = NotReportedHealthMessage;
                camera.LastHealthCheck = now;
                camera.UpdatedAt = now;
                markedOffline++;
            }
        }

        updated += EnsureSinglePrimary(printerId, linked, discoveredCamera);

        var result = new PrinterCameraSyncResult(
            printerId,
            PrinterFound: true,
            discoverySucceeded,
            created,
            updated,
            markedOffline,
            linked.Select(c => c.Id).ToList());

        if (!result.Changed)
        {
            return result;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        logger.LogInformation(
            "Camera sync for printer {PrinterId}: created={Created}, updated={Updated}, markedOffline={MarkedOffline}",
            printerId,
            created,
            updated,
            markedOffline);

        await BroadcastAsync(result, ct).ConfigureAwait(false);
        return result;
    }

    private (Camera? Camera, bool Created, bool Updated) ReconcileDiscovered(
        Printer printer,
        DiscoveredCamera discovered,
        bool isDismissed,
        List<Camera> linked,
        DateTime now)
    {
        string? streamUrl = CameraContractClassifier.NormalizeTarget(discovered.StreamUrl);
        string? snapshotUrl = CameraContractClassifier.NormalizeTarget(discovered.SnapshotUrl);
        List<Camera> sameSource = linked.Where(c => c.Source == discovered.Source && !IsBuddyCamera(c)).ToList();

        Camera? match = sameSource
            .Where(IsSyncManaged)
            .OrderByDescending(c => string.Equals(c.Name, discovered.Name, StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();

        if (match is not null)
        {
            bool changed = false;
            if (!string.Equals(match.StreamUrl, streamUrl, StringComparison.Ordinal))
            {
                match.StreamUrl = streamUrl;
                changed = true;
            }

            if (!string.Equals(match.SnapshotUrl, snapshotUrl, StringComparison.Ordinal))
            {
                match.SnapshotUrl = snapshotUrl;
                changed = true;
            }

            if (!string.Equals(match.Name, discovered.Name, StringComparison.Ordinal))
            {
                match.Name = discovered.Name;
                changed = true;
            }

            if (string.Equals(match.HealthMessage, NotReportedHealthMessage, StringComparison.Ordinal))
            {
                match.HealthStatus = CameraHealthStatus.Healthy;
                match.HealthMessage = null;
                match.ConsecutiveFailures = 0;
                match.LastHealthCheck = now;
                changed = true;
            }

            if (changed)
            {
                match.UpdatedAt = now;
            }

            return (match, false, changed);
        }

        // A user-modified or standalone camera for this source owns the slot: never overwrite it
        // and never create a duplicate beside it.
        bool claimedByProtectedCamera = discovered.Source == CameraSource.Standalone
            ? sameSource.Any(c => string.Equals(c.Name, discovered.Name, StringComparison.OrdinalIgnoreCase))
            : sameSource.Count > 0;
        if (claimedByProtectedCamera)
        {
            return (null, false, false);
        }

        // The user removed this printer's discovered camera; only an explicit discovery request
        // (which clears the dismissal first) may bring it back.
        if (isDismissed)
        {
            return (null, false, false);
        }

        var camera = new Camera
        {
            Id = Guid.NewGuid(),
            Name = discovered.Name,
            StreamUrl = streamUrl,
            SnapshotUrl = snapshotUrl,
            IsEnabled = true,
            SortOrder = 0,
            Source = discovered.Source,
            CameraType = CameraType.General,
            HealthStatus = CameraHealthStatus.Healthy,
            LastHealthCheck = now,
            CreatedAt = now,
        };
        camera.LinkToPrinter(printer.Id, isPrimary: false, sortOrder: linked.Count);
        db.Cameras.Add(camera);
        linked.Add(camera);
        return (camera, true, false);
    }

    private static int EnsureSinglePrimary(Guid printerId, List<Camera> linked, Camera? preferred)
    {
        List<CameraPrinter> links = linked
            .SelectMany(c => c.PrinterLinks.Where(l => l.PrinterId == printerId).Select(l => (Camera: c, Link: l)))
            .OrderByDescending(x => x.Camera.UserModified)
            .ThenBy(x => x.Link.SortOrder)
            .ThenBy(x => x.Camera.CreatedAt)
            .Select(x => x.Link)
            .ToList();
        if (links.Count == 0)
        {
            return 0;
        }

        List<CameraPrinter> primaries = links.Where(l => l.IsPrimary).ToList();
        if (primaries.Count == 1)
        {
            return 0;
        }

        if (primaries.Count == 0)
        {
            CameraPrinter? target = preferred?.PrinterLinks.FirstOrDefault(l => l.PrinterId == printerId) ?? links[0];
            target.IsPrimary = true;
            return 1;
        }

        foreach (CameraPrinter extra in primaries.Skip(1))
        {
            extra.IsPrimary = false;
        }

        return primaries.Count - 1;
    }

    private static bool IsSyncManaged(Camera camera) =>
        !camera.UserModified && camera.Source != CameraSource.Standalone && !IsBuddyCamera(camera);

    /// <summary>
    /// True when removing <paramref name="camera"/> from a printer must be remembered so sync does not
    /// re-create it: plugin-discovered cameras only (not standalone or Prusa Buddy cameras).
    /// </summary>
    /// <param name="camera">Camera being removed from a printer.</param>
    internal static bool IsDismissible(Camera camera) =>
        camera.Source != CameraSource.Standalone && !IsBuddyCamera(camera);

    // Prusa Buddy cameras are configured from the printer's BuddyCameraIp (see
    // PrintersService.SyncBuddyCameraAsync), not reported by the PrusaLink plugin.
    internal static bool IsBuddyCamera(Camera camera) =>
        camera.Source == CameraSource.PrusaLink &&
        camera.StreamUrl?.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase) == true;

    private async Task<(bool Succeeded, DiscoveredCamera? Camera)> DiscoverAsync(Printer printer, CancellationToken ct)
    {
        var backend = (PrinterBackend)printer.Backend;
        string? streamUrl = null;
        string? snapshotUrl = null;

        try
        {
            if (capabilityFactory.TryGetConfiguredCameraDetectionClient(backend, out ISupportsConfiguredCameraDetection? detectionClient) &&
                detectionClient is not null)
            {
                (streamUrl, snapshotUrl) = await detectionClient.DetectConfiguredCameraUrlsAsync(
                    printer.BackendUrl,
                    printer.FrontendPort,
                    printer.Credential,
                    ct).ConfigureAwait(false);

                if (string.IsNullOrWhiteSpace(streamUrl) && string.IsNullOrWhiteSpace(snapshotUrl) &&
                    backendFactory.GetClient(backend) is ISupportsPrinterCameraProfile profile)
                {
                    (streamUrl, snapshotUrl) = profile.GetDefaultCameraUrls(printer);
                }
            }
            else if (capabilityFactory.TryGetCameraClientTyped(backend, out ISupportsCamera? cameraClient) && cameraClient is not null)
            {
                streamUrl = await cameraClient.GetCameraStreamUrlAsync(printer.BackendUrl, printer.FrontendPort, printer.Credential, ct).ConfigureAwait(false);
                snapshotUrl = await cameraClient.GetCameraSnapshotUrlAsync(printer.BackendUrl, printer.FrontendPort, printer.Credential, ct).ConfigureAwait(false);
            }
            else
            {
                return (false, null);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Camera discovery failed for printer {PrinterId}", printer.Id);
            return (false, null);
        }

        if (string.IsNullOrWhiteSpace(streamUrl) && string.IsNullOrWhiteSpace(snapshotUrl))
        {
            return (true, null);
        }

        string name = $"{printer.Name} Camera";
        if (name.Length > 100)
        {
            name = name[..100];
        }

        return (true, new DiscoveredCamera(name, MapBackendToCameraSource(backend), streamUrl, snapshotUrl));
    }

    private async Task BroadcastAsync(PrinterCameraSyncResult result, CancellationToken ct)
    {
        try
        {
            await hubContext.Clients
                .Group(AuthorizedHubGroups.Printer(result.PrinterId))
                .SendAsync(CamerasUpdatedEventName, new CamerasUpdatedEvent(result.PrinterId, result.CameraIds), ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to broadcast {EventName} for printer {PrinterId}", CamerasUpdatedEventName, result.PrinterId);
        }
    }

    /// <summary>Maps a printer backend to the camera source recorded on discovered cameras.</summary>
    /// <param name="backend">Printer backend.</param>
    public static CameraSource MapBackendToCameraSource(PrinterBackend backend) => backend switch
    {
        PrinterBackend.Moonraker => CameraSource.Moonraker,
        PrinterBackend.PrusaLink => CameraSource.PrusaLink,
        PrinterBackend.OctoPrint => CameraSource.OctoPrint,
        PrinterBackend.SDCP => CameraSource.SDCP,
        PrinterBackend.FlashForge => CameraSource.FlashForge,
        _ => CameraSource.Standalone,
    };

    private sealed record DiscoveredCamera(string Name, CameraSource Source, string? StreamUrl, string? SnapshotUrl);
}
