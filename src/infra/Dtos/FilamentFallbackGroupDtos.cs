using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Dtos;

/// <summary>
/// Wire projection of a filament fallback group (issue #711, F6).
/// Ordered same-material chain over existing toolhead IDs on a single printer.
/// </summary>
public sealed record FilamentFallbackGroupDto(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("printerId")] Guid PrinterId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("materialType")] string MaterialType,
    [property: JsonPropertyName("displayOrder")] int DisplayOrder,
    [property: JsonPropertyName("createdAt")] DateTime CreatedAt,
    [property: JsonPropertyName("updatedAt")] DateTime UpdatedAt,
    [property: JsonPropertyName("members")] IReadOnlyList<FilamentFallbackGroupMemberDto> Members);

/// <summary>
/// Wire projection of a fallback group member. References an existing toolhead on the
/// same printer as the group.
/// </summary>
public sealed record FilamentFallbackGroupMemberDto(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("toolheadId")] Guid ToolheadId,
    [property: JsonPropertyName("position")] int Position,
    [property: JsonPropertyName("toolheadName")] string? ToolheadName,
    [property: JsonPropertyName("toolheadIndex")] int ToolheadIndex,
    [property: JsonPropertyName("currentMaterial")] string? CurrentMaterial,
    [property: JsonPropertyName("currentSpoolId")] int? CurrentSpoolId,
    [property: JsonPropertyName("materialMatches")] bool MaterialMatches);

/// <summary>Request body for creating a fallback group.</summary>
public sealed record CreateFilamentFallbackGroupRequest(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("materialType")] string MaterialType,
    [property: JsonPropertyName("displayOrder")] int? DisplayOrder,
    [property: JsonPropertyName("toolheadIds")] IReadOnlyList<Guid> ToolheadIds);

/// <summary>Request body for updating a fallback group.</summary>
public sealed record UpdateFilamentFallbackGroupRequest(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("materialType")] string MaterialType,
    [property: JsonPropertyName("displayOrder")] int? DisplayOrder,
    [property: JsonPropertyName("toolheadIds")] IReadOnlyList<Guid> ToolheadIds);
