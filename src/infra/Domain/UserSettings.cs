using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Farm.Infrastructure.Domain;

/// <summary>
/// Per-user preferences stored in the database. One row per user.
/// </summary>
public class UserSettings : IRevisionedEntity
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

    [MaxLength(4096)]
    public string? PrintablesOAuthAccessToken { get; set; }

    [MaxLength(4096)]
    public string? PrintablesOAuthRefreshToken { get; set; }

    [MaxLength(32)]
    public string? PrintablesOAuthTokenType { get; set; }

    [MaxLength(512)]
    public string? PrintablesOAuthScope { get; set; }

    public DateTime? PrintablesOAuthTokenExpiresAtUtc { get; set; }

    public DateTime? PrintablesOAuthLinkedAtUtc { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Opaque compatibility token derived from <see cref="Revision"/>.
    /// </summary>
    [NotMapped]
    public byte[] RowVersion
    {
        get => Revision > 0 ? RevisionETag.EncodeBytes(Revision) : [];
        set => Revision = RevisionETag.Decode(value);
    }

    /// <inheritdoc/>
    public long Revision { get; set; } = 1;
}
