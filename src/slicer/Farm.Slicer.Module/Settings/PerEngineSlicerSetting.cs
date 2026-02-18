using System.Text.Json.Serialization;

namespace Farm.Slicer.Module.Settings;

public sealed class PerEngineSlicerSetting
{
    public PerEngineSlicerSetting()
    {
    }

    public PerEngineSlicerSetting(string path, string argsTemplate)
    {
        Path = path;
        ArgsTemplate = argsTemplate;
    }

    [JsonPropertyName("path")]
    public string? Path { get; set; }

    [JsonPropertyName("argsTemplate")]
    public string? ArgsTemplate { get; set; }
}
