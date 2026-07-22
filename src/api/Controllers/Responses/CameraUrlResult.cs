using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Cameras;

namespace Farm.Web.Api.Controllers.Responses;

// URL-like values are represented as strings by design for transport compatibility.
#pragma warning disable CA1056 // URI-like properties should not be strings
public sealed record CameraUrlResult(
    string? StreamUrl,
    string? SnapshotUrl,
    CameraAccessMode AccessMode = CameraAccessMode.Unknown,
    CameraStreamFormat StreamFormat = CameraStreamFormat.Unknown,
    CameraSnapshotStrategy SnapshotStrategy = CameraSnapshotStrategy.None)
{
    public static CameraUrlResult FromUrls(string? streamUrl, string? snapshotUrl)
    {
        return new CameraUrlResult(
            streamUrl,
            snapshotUrl,
            CameraContractClassifier.GetAccessMode(streamUrl, snapshotUrl),
            CameraContractClassifier.GetStreamFormat(streamUrl),
            CameraContractClassifier.GetSnapshotStrategy(snapshotUrl));
    }

    public void Deconstruct(out string? streamUrl, out string? snapshotUrl)
    {
        streamUrl = StreamUrl;
        snapshotUrl = SnapshotUrl;
    }
}
#pragma warning restore CA1056
