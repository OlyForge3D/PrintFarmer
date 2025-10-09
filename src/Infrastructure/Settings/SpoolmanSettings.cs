using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Settings
{
    [AppSetting(SectionName)]
    [SettingDisplay(Name = "Spoolman", Description = "Settings for Spoolman filament management integration.", Icon = "pf-icon-spoolman", Group = "Integrations", Order = 10)]
    public class SpoolmanSettings : IAppSetting
    {
        public const string SectionName = "Spoolman";
        public static string SectionKey => SectionName;

        [SettingDisplay(
            Name = "Base URL",
            Description = "Base URL for the Spoolman API server (e.g., http://spoolman.local:7912)",
            InputType = SettingInputType.Text)]
        [JsonPropertyName("baseUrl")]
        public string BaseUrl { get; set; } = string.Empty;
    }
}
