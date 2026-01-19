using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using Farm.Infrastructure.Annotations;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure;

/// <summary>
/// Request to move model files by ID using target directory ID (more efficient than by path)
/// </summary>
public record MoveModelsRequest(
    [property: JsonPropertyName("modelIds")] IReadOnlyList<string> ModelIds,
    [property: JsonPropertyName("targetDirectoryId")] string TargetDirectoryId);
