using System.ComponentModel.DataAnnotations;

namespace Farm.Infrastructure.Domain;

/// <summary>
/// Represents a standalone webcam that can be displayed in the Camera View.
/// Unlike printer cameras, these are not attached to any 3D printer.
/// </summary>
public class Camera
{
    public Guid Id { get; set; }

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
}
