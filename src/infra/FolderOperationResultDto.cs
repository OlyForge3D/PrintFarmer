using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using Farm.Infrastructure.Annotations;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure;

/// <summary>
/// Response for folder operations
/// </summary>
public record FolderOperationResultDto(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("message")] string Message);
