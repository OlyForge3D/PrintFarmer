using System.Text.Json;
using Farm.Web.Api.Services.Interfaces;
using Farm.Web.Shared;

namespace Farm.Web.Api.Services;

public class PresetService : IPresetService
{
    private FilamentPresetsDto? _presets;
    private readonly string _path = Path.Combine(AppContext.BaseDirectory, "filament.presets.json");
    private static readonly JsonSerializerOptions s_writeOptions = new() { WriteIndented = true };

    public PresetService()
    {
        try
        {
            if (File.Exists(_path))
            {
                string json = File.ReadAllText(_path);
                FilamentPresetsDto? cfg = JsonSerializer.Deserialize<FilamentPresetsDto>(json);
                if (cfg is not null)
                {
                    _presets = cfg;
                }
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
            string json = JsonSerializer.Serialize(_presets, s_writeOptions);
            File.WriteAllText(_path, json);
        }
        catch { /* ignore */ }
    }

    private static FilamentPresetsDto Default() => new(
        Presets: new Dictionary<string, TempTargets>
        {
            ["ABS"] = new TempTargets(230, 100),
            ["ASA"] = new TempTargets(245, 100),
            ["PLA"] = new TempTargets(205, 60),
            ["PC"] = new TempTargets(260, 110),
            ["PCTG"] = new TempTargets(235, 80),
            ["PETG"] = new TempTargets(240, 85)
        }
    );
}
