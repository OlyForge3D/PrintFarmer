namespace Farm.Infrastructure.Domain;

/// <summary>Source that discovered or created this camera.</summary>
public enum CameraSource
{
    Standalone,
    Moonraker,
    PrusaLink,
    OctoPrint,
    SDCP,
    FlashForge
}

/// <summary>Purpose/position classification of the camera.</summary>
public enum CameraType
{
    General,
    Bed,
    Nozzle,
    Wide,
    Timelapse
}

/// <summary>Health status from periodic connectivity probes.</summary>
public enum CameraHealthStatus
{
    Unknown,
    Healthy,
    Degraded,
    Unhealthy
}

/// <summary>How clients should present or poll a camera endpoint.</summary>
public enum CameraAccessMode
{
    Unknown,
    StreamAndSnapshot,
    SnapshotOnly,
    StreamOnly,
    UnsupportedStream
}

/// <summary>Transport format for the live stream URL, if one is available.</summary>
public enum CameraStreamFormat
{
    Unknown,
    Mjpeg,
    WebRtc,
    Rtsp,
    Unsupported
}

/// <summary>Snapshot capture strategy required by the printer/backend.</summary>
public enum CameraSnapshotStrategy
{
    None,
    DirectUrl,

    /// <summary>
    /// Snapmaker U1 stock firmware requires Moonraker websocket JSON-RPC
    /// camera.start_monitor before GET /server/files/camera/monitor.jpg.
    /// </summary>
    SnapmakerU1MonitorJpeg
}
