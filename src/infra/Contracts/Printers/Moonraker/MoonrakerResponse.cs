using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Contracts.Printers.Moonraker;

// Base response wrapper for Moonraker API responses
public class MoonrakerResponse<T>
{
    [JsonPropertyName("result")]
    public T Result { get; set; } = default!;
}
