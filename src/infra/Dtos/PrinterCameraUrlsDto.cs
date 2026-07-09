using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using Farm.Infrastructure.Annotations;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Cameras;

namespace Farm.Infrastructure;

// Camera URLs for all printers (static configuration without external API calls)
/// <summary>
/// Lightweight camera URL information for printers without external API overhead.
/// </summary>
public record PrinterCameraUrlsDto(
    Guid Id,
    string Name,
    string? CameraStreamUrl = null,
    string? CameraSnapshotUrl = null,
    CameraAccessMode CameraAccessMode = CameraAccessMode.Unknown,
    CameraStreamFormat CameraStreamFormat = CameraStreamFormat.Unknown,
    CameraSnapshotStrategy CameraSnapshotStrategy = CameraSnapshotStrategy.None)
{
    public static PrinterCameraUrlsDto FromUrls(Guid id, string name, string? streamUrl, string? snapshotUrl)
    {
        return new PrinterCameraUrlsDto(
            id,
            name,
            streamUrl,
            snapshotUrl,
            CameraContractClassifier.GetAccessMode(streamUrl, snapshotUrl),
            CameraContractClassifier.GetStreamFormat(streamUrl),
            CameraContractClassifier.GetSnapshotStrategy(snapshotUrl));
    }
}
