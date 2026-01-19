using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Diagnostics.CodeAnalysis;
using Farm.Infrastructure;
using Farm.Infrastructure.Annotations;

namespace Farm.Infrastructure.Domain;

public class RevokedToken
{
    public Guid Id { get; set; }

    public string TokenHash { get; set; } = string.Empty; // SHA256 hash of JWT token (for privacy/storage efficiency)

    public Guid UserId { get; set; }

    public User User { get; set; } = null!;

    public DateTime RevokedAt { get; set; } = DateTime.UtcNow;

    public Guid? RevokedByUserId { get; set; } // Admin who revoked the token

    public User? RevokedByUser { get; set; }

    public string Reason { get; set; } = string.Empty; // Reason for revocation (e.g., "Security breach", "User request", "Admin action")

    public DateTime ExpiresAt { get; set; } // Original token expiration (for cleanup purposes)

    public string? IpAddress { get; set; } // IP from which revocation was initiated
}
