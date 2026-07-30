using System.ComponentModel.DataAnnotations;

namespace Farm.Infrastructure.Domain;

/// <summary>
/// Maps a printable output (a G-code file or a project file entry) to the
/// printed-part SKU it produces, plus how many copies per successful print.
/// The harvest workflow consults these mappings before falling back to the
/// caller-provided <c>outputs</c> array on the harvest request.
/// </summary>
public class PartOutputMapping
{
    public Guid Id { get; set; }

    public Guid PartInventoryId { get; set; }

    public PartInventory? PartInventory { get; set; }

    /// <summary>
    /// Optional source: a specific G-code file. Exactly one of
    /// <see cref="GcodeFileId"/> or <see cref="PrintProjectFileId"/> must be set.
    /// </summary>
    public Guid? GcodeFileId { get; set; }

    public GcodeFile? GcodeFile { get; set; }

    public Guid? PrintProjectFileId { get; set; }

    public PrintProjectFile? PrintProjectFile { get; set; }

    /// <summary>Number of copies of the SKU produced per successful print run.</summary>
    public int Quantity { get; set; } = 1;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
