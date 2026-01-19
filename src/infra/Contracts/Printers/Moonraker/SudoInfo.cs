using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Contracts.Printers.Moonraker;

public class SudoInfo
{
    [JsonPropertyName("sudo_access")]
    public bool? SudoAccess { get; set; }

    [JsonPropertyName("linux_user")]
    public string LinuxUser { get; set; } = string.Empty;

    [JsonPropertyName("sudo_requested")]
    public bool SudoRequested { get; set; }

    [JsonPropertyName("request_messages")]
    public string[] RequestMessages { get; set; } = Array.Empty<string>();
}
