using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using Farm.Infrastructure.Annotations;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure;

/// <summary>
/// Request to create a new folder in the models directory
/// </summary>
public record CreateFolderRequest(
    [property: JsonPropertyName("path")] string Path);
