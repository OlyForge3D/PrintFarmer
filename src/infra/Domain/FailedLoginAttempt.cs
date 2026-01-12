using System;
using System.ComponentModel.DataAnnotations;

namespace Farm.Infrastructure.Domain;

/// <summary>
/// Tracks failed login attempts for account lockout functionality
/// </summary>
public class FailedLoginAttempt
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Username or email that was attempted
    /// </summary>
    [Required]
    [MaxLength(256)]
    public string Identifier { get; set; } = string.Empty;

    /// <summary>
    /// IP address of the failed attempt
    /// </summary>
    [MaxLength(45)] // IPv6 max length
    public string? IpAddress { get; set; }

    /// <summary>
    /// When the failed attempt occurred
    /// </summary>
    public DateTime AttemptedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Reason for failure (e.g., "Invalid password", "User not found", "Account locked")
    /// </summary>
    [MaxLength(256)]
    public string? FailureReason { get; set; }
}
