using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace Farm.Infrastructure.Domain;

public class ApiKey
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid? UserId { get; set; }

    public string Name { get; set; } = string.Empty;

    // Stored as hash
    public string KeyHash { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? ExpiresAt { get; set; }

    /// <summary>
    /// What this key is intended to authenticate. Defaults to <see cref="ApiKeyPurpose.General"/>
    /// (legacy/OctoPrint-compatible slicer uploads) so existing and unscoped keys never
    /// implicitly gain desktop access.
    /// </summary>
    public ApiKeyPurpose Purpose { get; set; } = ApiKeyPurpose.General;

    /// <summary>
    /// Explicit permissions granted to this key. Only meaningful for
    /// <see cref="ApiKeyPurpose.Desktop"/> keys; General-purpose keys always carry
    /// <see cref="ApiKeyScope.None"/>.
    /// </summary>
    public ApiKeyScope Scopes { get; set; } = ApiKeyScope.None;

    /// <summary>
    /// True when <see cref="ExpiresAt"/> is set and has passed. Not persisted.
    /// </summary>
    [NotMapped]
    public bool IsExpired => ExpiresAt.HasValue && ExpiresAt.Value <= DateTime.UtcNow;
}
