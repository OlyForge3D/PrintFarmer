using System.ComponentModel.DataAnnotations;

namespace Farm.Infrastructure.Domain;

/// <summary>
/// A user-curated group of truly identical printers.
/// G-code sliced for a group is safe to print on any printer within that group.
/// A printer belongs to exactly one group (nullable FK, mutually exclusive).
/// </summary>
public class PrinterGroup
{
    public Guid Id { get; set; }

    /// <summary>
    /// Display name for the group (must be unique across all groups).
    /// </summary>
    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Optional description explaining what makes these printers identical.
    /// </summary>
    [MaxLength(1000)]
    public string? Description { get; set; }

    /// <summary>When this group was created.</summary>
    public DateTimeOffset CreatedDate { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>When this group was last updated.</summary>
    public DateTimeOffset UpdatedDate { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Printers that belong to this group.
    /// When the group is deleted, printers get PrinterGroupId set to null.
    /// </summary>
    public ICollection<Printer> Printers { get; set; } = new List<Printer>();

    /// <summary>
    /// Role-based access rules for this group.
    /// When empty, the group is open to all users (backward compatible).
    /// </summary>
    public ICollection<PrinterGroupAccess> AccessRules { get; set; } = new List<PrinterGroupAccess>();
}
