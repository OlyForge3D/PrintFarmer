using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Diagnostics.CodeAnalysis;
using Farm.Infrastructure;
using Farm.Infrastructure.Annotations;

namespace Farm.Infrastructure.Domain;

/// <summary>
/// Represents a virtual folder for organizing 3D models and G-code files.
/// Folders provide hierarchical organization and enable referential integrity through FK relationships.
/// Each folder is associated with a specific content type (models or gcode).
/// </summary>
public class FolderNode
{
    public Guid Id { get; set; }

    public string Path { get; set; } = string.Empty; // Virtual folder path (e.g., "/", "/subfolder")

    public string FolderType { get; set; } = string.Empty; // "models" or "gcode" - specifies which files this folder contains

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? DeletedAt { get; set; } // Soft delete support

    // Navigation properties to files in this folder
    // Note: Model3D navigation removed — Model3D is now in Farm.Slicer.Module.Domain.
    // The relationship is maintained via Model3D.FolderId (soft reference).
    public ICollection<GcodeFile> Files { get; set; } = new List<GcodeFile>();
}
