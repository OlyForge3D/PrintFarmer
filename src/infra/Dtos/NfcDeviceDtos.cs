using System.ComponentModel.DataAnnotations;

namespace Farm.Infrastructure;

/// <summary>
/// DTO for reading NFC device data.
/// </summary>
public class NfcDeviceDto
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? IpAddress { get; set; }

    public Guid? PrinterId { get; set; }

    public string? PrinterName { get; set; }

    public string? FirmwareVersion { get; set; }

    public int? WifiRssi { get; set; }

    public bool NfcReaderOk { get; set; }

    public int? FreeHeap { get; set; }

    public bool IsOnline { get; set; }

    public DateTime? LastHeartbeat { get; set; }

    public DateTime? LastScanAt { get; set; }

    public int? LastScannedSpoolId { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }
}

/// <summary>
/// DTO for registering a new NFC device.
/// </summary>
public class CreateNfcDeviceDto
{
    [Required(ErrorMessage = "Device name is required.")]
    [StringLength(128, MinimumLength = 1)]
    public string Name { get; set; } = string.Empty;

    [StringLength(45)]
    public string? IpAddress { get; set; }

    public Guid? PrinterId { get; set; }

    [StringLength(32)]
    public string? FirmwareVersion { get; set; }
}

/// <summary>
/// DTO for updating an NFC device.
/// </summary>
public class UpdateNfcDeviceDto
{
    [StringLength(128, MinimumLength = 1)]
    public string? Name { get; set; }

    public Guid? PrinterId { get; set; }
}

/// <summary>
/// DTO for device heartbeat — sent periodically by the ESP32 firmware.
/// </summary>
public class NfcDeviceHeartbeatDto
{
    /// <summary>
    /// Printer ID used to identify the device
    /// </summary>
    [Required]
    public string PrinterId { get; set; } = string.Empty;

    public int? WifiRssi { get; set; }

    public bool NfcReaderOk { get; set; } = true;

    public string? Ip { get; set; }

    public string? FirmwareVersion { get; set; }

    public int? FreeHeap { get; set; }
}

/// <summary>
/// DTO for scan event — sent when an NFC tag is scanned.
/// </summary>
public class NfcScanEventDto
{
    [Required]
    public string PrinterId { get; set; } = string.Empty;

    public int? SpoolId { get; set; }

    public string TagFormat { get; set; } = "nfc";

    public string? MaterialType { get; set; }

    public string? BrandName { get; set; }
}

/// <summary>
/// DTO for reading scan history entries.
/// </summary>
public class NfcScanHistoryDto
{
    public Guid Id { get; set; }

    public Guid NfcDeviceId { get; set; }

    public string? DeviceName { get; set; }

    public int? SpoolId { get; set; }

    public string TagFormat { get; set; } = string.Empty;

    public string? MaterialType { get; set; }

    public string? BrandName { get; set; }

    public string? Action { get; set; }

    public DateTime ScannedAt { get; set; }
}
