using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Diagnostics.CodeAnalysis;
using Farm.Infrastructure;
using Farm.Infrastructure.Annotations;

namespace Farm.Infrastructure.Domain;

public class AuthAuditLog
{
    public Guid Id { get; set; }

    public Guid? UserId { get; set; } // Nullable for failed login attempts where user doesn't exist

    public User? User { get; set; }

    public AuthEventType EventType { get; set; }

    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    public string? IpAddress { get; set; }

    public string? UserAgent { get; set; }

    public bool Success { get; set; }

    public string? FailureReason { get; set; }

    public string? Metadata { get; set; } // JSON for additional context (e.g., email for forgot password, lockout duration, etc.)

    public string? CorrelationId { get; set; } // For request tracing
}
