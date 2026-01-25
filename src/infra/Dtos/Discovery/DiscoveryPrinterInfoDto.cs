using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Discovery;

/// <summary>
/// Base printer information captured during network discovery.
/// This DTO is the foundation for all discovery-related data transfer.
/// Contains raw network/probe data that may include IpAddress (unlike registered Printer entity).
/// </summary>
public class DiscoveryPrinterInfoDto
{
    /// <summary>Display name for the printer (from hostname or probe response)</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Normalized server URL (e.g., http://hostname:7125)</summary>
    public string ServerUrl { get; set; } = string.Empty;

    /// <summary>Original user-supplied URL before normalization (if different)</summary>
    public string? OriginalServerUrl { get; set; }

    /// <summary>IP address of the printer on the network (discovery-specific, not stored on Printer entity)</summary>
    public string IpAddress { get; set; } = string.Empty;

    /// <summary>Backend type (moonraker, prusalink, octoprint, sdcp)</summary>
    public PrinterBackend Backend { get; set; }

    /// <summary>Backend-specific port number (default varies by backend)</summary>
    public int? BackendPort { get; set; }

    /// <summary>Frontend web UI port (if different from backend port)</summary>
    public int? FrontendPort { get; set; }

    /// <summary>Camera stream URL discovered from printer API (optional)</summary>
    public string? CameraStreamUrl { get; set; }

    /// <summary>Camera snapshot URL discovered from printer API (optional)</summary>
    public string? CameraSnapshotUrl { get; set; }

    /// <summary>Printer manufacturer name (from discovery or catalog match)</summary>
    public string? Manufacturer { get; set; }

    /// <summary>Printer model name (from discovery or catalog match)</summary>
    public string? Model { get; set; }

    /// <summary>User notes or description</summary>
    public string? Notes { get; set; }

    /// <summary>API key for backend authentication (if required)</summary>
    public string? ApiKey { get; set; }

    /// <summary>Timestamp when printer was discovered</summary>
    public DateTime DiscoveredAt { get; set; } = DateTime.UtcNow;

    /// <summary>Whether the printer is currently reachable</summary>
    public bool IsReachable { get; set; }
}
