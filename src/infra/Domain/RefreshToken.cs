using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Diagnostics.CodeAnalysis;
using Farm.Infrastructure;
using Farm.Infrastructure.Annotations;

namespace Farm.Infrastructure.Domain;

public class RefreshToken
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public User User { get; set; } = null!;

    public string Token { get; set; } = string.Empty;

    public DateTime ExpiresAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public bool IsRevoked { get; set; }

    public DateTime? RevokedAt { get; set; }

    public string? RevokedByIp { get; set; }

    public string? ReplacedByToken { get; set; } // Token that replaced this one during refresh

    public string CreatedByIp { get; set; } = string.Empty;
}
