using System.ComponentModel.DataAnnotations;

namespace Farm.Infrastructure.Domain;

/// <summary>
/// Represents a snapshot captured from a camera during a print event.
/// Snapshots are stored on the filesystem and tracked in the database
/// for association with print job history.
/// </summary>
public class CameraSnapshot
{
    public Guid Id { get; set; }

    /// <summary>
    /// The printer that was printing when the snapshot was taken.
    /// </summary>
    public Guid PrinterId { get; set; }

    /// <summary>
    /// Navigation property to the printer.
    /// </summary>
    public Printer? Printer { get; set; }

    /// <summary>
    /// The camera that captured the snapshot.
    /// </summary>
    public Guid CameraId { get; set; }

    /// <summary>
    /// Navigation property to the camera.
    /// </summary>
    public Camera? Camera { get; set; }

    /// <summary>
    /// The print job that was active when the snapshot was taken (if any).
    /// </summary>
    public Guid? PrintJobId { get; set; }

    /// <summary>
    /// Navigation property to the print job.
    /// </summary>
    public PrintJob? PrintJob { get; set; }

    /// <summary>
    /// The event type that triggered the snapshot (e.g., PrintStarted, PrintCompleted, PrintFailed).
    /// </summary>
    [Required]
    [MaxLength(50)]
    public string EventType { get; set; } = string.Empty;

    /// <summary>
    /// Relative file path from the snapshot storage root.
    /// </summary>
    [Required]
    [MaxLength(500)]
    public string FilePath { get; set; } = string.Empty;

    /// <summary>
    /// When the snapshot was captured (UTC).
    /// </summary>
    public DateTime CapturedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// File size in bytes (for display/management).
    /// </summary>
    public long? FileSizeBytes { get; set; }
}
