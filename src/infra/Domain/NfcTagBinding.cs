using System.ComponentModel.DataAnnotations;

namespace Farm.Infrastructure.Domain;

/// <summary>
/// Represents a persistent binding between an NFC tag UID and a spool + printer/tray context.
/// Created via POST /api/nfc/link when the user pairs a tag for the first time.
/// </summary>
public class NfcTagBinding
{
    public Guid Id { get; set; }

    /// <summary>
    /// Hardware UID of the NFC tag (hex string, e.g. "A1:B2:C3:D4").
    /// </summary>
    [Required]
    [MaxLength(64)]
    public string TagUid { get; set; } = string.Empty;

    /// <summary>
    /// Spoolman spool ID this tag is bound to.
    /// </summary>
    public int? SpoolId { get; set; }

    /// <summary>
    /// Human-readable spool name stored at bind time.
    /// </summary>
    [MaxLength(256)]
    public string? SpoolName { get; set; }

    /// <summary>
    /// Printer this tag is associated with.
    /// </summary>
    public Guid? PrinterId { get; set; }

    public Printer? Printer { get; set; }

    /// <summary>
    /// Tray or AMS slot identifier (optional).
    /// </summary>
    [MaxLength(64)]
    public string? TrayId { get; set; }

    /// <summary>
    /// Last time this tag was scanned and matched against this binding.
    /// </summary>
    public DateTime? SpoolLastSeenAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? UpdatedAt { get; set; }
}
