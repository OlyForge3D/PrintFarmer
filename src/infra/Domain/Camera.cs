using System.ComponentModel.DataAnnotations;

namespace Farm.Infrastructure.Domain;

/// <summary>
/// Represents a camera that can be displayed in the Camera View.
/// Can be either standalone or attached to a 3D printer.
/// Printer-attached cameras are discovered from Moonraker, PrusaLink, OctoPrint, etc.
/// </summary>
public class Camera
{
    public Guid Id { get; set; }

    /// <summary>
    /// Optional foreign key to printer if this camera is attached to a printer.
    /// Null for standalone cameras.
    /// </summary>
    public Guid? PrinterId { get; set; }

    /// <summary>
    /// Navigation property to the printer this camera is attached to (if any).
    /// </summary>
    public Printer? Printer { get; set; }

    /// <summary>
    /// Display name for the camera
    /// </summary>
    [Required]
    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Optional description
    /// </summary>
    [MaxLength(500)]
    public string? Description { get; set; }

    /// <summary>
    /// URL for the camera stream (MJPEG or similar)
    /// </summary>
    [MaxLength(500)]
    public string? StreamUrl { get; set; }

    /// <summary>
    /// URL for snapshot images
    /// </summary>
    [MaxLength(500)]
    public string? SnapshotUrl { get; set; }

    /// <summary>
    /// Whether this camera should be shown in the Camera View
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Display order in the camera grid (lower = first)
    /// </summary>
    public int SortOrder { get; set; } = 0;

    /// <summary>
    /// Optional location or group identifier
    /// </summary>
    [MaxLength(100)]
    public string? Location { get; set; }

    /// <summary>
    /// When the camera was added
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Last modification time
    /// </summary>
    public DateTime? UpdatedAt { get; set; }

    /// <summary>
    /// Source that discovered or created this camera
    /// </summary>
    public CameraSource Source { get; set; } = CameraSource.Standalone;

    /// <summary>
    /// Purpose/position classification of the camera
    /// </summary>
    public CameraType CameraType { get; set; } = CameraType.General;

    /// <summary>
    /// Health status from periodic connectivity probes
    /// </summary>
    public CameraHealthStatus HealthStatus { get; set; } = CameraHealthStatus.Unknown;

    /// <summary>
    /// Timestamp of the last health check
    /// </summary>
    public DateTime? LastHealthCheck { get; set; }

    /// <summary>
    /// Optional message from the last health check (error details, etc.)
    /// </summary>
    [MaxLength(500)]
    public string? HealthMessage { get; set; }

    /// <summary>
    /// Number of consecutive health check failures
    /// </summary>
    public int ConsecutiveFailures { get; set; } = 0;
}
