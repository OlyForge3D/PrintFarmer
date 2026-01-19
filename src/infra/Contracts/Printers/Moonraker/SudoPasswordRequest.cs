using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Contracts.Printers.Moonraker;

public class SudoPasswordRequest
{
    [JsonPropertyName("password")]
    public string Password { get; set; } = string.Empty;
}
