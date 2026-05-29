using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Settings;

[AppSetting(SectionName)]
[SettingGroup("Operations", DisplayName = "Operations", Description = "Operational settings and cost tracking", Icon = "pf-icon-operations", Order = 3)]
[SettingDisplay(Name = "Auto-Tagging", Description = "Automatically tag completed jobs with material, color, and nozzle information.", Icon = "pf-icon-tag", Group = "Operations", Order = 5)]
public class AutoTaggingSettings : IAppSetting
{
    public const string SectionName = "AutoTagging";

    public static string SectionKey => SectionName;

    [SettingDisplay(
        Name = "Enable Material Tags",
        Description = "Automatically tag completed jobs with material type (PLA, PETG, ABS, etc.).",
        InputType = SettingInputType.Boolean)]
    [JsonPropertyName("materialTagEnabled")]
    public bool MaterialTagEnabled { get; set; } = true;

    [SettingDisplay(
        Name = "Enable Color Tags",
        Description = "Automatically tag completed jobs with color family (Red, Blue, Green, etc.).",
        InputType = SettingInputType.Boolean)]
    [JsonPropertyName("colorTagEnabled")]
    public bool ColorTagEnabled { get; set; } = true;

    [SettingDisplay(
        Name = "Enable Nozzle Tags",
        Description = "Automatically tag completed jobs with nozzle diameter (0.4mm, 0.6mm, etc.).",
        InputType = SettingInputType.Boolean)]
    [JsonPropertyName("nozzleTagEnabled")]
    public bool NozzleTagEnabled { get; set; } = true;
}
