using Farm.Infrastructure.Settings;

namespace Farm.Web.Api.Services;

/// <summary>
/// Adapter that provides G-code upload settings from the persisted IAppSetting system.
/// This bridges the IGcodeUploadSettings interface to the database-backed GcodeUploadSettings.
/// </summary>
public class PersistedGcodeUploadSettingsAdapter : IGcodeUploadSettings
{
    private readonly ISettingsService _settingsService;

    public PersistedGcodeUploadSettingsAdapter(ISettingsService settingsService)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
    }

    public IReadOnlyCollection<string> GetAllowedExtensions()
    {
        GcodeUploadSettings? settings = _settingsService.Get<GcodeUploadSettings>();
        if (settings == null || settings.AllowedExtensions == null || settings.AllowedExtensions.Count == 0)
        {
            return new[] { ".gcode" };
        }

        return settings.AllowedExtensions.ToArray();
    }

    public void UpdateAllowedExtensions(IEnumerable<string> extensions)
    {
        GcodeUploadSettings? settings = _settingsService.Get<GcodeUploadSettings>() ?? new GcodeUploadSettings();
        settings.AllowedExtensions = extensions.ToList();
        _settingsService.Save(settings);
    }
}
