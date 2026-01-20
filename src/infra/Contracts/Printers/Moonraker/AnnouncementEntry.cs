using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Contracts.Printers.Moonraker;

public class AnnouncementEntry
{
    [JsonPropertyName("entry_id")]
    public string EntryId { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("priority")]
    public string Priority { get; set; } = string.Empty;

    [JsonPropertyName("date")]
    public long Date { get; set; }

    [JsonPropertyName("dismissed")]
    public bool Dismissed { get; set; }

    [JsonPropertyName("date_dismissed")]
    public long? DateDismissed { get; set; }

    [JsonPropertyName("dismiss_wake")]
    public long? DismissWake { get; set; }

    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    [JsonPropertyName("feed")]
    public string Feed { get; set; } = string.Empty;
}
