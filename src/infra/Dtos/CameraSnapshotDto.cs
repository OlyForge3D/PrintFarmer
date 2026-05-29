namespace Farm.Infrastructure.Dtos;

/// <summary>
/// DTO for camera snapshot metadata (excludes file path for security).
/// </summary>
public class CameraSnapshotDto
{
    public Guid Id { get; set; }

    public Guid PrinterId { get; set; }

    public Guid CameraId { get; set; }

    public Guid? PrintJobId { get; set; }

    public string EventType { get; set; } = string.Empty;

    public DateTime CapturedAt { get; set; }

    public long? FileSizeBytes { get; set; }
}
