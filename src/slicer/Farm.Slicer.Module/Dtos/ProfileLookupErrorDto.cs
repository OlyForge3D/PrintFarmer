using System.Text.Json.Serialization;

namespace Farm.Slicer.Module.Dtos;

/// <summary>
/// Describes why no OrcaSlicer machine profiles could be returned for a catalog model.
/// </summary>
public sealed record ProfileLookupErrorDto(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message);
