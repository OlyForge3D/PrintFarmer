using System.Text.Json.Serialization;
using Farm.Settings;

namespace Farm.Slicer.Module.Settings;

[AppSetting(SectionName)]
[SettingGroup("Slicing", DisplayName = "Slicing", Description = "Slicer and G-code generation settings", Icon = "pf-icon-slicer", Order = 3)]
[SettingDisplay(Name = "Slicer", Description = "Settings for slicer integration and engines.", Icon = "pf-icon-slicer", Group = "Slicing", Order = 1)]
public class SlicerSettings : IAppSetting
{
    public const string SectionName = "Slicer";

    public static string SectionKey => SectionName;

    [SettingDisplay(Name = "Slicer Enabled", Description = "Enable or disable slicer integration.", InputType = SettingInputType.Boolean)]
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [SettingDisplay(Name = "Per-Engine Slicer Settings", Description = "Settings for each slicer engine.", InputType = SettingInputType.Custom)]
    [JsonPropertyName("perEngine")]
    public Dictionary<string, PerEngineSlicerSetting> PerEngine { get; set; } = new();

    [SettingDisplay(Name = "Jitter Percent", Description = "Randomization percentage for slicer jobs.", InputType = SettingInputType.Number)]
    [JsonPropertyName("jitterPercent")]
    public double JitterPercent { get; set; } = 15.0;

    [SettingDisplay(Name = "Worker ID", Description = "Unique identifier for this slicer worker instance.", InputType = SettingInputType.Text)]
    [JsonPropertyName("workerId")]
    public string WorkerId { get; set; } = Environment.MachineName + "-" + Guid.NewGuid().ToString("N")[..8];

    [SettingDisplay(Name = "Max Retry Count", Description = "Maximum number of retries for failed jobs.", InputType = SettingInputType.Number)]
    [JsonPropertyName("maxRetryCount")]
    public int MaxRetryCount { get; set; } = 3;

    [SettingDisplay(Name = "Max Concurrent Jobs", Description = "Upper bound of simultaneous slice jobs per worker enforced by the orchestrator.", InputType = SettingInputType.Number)]
    [JsonPropertyName("maxConcurrentJobs")]
    public int MaxConcurrentJobs { get; set; } = 1;

    [SettingDisplay(Name = "Max Memory (MB)", Description = "Advisory memory ceiling for slice job admission. Future versions will use this for predictive scheduling.", InputType = SettingInputType.Number)]
    [JsonPropertyName("maxMemoryMb")]
    public int MaxMemoryMb { get; set; } = 1024;
}
