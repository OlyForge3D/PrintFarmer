using System.ComponentModel.DataAnnotations;

namespace Farm.Infrastructure.Domain;

/// <summary>
/// Represents a printer bed surface type (e.g., PEI Smooth, Glass, BuildTak).
/// Used for printer matching, filtering, and auto-dispatch compatibility.
/// </summary>
public class BedType
{
    public Guid Id { get; set; }

    /// <summary>
    /// Display name for the bed type (must be unique).
    /// </summary>
    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Optional description of the bed surface characteristics.
    /// </summary>
    [MaxLength(1000)]
    public string? Description { get; set; }

    /// <summary>
    /// Whether this bed type is a system default (cannot be deleted by users).
    /// </summary>
    public bool IsSystem { get; set; }

    /// <summary>
    /// Optional hex color code for UI badge display (e.g., "#4CAF50").
    /// </summary>
    [MaxLength(9)]
    public string? Color { get; set; }

    /// <summary>When this bed type was created.</summary>
    public DateTimeOffset CreatedDate { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>When this bed type was last updated.</summary>
    public DateTimeOffset UpdatedDate { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Printers that have this bed type.
    /// When the bed type is deleted, printers get BedTypeId set to null.
    /// </summary>
    public ICollection<Printer> Printers { get; set; } = new List<Printer>();
}
