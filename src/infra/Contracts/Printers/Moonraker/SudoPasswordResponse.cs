using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Contracts.Printers.Moonraker;

public class SudoPasswordResponse
{
    [JsonPropertyName("sudo_responses")]
    public string[] SudoResponses { get; set; } = Array.Empty<string>();

    [JsonPropertyName("is_restarting")]
    public bool IsRestarting { get; set; }
}
