using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using Farm.Infrastructure.Annotations;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure;

/// <summary>
/// Request to move files to a different folder
/// </summary>
public record MoveFilesRequest(
    [property: JsonPropertyName("filePaths")] IReadOnlyList<string> FilePaths,
    [property: JsonPropertyName("targetPath")] string TargetPath);
