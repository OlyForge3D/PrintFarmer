using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;

namespace Farm.Infrastructure.Domain;

// ============================================================================
// AUTHENTICATION & AUTHORIZATION DOMAIN
// Entities and enums for user authentication, token management, and audit logging.
// ============================================================================
#region Enumerations

/// <summary>
/// Types of authentication events tracked in the audit log.
/// Used for security monitoring, compliance, and troubleshooting.
/// </summary>
public enum AuthEventType
{
    /// <summary>No event type specified.</summary>
    None = 0,

    /// <summary>Successful user login.</summary>
    Login = 1,

    /// <summary>Failed login attempt (invalid credentials).</summary>
    LoginFailed = 2,

    /// <summary>User logged out.</summary>
    Logout = 3,

    /// <summary>New user registration.</summary>
    Register = 4,

    /// <summary>User changed their password.</summary>
    PasswordChange = 5,

    /// <summary>User reset their password using a reset token.</summary>
    PasswordReset = 6,

    /// <summary>Password reset request initiated (email sent).</summary>
    PasswordResetInitiated = 7,

    /// <summary>Account locked due to failed login attempts.</summary>
    AccountLocked = 8,

    /// <summary>Account unlocked by admin or timeout.</summary>
    AccountUnlocked = 9,

    /// <summary>JWT token refreshed using refresh token.</summary>
    RefreshToken = 10,

    /// <summary>Token revoked (logout, security action, or admin).</summary>
    TokenRevoked = 11
}

#endregion

#region Entities

/// <summary>
/// Audit log entry for authentication and authorization events.
/// Provides security monitoring and compliance tracking for all auth-related actions.
/// </summary>
public class AuthAuditLog
{
    /// <summary>Unique identifier for this audit log entry.</summary>
    public Guid Id { get; set; }

    /// <summary>ID of the user involved (null for failed logins where user doesn't exist).</summary>
    public Guid? UserId { get; set; }

    /// <summary>Navigation property to the user.</summary>
    public User? User { get; set; }

    /// <summary>Type of authentication event that occurred.</summary>
    public AuthEventType EventType { get; set; }

    /// <summary>When the event occurred.</summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>IP address of the client making the request.</summary>
    public string? IpAddress { get; set; }

    /// <summary>User agent string from the client's browser/application.</summary>
    public string? UserAgent { get; set; }

    /// <summary>Whether the authentication action succeeded.</summary>
    public bool Success { get; set; }

    /// <summary>Reason for failure if the action did not succeed.</summary>
    public string? FailureReason { get; set; }

    /// <summary>JSON-serialized additional context (email for password reset, lockout duration, etc.).</summary>
    public string? Metadata { get; set; }

    /// <summary>Correlation ID for distributed request tracing.</summary>
    public string? CorrelationId { get; set; }
}

/// <summary>
/// JWT refresh token for maintaining user sessions.
/// Allows clients to obtain new access tokens without re-authenticating.
/// </summary>
public class RefreshToken
{
    /// <summary>Unique identifier for this refresh token.</summary>
    public Guid Id { get; set; }

    /// <summary>ID of the user this token belongs to.</summary>
    public Guid UserId { get; set; }

    /// <summary>Navigation property to the token owner.</summary>
    public User User { get; set; } = null!;

    /// <summary>The refresh token value (cryptographically secure random string).</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>When the refresh token expires.</summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>When the refresh token was created.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>Whether this token has been revoked.</summary>
    public bool IsRevoked { get; set; }

    /// <summary>When the token was revoked (null if not revoked).</summary>
    public DateTime? RevokedAt { get; set; }

    /// <summary>IP address that revoked this token.</summary>
    public string? RevokedByIp { get; set; }

    /// <summary>Token value that replaced this one during refresh rotation.</summary>
    public string? ReplacedByToken { get; set; }

    /// <summary>IP address that created this token.</summary>
    public string CreatedByIp { get; set; } = string.Empty;
}

/// <summary>
/// Record of a revoked JWT access token.
/// Used to maintain a deny-list of tokens that were invalidated before expiration.
/// </summary>
public class RevokedToken
{
    /// <summary>Unique identifier for this revoked token record.</summary>
    public Guid Id { get; set; }

    /// <summary>SHA256 hash of the JWT token (for privacy and storage efficiency).</summary>
    public string TokenHash { get; set; } = string.Empty;

    /// <summary>ID of the user whose token was revoked.</summary>
    public Guid UserId { get; set; }

    /// <summary>Navigation property to the token owner.</summary>
    public User User { get; set; } = null!;

    /// <summary>When the token was revoked.</summary>
    public DateTime RevokedAt { get; set; } = DateTime.UtcNow;

    /// <summary>ID of the admin who revoked the token (null if self-revoked).</summary>
    public Guid? RevokedByUserId { get; set; }

    /// <summary>Navigation property to the admin who revoked the token.</summary>
    public User? RevokedByUser { get; set; }

    /// <summary>Reason for revocation (e.g., "Security breach", "User request", "Admin action").</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>Original token expiration time (for cleanup scheduling).</summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>IP address from which the revocation was initiated.</summary>
    public string? IpAddress { get; set; }
}

/// <summary>
/// Record of a failed login attempt for account lockout functionality.
/// Tracks repeated failures to enable brute-force protection.
/// </summary>
public class FailedLoginAttempt
{
    /// <summary>Unique identifier for this failed attempt record.</summary>
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Username or email that was attempted.</summary>
    [Required]
    [MaxLength(256)]
    public string Identifier { get; set; } = string.Empty;

    /// <summary>IP address of the failed attempt (IPv4 or IPv6).</summary>
    [MaxLength(45)]
    public string? IpAddress { get; set; }

    /// <summary>When the failed attempt occurred.</summary>
    public DateTime AttemptedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Reason for failure (e.g., "Invalid password", "User not found", "Account locked").</summary>
    [MaxLength(256)]
    public string? FailureReason { get; set; }
}

/// <summary>
/// Token for password reset functionality.
/// Allows users to securely reset their password via email link.
/// </summary>
public class PasswordResetToken
{
    /// <summary>Unique identifier for this reset token.</summary>
    public Guid Id { get; set; }

    /// <summary>ID of the user requesting password reset.</summary>
    public Guid UserId { get; set; }

    /// <summary>Navigation property to the user.</summary>
    public User User { get; set; } = null!;

    /// <summary>URL-safe token value (base64 encoded or GUID).</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>When the reset token was created.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>When the reset token expires (typically 1 hour).</summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>Whether this token has been used.</summary>
    public bool IsUsed { get; set; }

    /// <summary>When the token was used (null if not used).</summary>
    public DateTime? UsedAt { get; set; }

    /// <summary>IP address that used this token.</summary>
    public string? UsedByIp { get; set; }
}

/// <summary>
/// Dedicated audit log for login attempts — both successes and failures.
/// Provides an admin-facing security view separate from the broader AuthAuditLog.
/// Username is stored as submitted (truncated, never interpreted); IpAddress supports IPv6.
/// </summary>
public class LoginAuditEntry
{
    /// <summary>Unique identifier for this entry.</summary>
    public Guid Id { get; set; }

    /// <summary>UTC timestamp when the attempt occurred. Stored as DateTime UTC per project conventions.</summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>Username or email as submitted by the client. Nullable — attacker junk is allowed.</summary>
    public string? Username { get; set; }

    /// <summary>Whether the login attempt succeeded.</summary>
    public bool Success { get; set; }

    /// <summary>Real client IP (X-Forwarded-For respected). Stored as string to support IPv6.</summary>
    public string IpAddress { get; set; } = string.Empty;

    /// <summary>User-Agent header value, if present.</summary>
    public string? UserAgent { get; set; }

    /// <summary>Normalized failure code, e.g. "invalid_credentials", "account_locked", "account_disabled". Null on success.</summary>
    public string? FailureReason { get; set; }
}

/// <summary>
/// Password policy configuration entity.
/// Defines password complexity requirements for the system.
/// </summary>
[SuppressMessage("Naming", "CA1724:Type names should not match namespace", Justification = "Named PasswordPolicyEntity to avoid CA1724 conflicts with API domain type.")]
public class PasswordPolicyEntity
{
    /// <summary>Unique identifier for this policy (typically only one active policy).</summary>
    public int Id { get; set; }

    /// <summary>Minimum password length required.</summary>
    public int MinLength { get; set; } = 8;

    /// <summary>Whether passwords must contain at least one uppercase letter.</summary>
    public bool RequireUppercase { get; set; }

    /// <summary>Whether passwords must contain at least one lowercase letter.</summary>
    public bool RequireLowercase { get; set; }

    /// <summary>Whether passwords must contain at least one digit.</summary>
    public bool RequireDigit { get; set; }

    /// <summary>Whether passwords must contain at least one special character.</summary>
    public bool RequireSymbol { get; set; }

    /// <summary>When the policy was last updated.</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

#endregion
