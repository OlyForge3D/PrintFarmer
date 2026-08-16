using System.Text.Json.Serialization;
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
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)]
    public string ServerUrl { get; set; } = string.Empty;

    /// <summary>Original user-supplied URL before normalization (if different)</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)]
    public string? OriginalServerUrl { get; set; }

    /// <summary>IP address of the printer on the network (discovery-specific, not stored on Printer entity)</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)]
    public string IpAddress { get; set; } = string.Empty;

    /// <summary>Backend type (moonraker, prusalink, octoprint, sdcp)</summary>
    public PrinterBackend Backend { get; set; }

    /// <summary>Backend-specific port number (default varies by backend)</summary>
    public int? BackendPort { get; set; }

    /// <summary>Frontend web UI port (if different from backend port)</summary>
    public int? FrontendPort { get; set; }

    /// <summary>Camera stream URL discovered from printer API (optional)</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)]
    public string? CameraStreamUrl { get; set; }

    /// <summary>Camera snapshot URL discovered from printer API (optional)</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)]
    public string? CameraSnapshotUrl { get; set; }

    /// <summary>Printer manufacturer name (from discovery or catalog match)</summary>
    public string? Manufacturer { get; set; }

    /// <summary>Printer model name (from discovery or catalog match)</summary>
    public string? Model { get; set; }

    /// <summary>User notes or description</summary>
    public string? Notes { get; set; }

    /// <summary>API key for backend authentication (if required)</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)]
    public string? ApiKey { get; set; }

    /// <summary>
    /// Username for HTTP Digest authentication (primarily for PrusaLink).
    /// Defaults to "maker" for PrusaLink printers if not specified.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)]
    public string? Username { get; set; }

    /// <summary>
    /// Password for HTTP Digest authentication (primarily for PrusaLink).
    /// User must obtain this from the printer's web interface under Settings → Network → Credentials.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)]
    public string? Password { get; set; }

    /// <summary>Timestamp when printer was discovered</summary>
    public DateTime DiscoveredAt { get; set; } = DateTime.UtcNow;

    /// <summary>Whether the printer is currently reachable</summary>
    public bool IsReachable { get; set; }

    /// <summary>
    /// Firmware family detected during the discovery probe (e.g. Klipper). Authoritative for
    /// <see cref="GcodeDialect"/> — independent of, and may legitimately disagree with, a machine
    /// profile's own gcode_flavor assertion (see #1618 / #1613 §4.5.1).
    /// </summary>
    public PrinterFirmwareFamily? FirmwareFamily { get; set; }

    /// <summary>G-code dialect implied by the detected <see cref="FirmwareFamily"/>.</summary>
    public PrinterGcodeDialect? GcodeDialect { get; set; }

    /// <summary>How firmware identity was determined (live probe vs. operator-configured).</summary>
    public FirmwareDetectionSource? FirmwareDetectionSource { get; set; }

    /// <summary>Firmware/software version string extracted from the probe response, when available.</summary>
    public string? FirmwareVersion { get; set; }

    /// <summary>Detector/probe-logic version stamp (e.g. "moonraker-probe-v1"), not the firmware's own version.</summary>
    public string? FirmwareDetectionVersion { get; set; }

    /// <summary>Detection confidence normalized to 0.0-1.0, mapped from the probe's raw match score.</summary>
    public decimal? FirmwareDetectionConfidence { get; set; }

    /// <summary>UTC timestamp when firmware identity was last (re-)detected.</summary>
    public DateTime? FirmwareDetectedAtUtc { get; set; }
}
