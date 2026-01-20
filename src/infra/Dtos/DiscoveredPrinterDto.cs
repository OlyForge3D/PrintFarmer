using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure;

/// <summary>
/// Printer discovered during network scanning.
/// Now a type alias to PrinterInfoDto for backward compatibility.
/// All discovery operations should use PrinterInfoDto going forward.
/// </summary>
public class DiscoveredPrinterDto : PrinterInfoDto
{
    /// <summary>
    /// Create a DiscoveredPrinterDto from raw discovery data.
    /// </summary>
    /// <param name="ipAddress">IP address of the discovered printer.</param>
    /// <param name="serverUrl">Server URL for printer communication.</param>
    /// <param name="name">Display name of the printer.</param>
    /// <param name="backend">Printer backend type (Moonraker, PrusaLink, etc.).</param>
    /// <param name="backendPort">Optional backend API port.</param>
    /// <param name="frontendPort">Optional frontend web interface port.</param>
    /// <param name="manufacturer">Optional manufacturer name.</param>
    /// <param name="model">Optional printer model name.</param>
    /// <param name="cameraStreamUrl">Optional camera stream URL.</param>
    /// <param name="cameraSnapshotUrl">Optional camera snapshot URL.</param>
    /// <returns>A new DiscoveredPrinterDto populated with discovery data.</returns>
    public static DiscoveredPrinterDto FromProbe(
        string ipAddress,
        string serverUrl,
        string name,
        PrinterBackend backend,
        int? backendPort = null,
        int? frontendPort = null,
        string? manufacturer = null,
        string? model = null,
        string? cameraStreamUrl = null,
        string? cameraSnapshotUrl = null) =>
        new()
        {
            IpAddress = ipAddress,
            ServerUrl = serverUrl,
            Name = name,
            Backend = backend,
            BackendPort = backendPort,
            FrontendPort = frontendPort,
            Manufacturer = manufacturer,
            Model = model,
            CameraStreamUrl = cameraStreamUrl,
            CameraSnapshotUrl = cameraSnapshotUrl,
            DiscoveredAt = DateTime.UtcNow,
            IsReachable = true
        };
}
