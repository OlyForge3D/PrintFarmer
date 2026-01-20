using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Contracts.Printers.Moonraker;

public class AnnouncementDismissRequest
{
    [JsonPropertyName("entry_id")]
    public string EntryId { get; set; } = string.Empty;

    [JsonPropertyName("wake_time")]
    public long? WakeTime { get; set; }
}
