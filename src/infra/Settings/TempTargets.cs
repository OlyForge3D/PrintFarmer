using System.Text.Json;
using System.Text.Json.Serialization;
using Farm.Infrastructure;

namespace Farm.Infrastructure.Settings;

// Filament presets DTO

public sealed class TempTargets
{
    public TempTargets() { }
    public TempTargets(int hotend, int bed)
    {
        Hotend = hotend;
        Bed = bed;
    }

    [JsonPropertyName("hotend")]
    public int Hotend { get; set; }

    [JsonPropertyName("bed")]
    public int Bed { get; set; }
}
