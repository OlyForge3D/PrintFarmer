using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Settings;

[AppSetting(SectionName)]
[SettingGroup("Integrations", DisplayName = "Integrations", Description = "External service integrations", Icon = "pf-icon-integration", Order = 5)]
[SettingDisplay(Name = "Spoolman", Description = "Settings for Spoolman filament management integration.", Icon = "pf-icon-spoolman", Group = "Integrations", Order = 1)]
public class SpoolmanSettings : IAppSetting
{
    public const string SectionName = "Spoolman";

    public static string SectionKey => SectionName;

    [SettingDisplay(
        Name = "Base URL",
        Description = "Base URL for the Spoolman API server (e.g., http://spoolman.local:7912)",
        InputType = SettingInputType.Url)]
    [JsonPropertyName("baseUrl")]
    public string BaseUrl { get; set; } = string.Empty;

    [SettingDisplay(
        Name = "Barcode scan debug logging",
        Description = "Record barcode scan attempts and outcomes in the backend database for diagnostics.",
        InputType = SettingInputType.Boolean)]
    [JsonPropertyName("barcodeScanDebugLoggingEnabled")]
    public bool BarcodeScanDebugLoggingEnabled { get; set; }
}
