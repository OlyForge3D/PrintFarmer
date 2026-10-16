namespace Farm.Infrastructure.Domain;

/// <summary>
/// Records that a user removed an auto-discovered camera from a printer so background camera
/// sync does not re-create it. Keyed by printer and camera <see cref="CameraSource"/>, which is the
/// identity the printer camera synchronizer uses to match a discovered camera to its slot
/// (one discovered camera per source per printer). An explicit camera discovery request for the
/// printer, or manually linking a camera of the same source, clears the dismissal.
/// </summary>
public class DismissedPrinterCamera
{
    /// <summary>Primary key.</summary>
    public Guid Id { get; set; }

    /// <summary>Printer the discovered camera was removed from.</summary>
    public Guid PrinterId { get; set; }

    /// <summary>Plugin source of the dismissed camera (the synchronizer's match key).</summary>
    public CameraSource Source { get; set; }

    /// <summary>Name of the camera when it was dismissed (informational).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>When the camera was dismissed (UTC).</summary>
    public DateTime DismissedAt { get; set; }
}
