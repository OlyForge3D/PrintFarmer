using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Services.Cameras;

/// <summary>
/// Derives client-facing camera contract fields from stored URLs without requiring schema changes.
/// </summary>
public static class CameraContractClassifier
{
    public const string SnapmakerU1MonitorSnapshotPath = "/server/files/camera/monitor.jpg";

    public static CameraSnapshotStrategy GetSnapshotStrategy(string? snapshotUrl)
    {
        if (string.IsNullOrWhiteSpace(snapshotUrl))
        {
            return CameraSnapshotStrategy.None;
        }

        return IsSnapmakerU1MonitorSnapshotUrl(snapshotUrl)
            ? CameraSnapshotStrategy.SnapmakerU1MonitorJpeg
            : CameraSnapshotStrategy.DirectUrl;
    }

    public static CameraStreamFormat GetStreamFormat(string? streamUrl)
    {
        if (string.IsNullOrWhiteSpace(streamUrl))
        {
            return CameraStreamFormat.Unknown;
        }

        if (!Uri.TryCreate(streamUrl, UriKind.Absolute, out Uri? uri))
        {
            return CameraStreamFormat.Unsupported;
        }

        if (uri.Scheme.Equals("rtsp", StringComparison.OrdinalIgnoreCase))
        {
            return CameraStreamFormat.Rtsp;
        }

        if (!uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return CameraStreamFormat.Unsupported;
        }

        return uri.AbsolutePath.Contains("webrtc", StringComparison.OrdinalIgnoreCase) ||
               uri.Query.Contains("webrtc", StringComparison.OrdinalIgnoreCase)
            ? CameraStreamFormat.WebRtc
            : CameraStreamFormat.Mjpeg;
    }

    public static CameraAccessMode GetAccessMode(string? streamUrl, string? snapshotUrl)
    {
        CameraStreamFormat streamFormat = GetStreamFormat(streamUrl);
        bool hasSnapshot = !string.IsNullOrWhiteSpace(snapshotUrl);

        return streamFormat switch
        {
            CameraStreamFormat.Mjpeg when hasSnapshot => CameraAccessMode.StreamAndSnapshot,
            CameraStreamFormat.Mjpeg => CameraAccessMode.StreamOnly,
            CameraStreamFormat.WebRtc or CameraStreamFormat.Rtsp when hasSnapshot => CameraAccessMode.StreamAndSnapshot,
            CameraStreamFormat.WebRtc or CameraStreamFormat.Rtsp => CameraAccessMode.StreamOnly,
            CameraStreamFormat.Unsupported when hasSnapshot => CameraAccessMode.SnapshotOnly,
            CameraStreamFormat.Unsupported => CameraAccessMode.UnsupportedStream,
            _ when hasSnapshot => CameraAccessMode.SnapshotOnly,
            _ => CameraAccessMode.Unknown,
        };
    }

    public static bool IsSnapmakerU1MonitorSnapshotUrl(string snapshotUrl)
    {
        return Uri.TryCreate(snapshotUrl, UriKind.Absolute, out Uri? uri) &&
               uri.AbsolutePath.Equals(SnapmakerU1MonitorSnapshotPath, StringComparison.OrdinalIgnoreCase);
    }
}
