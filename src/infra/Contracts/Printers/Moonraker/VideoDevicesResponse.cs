using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Contracts.Printers.Moonraker;

public class VideoDevicesResponse
{
    [JsonPropertyName("video_devices")]
    public VideoDevice[] VideoDevices { get; set; } = Array.Empty<VideoDevice>();
}
