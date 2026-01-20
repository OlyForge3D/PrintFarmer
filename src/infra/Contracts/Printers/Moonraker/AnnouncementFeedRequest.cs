using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Contracts.Printers.Moonraker;

public class AnnouncementFeedRequest
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}
