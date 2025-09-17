using Farm.Web.Shared;

namespace Farm.Web.Api.Services.SlicerServices;

/// <summary>
/// Simple in-memory settings store for slicer runtime configuration.
/// Persisted in memory for now; can be swapped with DB-backed implementation later.
/// </summary>
public class InMemorySlicerSettingsService : ISlicerSettingsService
{
    private readonly object _lock = new();
    private SlicerSettingsDto _settings;

    public InMemorySlicerSettingsService(IConfiguration cfg)
    {
        // Initialize from configuration if available
        bool enabled = cfg.GetValue<bool?>("SlicerWorker:Enabled") ?? false;
        Dictionary<SlicerEngineType, PerEngineSlicerSetting> per = new();
        foreach (SlicerEngineType engine in Enum.GetValues<SlicerEngineType>())
        {
            IConfigurationSection section = cfg.GetSection($"SlicerExecutables:{engine}");
            string? path = section["Path"];
            string? args = section["ArgsTemplate"];
            if (!string.IsNullOrWhiteSpace(path) || !string.IsNullOrWhiteSpace(args))
            {
                per[engine] = new PerEngineSlicerSetting(path, args);
            }
        }
        double jitterPercent = cfg.GetValue<double?>("SlicerWorker:JitterPercent") ?? 15.0;
        _settings = new SlicerSettingsDto(enabled, per, jitterPercent);
    }

    public SlicerSettingsDto GetSettings()
    {
        lock (_lock)
        {
            // Return a shallow copy to avoid callers mutating internal state directly
            return new SlicerSettingsDto(_settings.Enabled, new Dictionary<SlicerEngineType, PerEngineSlicerSetting>(_settings.PerEngine), _settings.JitterPercent);
        }
    }

    public void SaveSettings(SlicerSettingsDto settings)
    {
        if (settings is null)
        {
            ArgumentNullException.ThrowIfNull(settings);
        }
        lock (_lock)
        {
            _settings = new SlicerSettingsDto(settings.Enabled, new Dictionary<SlicerEngineType, PerEngineSlicerSetting>(settings.PerEngine), settings.JitterPercent);
        }
    }
}
