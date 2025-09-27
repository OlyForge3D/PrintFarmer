using System.Text.Json;
using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Settings
{
    public class AppSettings
    {
        public NetworkDiscoverySettingsDto NetworkDiscovery { get; set; } = new();
        public SystemLogSettingsDto SystemLog { get; set; } = new();
        public SignalRSettingsDto SignalR { get; set; } = new();
        public SlicerSettingsDto Slicer { get; set; } = new();
        public GcodeUploadSettingsDto GcodeUpload { get; set; } = new();
        public FilamentPresetsDto FilamentPresets { get; set; } = new();
    }
    // Filament presets DTO
    public sealed class FilamentPresetsDto
    {
        public Dictionary<string, TempTargets> Presets { get; set; } = new();

        public FilamentPresetsDto() { }
        public FilamentPresetsDto(Dictionary<string, TempTargets> presets)
        {
            Presets = presets ?? new();
        }
    }

    public sealed class TempTargets
    {
        public int Hotend { get; set; }
        public int Bed { get; set; }

        public TempTargets() { }
        public TempTargets(int hotend, int bed)
        {
            Hotend = hotend;
            Bed = bed;
        }
    }
    // SignalR settings DTO (from shared)
    public sealed record SignalRSettingsDto(
        string LogLevel = "Information",
        bool ConsoleLoggingEnabled = true
    );

    // Slicer settings DTO (from shared)
    public record PerEngineSlicerSetting(string? Path, string? ArgsTemplate);
    public enum SlicerEngineType { OrcaSlicer, PrusaSlicer, SuperSlicer }
    public record SlicerSettingsDto(
        bool Enabled = true,
        Dictionary<SlicerEngineType, PerEngineSlicerSetting>? PerEngine = null,
        double JitterPercent = 15.0
    );

    // Gcode upload settings DTO (inline)
    public class GcodeUploadSettingsDto
    {
        public IReadOnlyCollection<string> AllowedExtensions { get; set; } = new List<string> { ".gcode", ".bgcode" }.AsReadOnly();
        public long DailyUploadLimitBytes { get; set; } = 2L * 1024 * 1024 * 1024; // 2GB default
    }

    public class NetworkDiscoverySettingsDto
    {
        public bool EnableDiscovery { get; set; } = true;
        public string DiscoverySubnet { get; set; } = "10.0.0.0/24";
        // Add other network discovery settings
    }

    public class SystemLogSettingsDto
    {
        public int RetentionDays { get; set; } = 30;
        public bool EnableExport { get; set; } = true;
        // Add other log settings
    }
}
