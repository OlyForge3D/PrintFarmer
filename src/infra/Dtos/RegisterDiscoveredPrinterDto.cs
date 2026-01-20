using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure;

/// <summary>
/// DEPRECATED: Use PrinterInfoDto directly instead.
/// This bridge DTO existed to pass data from discovery service to API registration.
/// Data loss in this layer has been eliminated by consolidating to PrinterInfoDto.
/// </summary>
public class RegisterDiscoveredPrinterDto
{
    /// <summary>Hostname or local name of the printer</summary>
    public string Hostname { get; set; } = string.Empty;

    /// <summary>IP address of the printer</summary>
    public string IpAddress { get; set; } = string.Empty;

    /// <summary>Port number where printer is accessible</summary>
    public int Port { get; set; } = 80;

    /// <summary>Backend type (moonraker, prusalink, octoprint, sdcp)</summary>
    public string PrinterBackend { get; set; } = string.Empty;

    /// <summary>Friendly display name for the printer</summary>
    public string? FriendlyName { get; set; }

    /// <summary>Timestamp when printer was discovered</summary>
    public DateTime DiscoveredAt { get; set; } = DateTime.UtcNow;

    /// <summary>Convert to PrinterInfoDto (preferred modern format)</summary>
    public PrinterInfoDto ToPrinterInfoDto() =>
        new()
        {
            Name = FriendlyName ?? Hostname,
            IpAddress = IpAddress,
            ServerUrl = $"http://{IpAddress}:{Port}",
            OriginalServerUrl = null,
            Backend = Enum.TryParse(PrinterBackend, ignoreCase: true, out PrinterBackend b) ? b : Farm.Infrastructure.Domain.PrinterBackend.Moonraker,
            BackendPort = Port,
            FrontendPort = null,
            DiscoveredAt = DiscoveredAt,
            IsReachable = true
        };
}
