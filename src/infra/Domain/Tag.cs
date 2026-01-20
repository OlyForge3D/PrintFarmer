using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Diagnostics.CodeAnalysis;
using Farm.Infrastructure;
using Farm.Infrastructure.Annotations;

namespace Farm.Infrastructure.Domain;

/// <summary>
/// Generic tag that can be applied to any taggable object (Model3D, GcodeFile, Printer, etc.)
/// </summary>
public class Tag
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty; // e.g., "functional", "decorative", "tools"

    public string? Color { get; set; } // Optional hex color for UI display (e.g., "#FF5733")

    public string? Description { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
