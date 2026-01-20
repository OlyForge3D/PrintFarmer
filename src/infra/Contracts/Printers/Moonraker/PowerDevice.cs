using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Contracts.Printers.Moonraker;

public class PowerDevice
{
    [JsonPropertyName("device")]
    public string Device { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("locked_while_printing")]
    public bool LockedWhilePrinting { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;
}
