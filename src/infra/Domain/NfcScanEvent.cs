using System.ComponentModel.DataAnnotations;

namespace Farm.Infrastructure.Domain;

/// <summary>
/// Records a single NFC tag scan event from an NFC reader device.
/// </summary>
public class NfcScanEvent
{
    public Guid Id { get; set; }

    /// <summary>
    /// The NFC device that performed the scan
    /// </summary>
    public Guid NfcDeviceId { get; set; }

    public NfcDevice NfcDevice { get; set; } = null!;

    /// <summary>
    /// Spoolman spool ID read from the tag
    /// </summary>
    public int? SpoolId { get; set; }

    /// <summary>
    /// NFC tag format detected (openspool, openprinttag, raw)
    /// </summary>
    [MaxLength(32)]
    public string TagFormat { get; set; } = "openspool";

    /// <summary>
    /// Material type read from the tag (e.g., PLA, PETG)
    /// </summary>
    [MaxLength(64)]
    public string? MaterialType { get; set; }

    /// <summary>
    /// Brand name read from the tag
    /// </summary>
    [MaxLength(128)]
    public string? BrandName { get; set; }

    /// <summary>
    /// Action taken as a result of the scan
    /// </summary>
    [MaxLength(64)]
    public string? Action { get; set; }

    public DateTime ScannedAt { get; set; } = DateTime.UtcNow;
}
