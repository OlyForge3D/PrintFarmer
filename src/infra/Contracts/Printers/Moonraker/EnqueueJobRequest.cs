using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Contracts.Printers.Moonraker;

public class EnqueueJobRequest
{
    [JsonPropertyName("filenames")]
    public string[] Filenames { get; set; } = Array.Empty<string>();

    [JsonPropertyName("reset")]
    public bool Reset { get; set; }
}
