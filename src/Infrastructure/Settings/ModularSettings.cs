using System.ComponentModel.DataAnnotations;

namespace Farm.Infrastructure.Settings
{
    [AppSetting(NetworkDiscoverySettings.SectionName)]
    public class NetworkDiscoverySettings : IAppSetting, IValidatableSetting
    {
        public const string SectionName = "NetworkDiscovery";
        public static string SectionKey => SectionName;
        public bool EnableDiscovery { get; set; } = true;
        public string DiscoverySubnet { get; set; } = "10.0.0.0/24";
        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(DiscoverySubnet))
            {
                throw new ValidationException("DiscoverySubnet is required.");
            }
        }
    }

    [AppSetting(SystemLogSettings.SectionName)]
    public class SystemLogSettings : IAppSetting, IValidatableSetting
    {
        public const string SectionName = "SystemLog";
        public static string SectionKey => SectionName;
        [Range(1, 365)]
        public int RetentionDays { get; set; } = 30;
        public bool EnableExport { get; set; } = true;
        public void Validate()
        {
            if (RetentionDays < 1 || RetentionDays > 365)
            {
                throw new ValidationException("RetentionDays must be between 1 and 365.");
            }
        }
    }

    [AppSetting(SignalRSettings.SectionName)]
    public class SignalRSettings : IAppSetting
    {
        public const string SectionName = "SignalR";
        public static string SectionKey => SectionName;
        public string LogLevel { get; set; } = "Information";
        public bool ConsoleLoggingEnabled { get; set; } = true;
    }

    [AppSetting(SlicerSettings.SectionName)]
    public class SlicerSettings : IAppSetting
    {
        public const string SectionName = "Slicer";
        public static string SectionKey => SectionName;
        public bool Enabled { get; set; } = true;
        public Dictionary<string, PerEngineSlicerSetting> PerEngine { get; set; } = new();
        public double JitterPercent { get; set; } = 15.0;
    }

    // Use PerEngineSlicerSetting from AppSettings.cs (record)

    [AppSetting(GcodeUploadSettings.SectionName)]
    public class GcodeUploadSettings : IAppSetting, IValidatableSetting
    {
        public const string SectionName = "GcodeUpload";
        public static string SectionKey => SectionName;
        private static readonly string[] _defaultExtensions = new[] { ".gcode", ".bgcode" };
        public System.Collections.ObjectModel.ReadOnlyCollection<string> AllowedExtensions { get; set; } =
            new System.Collections.ObjectModel.ReadOnlyCollection<string>(_defaultExtensions);
        public long DailyUploadLimitBytes { get; set; } = 2L * 1024 * 1024 * 1024;
        public void Validate()
        {
            if (AllowedExtensions == null || AllowedExtensions.Count == 0)
            {
                throw new ValidationException("At least one allowed extension is required.");
            }
            if (DailyUploadLimitBytes < 1)
            {
                throw new ValidationException("DailyUploadLimitBytes must be positive.");
            }
        }
    }

    [AppSetting(FilamentPresetsSettings.SectionName)]
    public class FilamentPresetsSettings : IAppSetting
    {
        public const string SectionName = "FilamentPresets";
        public static string SectionKey => SectionName;
        public Dictionary<string, TempTargets> Presets { get; set; } = new();
    }

    // Use TempTargets from AppSettings.cs (sealed class)
}
