using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Settings;

// Use PerEngineSlicerSetting from AppSettings.cs (record)
[AppSetting(SectionName)]
[SettingDisplay(Name = "G-code Upload", Description = "Settings for G-code file uploads.", Icon = "pf-icon-gcodeupload", Group = "Files", Order = 6)]
public class GcodeUploadSettings : IAppSetting, IValidatableSetting
{
    public const string SectionName = "GcodeUpload";

    private static readonly List<string> _defaultExtensions = new() { ".gcode" };
    private List<string> _allowedExtensions = new List<string>(_defaultExtensions);

    public static string SectionKey => SectionName;

    [SettingDisplay(Name = "Allowed Extensions", Description = "File extensions allowed for upload (e.g. .gcode)", InputType = SettingInputType.Array, IsMulti = true, Required = true)]
    [JsonPropertyName("allowedExtensions")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1002:Do not expose generic lists", Justification = "Exposing as IList for serialization and API stability")]
    public IList<string> AllowedExtensions
    {
        get => _allowedExtensions;
        set => _allowedExtensions = NormalizeExtensions(value);
    }

    [SettingDisplay(Name = "Daily Upload Limit (Bytes)", Description = "Maximum total bytes allowed for upload per day.", InputType = SettingInputType.Number, Required = true)]
    [JsonPropertyName("dailyUploadLimitBytes")]
    public long DailyUploadLimitBytes { get; set; } = 2L * 1024 * 1024 * 1024;

    /// <summary>
    /// Normalizes extensions: trims whitespace, ensures dot prefix, lowercases, and removes duplicates.
    /// </summary>
    private static List<string> NormalizeExtensions(IList<string>? extensions)
    {
        if (extensions == null || extensions.Count == 0)
        {
            return new List<string>(_defaultExtensions);
        }

        return extensions
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .Select(e => e.Trim().ToLowerInvariant())
            .Select(e => e.StartsWith('.') ? e : "." + e)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // MinValue/MaxValue can be set via metadata reflection or in the attribute property initializer at runtime if needed.
    public void Validate()
    {
        // Re-normalize to ensure no duplicates (in case deserialization bypassed setter)
        _allowedExtensions = NormalizeExtensions(_allowedExtensions);

        if (_allowedExtensions.Count == 0)
        {
            throw new ValidationException("At least one allowed extension is required.");
        }

        if (DailyUploadLimitBytes < 1)
        {
            throw new ValidationException("DailyUploadLimitBytes must be positive.");
        }
    }
}
