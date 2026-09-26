using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Cameras;
using Farm.Infrastructure.Services.Printers;

namespace Farm.Backend.Plugin.Moonraker;

public partial class MoonrakerClient : ISupportsPrinterCameraProfile
{
    /// <inheritdoc />
    public (string? StreamUrl, string? SnapshotUrl) GetDefaultCameraUrls(Printer printer)
    {
        ArgumentNullException.ThrowIfNull(printer);
        if (!IsSnapmakerU1(printer))
        {
            return (null, null);
        }

        var uri = new UriBuilder(printer.BackendUrl)
        {
            Path = "server/files/camera/monitor.jpg",
            Query = string.Empty,
        };
        return (null, uri.Uri.ToString());
    }

    /// <inheritdoc />
    public bool ShouldTriggerSnapshot(Printer printer, string? snapshotUrl)
    {
        ArgumentNullException.ThrowIfNull(printer);
        return IsSnapmakerU1(printer) &&
            (string.IsNullOrWhiteSpace(snapshotUrl) || CameraContractClassifier.IsSnapmakerU1MonitorSnapshotUrl(snapshotUrl));
    }

    private static bool IsSnapmakerU1(Printer printer) =>
        printer.Manufacturer?.Name.Equals("Snapmaker", StringComparison.OrdinalIgnoreCase) == true &&
        printer.Model?.Name.Equals("Snapmaker U1", StringComparison.OrdinalIgnoreCase) == true;

    private static string GetCameraFrontendUrl(string backendUrl, int? frontendPort)
    {
        var uri = new UriBuilder(backendUrl);
        uri.Port = frontendPort ?? (uri.Scheme == Uri.UriSchemeHttps ? 443 : 80);
        return uri.Uri.ToString().TrimEnd('/');
    }
}
