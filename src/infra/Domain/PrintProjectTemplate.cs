using System.ComponentModel.DataAnnotations;

namespace Farm.Infrastructure.Domain;

/// <summary>
/// Represents a reusable template for creating print projects.
/// Templates define preset file patterns, color requirements, and print counts
/// for common printer kit assemblies (e.g., Voron 2.4, Trident).
/// </summary>
public class PrintProjectTemplate
{
    public Guid Id { get; set; }

    [Timestamp]
    public byte[]? RowVersion { get; set; }

    [Required]
    [MaxLength(255)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(2000)]
    public string? Description { get; set; }

    /// <summary>
    /// Category for grouping templates (e.g., "Voron", "Prusa", "Custom").
    /// </summary>
    [MaxLength(100)]
    public string? Category { get; set; }

    /// <summary>
    /// Default priority for projects created from this template.
    /// </summary>
    public int DefaultPriority { get; set; }

    /// <summary>
    /// Default notes to include in projects created from this template.
    /// </summary>
    [MaxLength(2000)]
    public string? DefaultNotes { get; set; }

    /// <summary>
    /// Whether this template is a system default (cannot be deleted).
    /// </summary>
    public bool IsSystemTemplate { get; set; }

    /// <summary>
    /// Sort order for display in the template list.
    /// </summary>
    public int SortOrder { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    // Navigation property to template file entries
    public ICollection<PrintProjectTemplateFile> Files { get; set; } = new List<PrintProjectTemplateFile>();
}

/// <summary>
/// Represents a file entry within a project template.
/// Defines the expected file pattern, color requirement, and print count.
/// </summary>
public class PrintProjectTemplateFile
{
    public Guid Id { get; set; }

    public Guid PrintProjectTemplateId { get; set; }

    /// <summary>
    /// Display name for this file entry (e.g., "Skirt Clips", "Primary Toolhead").
    /// </summary>
    [Required]
    [MaxLength(255)]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Optional file name pattern to match when adding files (e.g., "*skirt*", "*toolhead*").
    /// Supports simple wildcard matching.
    /// </summary>
    [MaxLength(255)]
    public string? FileNamePattern { get; set; }

    public PrintColorRequirement ColorRequirement { get; set; } = PrintColorRequirement.Base;

    [MaxLength(100)]
    public string? MaterialRequirement { get; set; }

    public int PrintCount { get; set; } = 1;

    public int SortOrder { get; set; }

    [MaxLength(500)]
    public string? Notes { get; set; }

    // Navigation property
    public PrintProjectTemplate Template { get; set; } = null!;
}
