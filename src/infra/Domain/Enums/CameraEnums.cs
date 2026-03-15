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
