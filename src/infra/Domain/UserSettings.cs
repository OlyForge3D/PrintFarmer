using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Farm.Infrastructure.Domain;

/// <summary>
/// Per-user preferences stored in the database. One row per user.
/// </summary>
public class UserSettings
{
    public Guid Id { get; set; }

    [Required]
    public Guid UserId { get; set; }

    public User User { get; set; } = null!;

    // UI display preferences
    [MaxLength(32)]
    public string Theme { get; set; } = "system";

    [MaxLength(16)]
    public string Locale { get; set; } = "en";

    public int ItemsPerPage { get; set; } = 25;

    // Slicer defaults
    [MaxLength(256)]
    public string? DefaultSlicerPreset { get; set; }

    [MaxLength(64)]
    public string? PrintablesUsername { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Optimistic concurrency token. Prevents silent overwrites from concurrent writers.
    /// </summary>
    public byte[] RowVersion { get; set; } = [];
}
