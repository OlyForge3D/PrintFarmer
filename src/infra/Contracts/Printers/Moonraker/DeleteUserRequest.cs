using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Contracts.Printers.Moonraker;

public class DeleteUserRequest
{
    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;
}
