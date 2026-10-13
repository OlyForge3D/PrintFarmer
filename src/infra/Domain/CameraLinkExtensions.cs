namespace Farm.Infrastructure.Domain;

/// <summary>
/// Helpers for reading a <see cref="Camera"/>'s printer links and effective location.
/// All members operate on already-loaded navigations; callers must include
/// <see cref="Camera.PrinterLinks"/> (and <see cref="CameraPrinter.Printer"/> / <see cref="Printer.Location"/> where needed).
/// </summary>
public static class CameraLinkExtensions
{
    /// <summary>
    /// Returns the camera's printer links in a deterministic order: primary links first, then by sort order, then by printer id.
    /// </summary>
    /// <param name="camera">The camera.</param>
    public static IEnumerable<CameraPrinter> OrderedPrinterLinks(this Camera camera)
    {
        ArgumentNullException.ThrowIfNull(camera);
        return camera.PrinterLinks
            .OrderByDescending(l => l.IsPrimary)
            .ThenBy(l => l.SortOrder)
            .ThenBy(l => l.PrinterId);
    }

    /// <summary>
    /// Returns the representative printer link (used where a single printer id is still required), or null for standalone cameras.
    /// </summary>
    /// <param name="camera">The camera.</param>
    public static CameraPrinter? GetPrimaryPrinterLink(this Camera camera) => camera.OrderedPrinterLinks().FirstOrDefault();

    /// <summary>
    /// Returns the representative printer id, or null when the camera is not linked to any printer.
    /// </summary>
    /// <param name="camera">The camera.</param>
    public static Guid? GetPrimaryPrinterId(this Camera camera) => camera.GetPrimaryPrinterLink()?.PrinterId;

    /// <summary>
    /// Returns the representative printer entity (requires <see cref="CameraPrinter.Printer"/> to be loaded).
    /// </summary>
    /// <param name="camera">The camera.</param>
    public static Printer? GetPrimaryPrinter(this Camera camera) => camera.GetPrimaryPrinterLink()?.Printer;

    /// <summary>
    /// Adds a link from the camera to a printer if one does not already exist and returns the (new or existing) link.
    /// </summary>
    /// <param name="camera">The camera.</param>
    /// <param name="printerId">The printer to link.</param>
    /// <param name="isPrimary">Whether the camera should be the printer's primary camera.</param>
    /// <param name="sortOrder">Display order of the camera among the printer's cameras.</param>
    public static CameraPrinter LinkToPrinter(this Camera camera, Guid printerId, bool isPrimary = false, int sortOrder = 0)
    {
        ArgumentNullException.ThrowIfNull(camera);
        CameraPrinter? existing = camera.PrinterLinks.FirstOrDefault(l => l.PrinterId == printerId);
        if (existing is not null)
        {
            return existing;
        }

        CameraPrinter link = new()
        {
            CameraId = camera.Id,
            Camera = camera,
            PrinterId = printerId,
            IsPrimary = isPrimary,
            SortOrder = sortOrder,
        };
        camera.PrinterLinks.Add(link);
        return link;
    }

    /// <summary>
    /// Returns true when the camera is linked to the given printer.
    /// </summary>
    /// <param name="camera">The camera.</param>
    /// <param name="printerId">The printer id.</param>
    public static bool IsLinkedTo(this Camera camera, Guid printerId)
    {
        ArgumentNullException.ThrowIfNull(camera);
        return camera.PrinterLinks.Any(l => l.PrinterId == printerId);
    }

    /// <summary>
    /// Effective location id: when exactly one printer is linked, that printer's location; otherwise <see cref="Camera.LocationId"/>.
    /// </summary>
    /// <param name="camera">The camera (with <see cref="Camera.PrinterLinks"/> and <see cref="CameraPrinter.Printer"/> loaded).</param>
    public static Guid? GetEffectiveLocationId(this Camera camera)
    {
        ArgumentNullException.ThrowIfNull(camera);
        return camera.PrinterLinks.Count == 1
            ? camera.PrinterLinks.First().Printer?.LocationId
            : camera.LocationId;
    }

    /// <summary>
    /// Effective location entity using the same rule as <see cref="GetEffectiveLocationId(Camera)"/>.
    /// Requires <see cref="Printer.Location"/> or <see cref="Camera.AssignedLocation"/> to be loaded.
    /// </summary>
    /// <param name="camera">The camera.</param>
    public static Location? GetEffectiveLocation(this Camera camera)
    {
        ArgumentNullException.ThrowIfNull(camera);
        return camera.PrinterLinks.Count == 1
            ? camera.PrinterLinks.First().Printer?.Location
            : camera.AssignedLocation;
    }

    /// <summary>
    /// True when the location is derived from the single linked printer and therefore not user-settable.
    /// </summary>
    /// <param name="camera">The camera.</param>
    public static bool IsLocationDerivedFromPrinter(this Camera camera)
    {
        ArgumentNullException.ThrowIfNull(camera);
        return camera.PrinterLinks.Count == 1;
    }
}
