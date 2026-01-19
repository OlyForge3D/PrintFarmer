using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Contracts.Printers.Moonraker;

public class AnnouncementDismissResponse
{
    [JsonPropertyName("entry_id")]
    public string EntryId { get; set; } = string.Empty;

    [JsonPropertyName("action")]
    public string Action { get; set; } = string.Empty;
}
