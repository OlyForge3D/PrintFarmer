using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Contracts.Printers.Moonraker;

public class UserListResponse
{
    [JsonPropertyName("users")]
    public UserInfo[] Users { get; set; } = Array.Empty<UserInfo>();
}
