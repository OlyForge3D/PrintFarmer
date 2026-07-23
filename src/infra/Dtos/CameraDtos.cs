using System.ComponentModel.DataAnnotations;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Cameras;

namespace Farm.Infrastructure;

/// <summary>
/// URL validation attribute that allows empty or null values.
/// Unlike [Url], this won't reject empty strings.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter, AllowMultiple = false)]
public sealed class OptionalUrlAttribute : ValidationAttribute
{
    public OptionalUrlAttribute()
        : base("The {0} field is not a valid URL.")
    {
    }

    public override bool IsValid(object? value)
    {
        if (value == null)
        {
            return true;
        }

        var str = value as string;
        if (string.IsNullOrWhiteSpace(str))
        {
            return true;
        }

        return Uri.TryCreate(str, UriKind.Absolute, out var uri)
               && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }
}

/// <summary>
/// Camera DTO for reading and listing cameras (standalone and printer-attached).
/// Contains all camera properties for display and management.
/// </summary>
public class CameraDto
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string? StreamUrl { get; set; }

    public string? SnapshotUrl { get; set; }

    /// <summary>
    /// Client presentation mode. Stock Snapmaker U1 is SnapshotOnly because it exposes monitor.jpg, not MJPEG.
    /// </summary>
    public CameraAccessMode AccessMode => CameraContractClassifier.GetAccessMode(StreamUrl, SnapshotUrl);

    /// <summary>
    /// Live stream transport. WebRTC/RTSP are exposed so clients can avoid treating them as MJPEG.
    /// </summary>
    public CameraStreamFormat StreamFormat => CameraContractClassifier.GetStreamFormat(StreamUrl);

    /// <summary>
    /// Snapshot capture strategy. SnapmakerU1MonitorJpeg means the API wakes the monitor over Moonraker websocket.
    /// </summary>
    public CameraSnapshotStrategy SnapshotStrategy => CameraContractClassifier.GetSnapshotStrategy(SnapshotUrl);

    public bool IsEnabled { get; set; } = true;

    public int SortOrder { get; set; }

    public string? Location { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }

    /// <summary>
    /// If this camera is attached to a printer, this is the printer's ID.
    /// Null for standalone cameras.
    /// </summary>
    public Guid? PrinterId { get; set; }

    /// <summary>
    /// If this camera is attached to a printer, this is the printer's name.
    /// Null for standalone cameras.
    /// </summary>
    public string? PrinterName { get; set; }

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
    /// Indicates this is a standalone camera (not attached to a printer).
    /// Used to differentiate from printer-attached cameras in the UI.
    /// </summary>
    public bool IsStandalone { get; set; } = true;
}

/// <summary>
/// DTO for creating a new camera (standalone or printer-attached).
/// </summary>
public class CreateCameraDto
{
    [Required(ErrorMessage = "Camera name is required.")]
    [StringLength(256, MinimumLength = 1, ErrorMessage = "Camera name must be between 1 and 256 characters.")]
    public string Name { get; set; } = string.Empty;

    [StringLength(1024, ErrorMessage = "Camera description cannot exceed 1024 characters.")]
    public string? Description { get; set; }

    [OptionalUrl(ErrorMessage = "Stream URL must be a valid URL.")]
    [StringLength(2048, ErrorMessage = "Stream URL cannot exceed 2048 characters.")]
    public string? StreamUrl { get; set; }

    [OptionalUrl(ErrorMessage = "Snapshot URL must be a valid URL.")]
    [StringLength(2048, ErrorMessage = "Snapshot URL cannot exceed 2048 characters.")]
    public string? SnapshotUrl { get; set; }

    public CameraAccessMode AccessMode => CameraContractClassifier.GetAccessMode(StreamUrl, SnapshotUrl);

    public CameraStreamFormat StreamFormat => CameraContractClassifier.GetStreamFormat(StreamUrl);

    public CameraSnapshotStrategy SnapshotStrategy => CameraContractClassifier.GetSnapshotStrategy(SnapshotUrl);

    public bool IsEnabled { get; set; } = true;

    public int SortOrder { get; set; }

    [StringLength(256, ErrorMessage = "Location cannot exceed 256 characters.")]
    public string? Location { get; set; }

    /// <summary>
    /// Optional printer ID if this camera is attached to a printer.
    /// Null for standalone cameras.
    /// </summary>
    public Guid? PrinterId { get; set; }

    /// <summary>
    /// Source that discovered or created this camera.
    /// If not specified, defaults to Standalone.
    /// </summary>
    public CameraSource? Source { get; set; }

    /// <summary>
    /// Purpose/position classification of the camera.
    /// If not specified, defaults to General.
    /// </summary>
    public CameraType? CameraType { get; set; }
}

/// <summary>
/// DTO for updating an existing camera.
/// </summary>
public class UpdateCameraDto
{
    [StringLength(256, MinimumLength = 1, ErrorMessage = "Camera name must be between 1 and 256 characters.")]
    public string? Name { get; set; }

    [StringLength(1024, ErrorMessage = "Camera description cannot exceed 1024 characters.")]
    public string? Description { get; set; }

    [OptionalUrl(ErrorMessage = "Stream URL must be a valid URL.")]
    [StringLength(2048, ErrorMessage = "Stream URL cannot exceed 2048 characters.")]
    public string? StreamUrl { get; set; }

    [OptionalUrl(ErrorMessage = "Snapshot URL must be a valid URL.")]
    [StringLength(2048, ErrorMessage = "Snapshot URL cannot exceed 2048 characters.")]
    public string? SnapshotUrl { get; set; }

    public bool? IsEnabled { get; set; }

    public int? SortOrder { get; set; }

    [StringLength(256, ErrorMessage = "Location cannot exceed 256 characters.")]
    public string? Location { get; set; }

    /// <summary>
    /// Optional printer ID if this camera should be attached to or moved to a different printer.
    /// Omit or leave null to keep the current association unchanged.
    /// </summary>
    public Guid? PrinterId { get; set; }

    /// <summary>
    /// Source that discovered or created this camera
    /// </summary>
    public CameraSource? Source { get; set; }

    /// <summary>
    /// Purpose/position classification of the camera
    /// </summary>
    public CameraType? CameraType { get; set; }
}

/// <summary>
/// DTO for toggling camera visibility in the Camera View.
/// Used when user enables/disables a camera without full update.
/// </summary>
public class ToggleCameraDto
{
    [Required]
    public bool IsEnabled { get; set; }
}

/// <summary>
/// Request DTO for detecting camera endpoints for a configured printer.
/// </summary>
public class DetectCameraEndpointsRequest
{
    [Required]
    public Guid PrinterId { get; set; }
}

/// <summary>
/// Response DTO for detected camera endpoints.
/// </summary>
public class CameraEndpointDetectionDto
{
    public string? StreamUrl { get; set; }

    public string? SnapshotUrl { get; set; }

    public bool Detected { get; set; }

    public string Source { get; set; } = string.Empty;
}

/// <summary>
/// Combined camera DTO that includes both standalone and printer-attached cameras.
/// Used for the Camera View page to display all available camera feeds.
/// </summary>
public class DisplayCameraDto
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string? StreamUrl { get; set; }

    public string? SnapshotUrl { get; set; }

    public bool IsEnabled { get; set; } = true;

    public int SortOrder { get; set; }

    public string? Location { get; set; }

    /// <summary>
    /// If this camera is attached to a printer, this is the printer's ID.
    /// Null for standalone cameras.
    /// </summary>
    public Guid? PrinterId { get; set; }

    /// <summary>
    /// If this camera is attached to a printer, this is the printer's name.
    /// Null for standalone cameras.
    /// </summary>
    public string? PrinterName { get; set; }

    /// <summary>
    /// True if this is a standalone camera, false if attached to a printer.
    /// </summary>
    public bool IsStandalone { get; set; }

    /// <summary>
    /// The source of this camera (e.g., Standalone, Moonraker, PrusaLink).
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
}
