using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Contracts.Printers.Moonraker;

// Announcement Models
public class AnnouncementListResponse
{
    [JsonPropertyName("entries")]
    public AnnouncementEntry[] Entries { get; set; } = Array.Empty<AnnouncementEntry>();

    [JsonPropertyName("feeds")]
    public string[] Feeds { get; set; } = Array.Empty<string>();

    [JsonPropertyName("modified")]
    public bool? Modified { get; set; }
}
