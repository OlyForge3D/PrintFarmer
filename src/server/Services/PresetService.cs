using System.Text.Json;
using Farm.Web.Shared;
using Farm.Web.Server.Services.Interfaces;

namespace Farm.Web.Server.Services;

public class PresetService : IPresetService
{
    private FilamentPresetsDto? _presets;
    private readonly string _path = Path.Combine(AppContext.BaseDirectory, "filament.presets.json");

    public PresetService()
    {
        try
        {
            if (File.Exists(_path))
            {
                var json = File.ReadAllText(_path);
                var cfg = JsonSerializer.Deserialize<FilamentPresetsDto>(json);
                if (cfg is not null) _presets = cfg;
            }
        }
        catch { /* ignore */ }
    }

    public FilamentPresetsDto GetPresets() => _presets ?? Default();

    public void SavePresets(FilamentPresetsDto presets)
    {
        _presets = presets;
        try
        {
            var json = JsonSerializer.Serialize(_presets, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_path, json);
        }
        catch { /* ignore */ }
    }

    private static FilamentPresetsDto Default() => new(
        Abs: new TempTargets(230, 100),
        Asa: new TempTargets(245, 100),
        Pla: new TempTargets(205, 60),
        Pc: new TempTargets(260, 110),
        Pctg: new TempTargets(235, 80),
        Petg: new TempTargets(240, 85)
    );
}
