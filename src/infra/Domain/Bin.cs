using System.ComponentModel.DataAnnotations;

namespace Farm.Infrastructure.Domain;

/// <summary>
/// A physical bin, tote, or shelf slot that holds printed parts.
/// Bins are registered against an operator-controlled barcode / label
/// (<see cref="Code"/>) so scanning routes physical parts into stock.
/// </summary>
public class Bin
{
    public Guid Id { get; set; }

    /// <summary>
    /// Canonical barcode / label value printed on the bin. Unique.
    /// This is the value returned by QR / 1D barcode scanners on the floor.
    /// </summary>
    [Required]
    [MaxLength(128)]
    public string Code { get; set; } = string.Empty;

    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    /// <summary>Freeform location descriptor (rack, aisle, shelf).</summary>
    [MaxLength(200)]
    public string? Location { get; set; }

    [MaxLength(1000)]
    public string? Notes { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
