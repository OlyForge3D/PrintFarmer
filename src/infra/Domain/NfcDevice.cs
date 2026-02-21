using System.ComponentModel.DataAnnotations;

namespace Farm.Infrastructure.Domain;

/// <summary>
/// Represents an NFC reader/writer device (e.g., ESP32 + PN532) registered with PrintFarmer.
/// Used for tracking filament spools via NFC tags on 3D printers.
/// </summary>
public class NfcDevice
{
    public Guid Id { get; set; }

    /// <summary>
    /// Display name for the device (e.g., "Prusa MK4 NFC Reader")
    /// </summary>
    [Required]
    [MaxLength(128)]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// IP address of the device on the local network
    /// </summary>
    [MaxLength(45)]
    public string? IpAddress { get; set; }

    /// <summary>
    /// Associated printer ID — one NFC reader per printer
    /// </summary>
    public Guid? PrinterId { get; set; }

    /// <summary>
    /// Navigation property to the associated printer
    /// </summary>
    public Printer? Printer { get; set; }

    /// <summary>
    /// Firmware version reported by the device
    /// </summary>
    [MaxLength(32)]
    public string? FirmwareVersion { get; set; }

    /// <summary>
    /// WiFi signal strength (RSSI) from last heartbeat
    /// </summary>
    public int? WifiRssi { get; set; }

    /// <summary>
    /// Whether the NFC reader hardware is functioning
    /// </summary>
    public bool NfcReaderOk { get; set; } = true;

    /// <summary>
    /// Free heap memory (bytes) from last heartbeat
    /// </summary>
    public int? FreeHeap { get; set; }

    /// <summary>
    /// Last time a heartbeat was received
    /// </summary>
    public DateTime? LastHeartbeat { get; set; }

    /// <summary>
    /// Last time an NFC tag was scanned
    /// </summary>
    public DateTime? LastScanAt { get; set; }

    /// <summary>
    /// Spool ID from the most recent scan
    /// </summary>
    public int? LastScannedSpoolId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? UpdatedAt { get; set; }

    /// <summary>
    /// Scan history entries for this device
    /// </summary>
    public ICollection<NfcScanEvent> ScanHistory { get; set; } = [];
}
