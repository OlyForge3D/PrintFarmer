using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using Farm.Infrastructure.Annotations;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure;

/// <summary>
/// Tag for organizing and categorizing 3D models
/// </summary>
/// <summary>
/// Tag data transfer object (works for any taggable object type)
/// </summary>
public class TagDto
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Color { get; set; } // Hex color for UI display

    public string? Description { get; set; }
}
