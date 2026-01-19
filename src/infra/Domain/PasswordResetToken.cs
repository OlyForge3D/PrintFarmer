using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Diagnostics.CodeAnalysis;
using Farm.Infrastructure;
using Farm.Infrastructure.Annotations;

namespace Farm.Infrastructure.Domain;

public class PasswordResetToken
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public User User { get; set; } = null!;

    public string Token { get; set; } = string.Empty; // URL-safe token (base64 or GUID)

    public DateTime CreatedAt { get; set; }

    public DateTime ExpiresAt { get; set; } // Typically 1 hour expiration

    public bool IsUsed { get; set; }

    public DateTime? UsedAt { get; set; }

    public string? UsedByIp { get; set; }
}
