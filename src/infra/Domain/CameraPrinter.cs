using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Domain;

/// <summary>
/// Join entity linking a <see cref="Camera"/> to a <see cref="Printer"/>.
/// A camera may be linked to several printers (e.g. a room camera covering a shelf) and a printer may have several cameras.
/// </summary>
public class CameraPrinter
{
    /// <summary>
    /// The linked camera.
    /// </summary>
    public Guid CameraId { get; set; }

    /// <summary>
    /// Navigation to the linked camera.
    /// </summary>
    [JsonIgnore]
    public Camera Camera { get; set; } = null!;

    /// <summary>
    /// The linked printer.
    /// </summary>
    public Guid PrinterId { get; set; }

    /// <summary>
    /// Navigation to the linked printer.
    /// </summary>
    [JsonIgnore]
    public Printer Printer { get; set; } = null!;

    /// <summary>
    /// True when this camera is the printer's primary camera (preferred for previews, snapshots and failure detection).
    /// </summary>
    public bool IsPrimary { get; set; }

    /// <summary>
    /// Display order of this camera among the printer's cameras (lower = first).
    /// </summary>
    public int SortOrder { get; set; }
}
