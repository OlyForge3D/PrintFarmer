using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Settings;

[AppSetting(SectionName)]
[SettingGroup("Integrations", DisplayName = "Integrations", Description = "External service integrations", Icon = "pf-icon-integration", Order = 5)]
[SettingDisplay(Name = "go2rtc", Description = "Settings for go2rtc RTSP-to-WebRTC transcoding sidecar.", Icon = "pf-icon-camera", Group = "Integrations", Order = 2)]
public class Go2RtcSettings : IAppSetting
{
    public const string SectionName = "Go2Rtc";

    public static string SectionKey => SectionName;

    [SettingDisplay(
        Name = "Enabled",
        Description = "Enable go2rtc integration for RTSP stream transcoding to WebRTC/HLS/MSE.",
        InputType = SettingInputType.Boolean)]
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [SettingDisplay(
        Name = "Base URL",
        Description = "Base URL for the go2rtc API (e.g., http://go2rtc:1984 in Docker, http://localhost:1984 for local dev)",
        InputType = SettingInputType.Url)]
    [JsonPropertyName("baseUrl")]
    public string BaseUrl { get; set; } = "http://go2rtc:1984";
}
