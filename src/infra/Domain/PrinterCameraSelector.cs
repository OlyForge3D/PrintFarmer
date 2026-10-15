namespace Farm.Infrastructure.Domain;

/// <summary>
/// Why a printer has (or does not have) a camera usable for snapshots.
/// </summary>
public enum PrinterCameraAvailability
{
    /// <summary>An enabled camera with a snapshot URL that is not reported offline was found.</summary>
    Available,

    /// <summary>No camera is linked to the printer.</summary>
    NoCameraLinked,

    /// <summary>Cameras are linked, but every one of them is disabled.</summary>
    AllCamerasDisabled,

    /// <summary>Enabled cameras are linked, but none has a snapshot URL.</summary>
    NoSnapshotUrl,

    /// <summary>Every enabled camera with a snapshot URL is currently reported unhealthy (offline).</summary>
    CamerasOffline,
}

/// <summary>
/// Result of selecting a snapshot camera for a printer.
/// </summary>
/// <param name="Camera">The selected camera, or null when none is usable. For <see cref="PrinterCameraAvailability.CamerasOffline"/> this is the best offline candidate.</param>
/// <param name="Availability">Why the camera was (or was not) selected.</param>
public sealed record PrinterCameraSelection(Camera? Camera, PrinterCameraAvailability Availability);

/// <summary>
/// Deterministic camera selection for a printer through the <see cref="CameraPrinter"/> join:
/// the printer's primary camera first, then link sort order, camera sort order, name and id.
/// Shared cameras are ordered using the link that belongs to the requested printer.
/// </summary>
public static class PrinterCameraSelector
{
    /// <summary>
    /// Orders the cameras linked to a printer (requires <see cref="CameraPrinter.Camera"/> to be loaded).
    /// </summary>
    /// <param name="links">The printer's camera links (<see cref="Printer.CameraLinks"/>).</param>
    public static IReadOnlyList<Camera> OrderCameras(IEnumerable<CameraPrinter> links)
    {
        ArgumentNullException.ThrowIfNull(links);
        return links
            .Where(l => l.Camera is not null)
            .OrderByDescending(l => l.IsPrimary)
            .ThenBy(l => l.SortOrder)
            .ThenBy(l => l.Camera.SortOrder)
            .ThenBy(l => l.Camera.Name, StringComparer.Ordinal)
            .ThenBy(l => l.Camera.Id)
            .Select(l => l.Camera)
            .ToList();
    }

    /// <summary>
    /// Orders cameras for a specific printer using each camera's link to that printer
    /// (requires <see cref="Camera.PrinterLinks"/> to be loaded). Cameras not linked to the printer are excluded.
    /// </summary>
    /// <param name="cameras">Candidate cameras.</param>
    /// <param name="printerId">The printer whose link ordering applies.</param>
    public static IReadOnlyList<Camera> OrderCamerasForPrinter(IEnumerable<Camera> cameras, Guid printerId)
    {
        ArgumentNullException.ThrowIfNull(cameras);
        return OrderCameras(cameras
            .SelectMany(c => c.PrinterLinks
                .Where(l => l.PrinterId == printerId)
                .Take(1)
                .Select(l => new CameraPrinter
                {
                    CameraId = c.Id,
                    Camera = c,
                    PrinterId = printerId,
                    IsPrimary = l.IsPrimary,
                    SortOrder = l.SortOrder,
                })));
    }

    /// <summary>
    /// Selects the camera to use for snapshot-based features (e.g. failure detection) from an ordered list:
    /// the first enabled camera with a snapshot URL that is not reported unhealthy, falling back through the list.
    /// </summary>
    /// <param name="orderedCameras">Cameras already ordered by <see cref="OrderCameras"/> or <see cref="OrderCamerasForPrinter"/>.</param>
    public static PrinterCameraSelection SelectSnapshotCamera(IReadOnlyList<Camera> orderedCameras)
    {
        ArgumentNullException.ThrowIfNull(orderedCameras);
        if (orderedCameras.Count == 0)
        {
            return new PrinterCameraSelection(null, PrinterCameraAvailability.NoCameraLinked);
        }

        List<Camera> enabled = orderedCameras.Where(c => c.IsEnabled).ToList();
        if (enabled.Count == 0)
        {
            return new PrinterCameraSelection(null, PrinterCameraAvailability.AllCamerasDisabled);
        }

        List<Camera> withSnapshot = enabled.Where(c => !string.IsNullOrWhiteSpace(c.SnapshotUrl)).ToList();
        if (withSnapshot.Count == 0)
        {
            return new PrinterCameraSelection(null, PrinterCameraAvailability.NoSnapshotUrl);
        }

        Camera? online = withSnapshot.FirstOrDefault(c => c.HealthStatus != CameraHealthStatus.Unhealthy);
        return online is not null
            ? new PrinterCameraSelection(online, PrinterCameraAvailability.Available)
            : new PrinterCameraSelection(withSnapshot[0], PrinterCameraAvailability.CamerasOffline);
    }
}
