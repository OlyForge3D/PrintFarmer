using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Contracts.Printers.Moonraker;

public class PythonInfo
{
    [JsonPropertyName("version")]
    public object[] Version { get; set; } = Array.Empty<object>();

    [JsonPropertyName("version_string")]
    public string VersionString { get; set; } = string.Empty;
}
