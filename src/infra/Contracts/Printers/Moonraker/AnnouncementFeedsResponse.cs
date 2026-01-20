using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Contracts.Printers.Moonraker;

public class AnnouncementFeedsResponse
{
    [JsonPropertyName("feeds")]
    public string[] Feeds { get; set; } = Array.Empty<string>();
}
