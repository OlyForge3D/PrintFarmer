using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Settings;

// Use PerEngineSlicerSetting from AppSettings.cs (record)

[AppSetting(SectionName)]
[SettingDisplay(Name = "G-code Upload", Description = "Settings for G-code file uploads.", Icon = "pf-icon-gcodeupload", Group = "Files", Order = 6)]
public class GcodeUploadSettings : IAppSetting, IValidatableSetting
{
    public const string SectionName = "GcodeUpload";

    public static string SectionKey => SectionName;

    private static readonly List<string> _defaultExtensions = new() { ".gcode" };

    [SettingDisplay(Name = "Allowed Extensions", Description = "File extensions allowed for upload (e.g. .gcode)", InputType = SettingInputType.Array, IsMulti = true, Required = true)]
    [JsonPropertyName("allowedExtensions")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1002:Do not expose generic lists", Justification = "Exposing as IList for serialization and API stability")]
    public IList<string> AllowedExtensions { get; set; } = new List<string>(_defaultExtensions);

    [SettingDisplay(Name = "Daily Upload Limit (Bytes)", Description = "Maximum total bytes allowed for upload per day.", InputType = SettingInputType.Number, Required = true)]
    [JsonPropertyName("dailyUploadLimitBytes")]
    public long DailyUploadLimitBytes { get; set; } = 2L * 1024 * 1024 * 1024;

    // MinValue/MaxValue can be set via metadata reflection or in the attribute property initializer at runtime if needed.
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
