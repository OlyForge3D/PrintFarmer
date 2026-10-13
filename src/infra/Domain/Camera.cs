using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

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
    /// Links to the printers this camera is associated with (many-to-many via <see cref="CameraPrinter"/>).
    /// Empty for standalone cameras.
    /// </summary>
    [JsonIgnore]
    public ICollection<CameraPrinter> PrinterLinks { get; set; } = new List<CameraPrinter>();

    /// <summary>
    /// Skip navigation to the printers linked through <see cref="PrinterLinks"/>.
    /// </summary>
    [JsonIgnore]
    public ICollection<Printer> Printers { get; set; } = new List<Printer>();

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
    [JsonIgnore]
    public string? StreamUrl { get; set; }

    /// <summary>
    /// URL for snapshot images
    /// </summary>
    [MaxLength(500)]
    [JsonIgnore]
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
    /// Optional explicit location assignment. Used as the effective location unless the camera
    /// is linked to exactly one printer, in which case that printer's location wins
    /// (see <see cref="CameraLinkExtensions.GetEffectiveLocationId(Camera)"/>).
    /// </summary>
    public Guid? LocationId { get; set; }

    /// <summary>
    /// Navigation to the explicitly assigned <see cref="Domain.Location"/> (named to avoid clashing with
    /// the legacy free-text <see cref="Location"/> column).
    /// </summary>
    [JsonIgnore]
    public Location? AssignedLocation { get; set; }

    /// <summary>
    /// True once a user has edited this camera; automatic sync must not overwrite user-modified rows.
    /// </summary>
    public bool UserModified { get; set; }

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
    [JsonIgnore]
    public string? HealthMessage { get; set; }

    /// <summary>
    /// Number of consecutive health check failures
    /// </summary>
    public int ConsecutiveFailures { get; set; } = 0;
}
