using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Settings;

/// <summary>Persisted, bounded metadata-index controls; never an auto-import policy.</summary>
[AppSetting(SectionName)]
[SettingGroup("Files", DisplayName = "Files", Description = "File storage and printer inventory", Icon = "pf-icon-folder", Order = 6)]
[SettingDisplay(Name = "Printer file index", Description = "Metadata-only discovery on idle registered printers.", Icon = "pf-icon-folder", Group = "Files", Order = 1)]
public sealed class PrinterFileIndexSettings : IAppSetting, IValidatableSetting
{
    public const string SectionName = "PrinterFileIndex";

    public static string SectionKey => SectionName;

    [JsonPropertyName("enabled")]
    [SettingDisplay(Name = "Enable printer file index", Description = "Index metadata while printers are idle. Disabling retains catalog entries and local files.", InputType = SettingInputType.Boolean)]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("scanIntervalMinutes")]
    [SettingDisplay(Name = "Idle scan interval", Unit = "minutes", InputType = SettingInputType.Number, MinValue = 1, MaxValue = 1440)]
    public int ScanIntervalMinutes { get; set; } = 15;

    [JsonPropertyName("maxConcurrentIo")]
    [SettingDisplay(Name = "Concurrent printer file operations", Description = "Deployment-wide slots shared by scans, copies and remote deletion. Each printer remains exclusive.", InputType = SettingInputType.Number, MinValue = 1, MaxValue = 16)]
    public int MaxConcurrentIo { get; set; } = 2;

    [JsonPropertyName("scanTimeoutSeconds")]
    [SettingDisplay(Name = "Total scan budget", Unit = "seconds", InputType = SettingInputType.Number, MinValue = 1, MaxValue = 120)]
    public int ScanTimeoutSeconds { get; set; } = 120;

    [JsonPropertyName("maxRequests")]
    [SettingDisplay(Name = "Listing request budget", InputType = SettingInputType.Number, MinValue = 1, MaxValue = 100)]
    public int MaxRequests { get; set; } = 100;

    [JsonPropertyName("maxEntries")]
    [SettingDisplay(Name = "Entry budget", InputType = SettingInputType.Number, MinValue = 1, MaxValue = 10000)]
    public int MaxEntries { get; set; } = 10_000;

    [JsonPropertyName("maxDepth")]
    [SettingDisplay(Name = "Directory depth budget", InputType = SettingInputType.Number, MinValue = 0, MaxValue = 16)]
    public int MaxDepth { get; set; } = 16;

    public void Validate()
    {
        if (ScanIntervalMinutes is < 1 or > 1440 ||
            MaxConcurrentIo is < 1 or > 16 ||
            ScanTimeoutSeconds is < 1 or > 120 ||
            MaxRequests is < 1 or > 100 ||
            MaxEntries is < 1 or > 10_000 ||
            MaxDepth is < 0 or > 16)
        {
            throw new ValidationException("Printer file index limits are outside their supported bounds.");
        }
    }
}
