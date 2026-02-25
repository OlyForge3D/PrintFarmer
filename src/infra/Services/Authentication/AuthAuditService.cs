using System.Text.Json;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Authentication;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.Authentication;

/// <summary>
/// Service for logging authentication and authorization events for security audit trail
/// </summary>
public class AuthAuditService(IAuthAuditLogRepository auditRepository, ILogger<AuthAuditService> logging) : IAuthAuditService
{
    private readonly IAuthAuditLogRepository _auditRepository = auditRepository;
    private readonly ILogger<AuthAuditService> _logging = logging;

    // Centralized save helper
    private async Task SaveAuditAsync(AuthAuditLog auditLog, CancellationToken cancellationToken = default)
    {
        await _auditRepository.AddAsync(auditLog, cancellationToken);
        _logging.LogInformation("[AuthAudit] Saved audit {AuditLogEventType} Id={AuditLogId}", auditLog.EventType, auditLog.Id);
    }

    public async Task LogLoginAsync(Guid userId, string? ipAddress, string? userAgent, string? correlationId = null, CancellationToken cancellationToken = default)
    {
        AuthAuditLog auditLog = new()
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            EventType = AuthEventType.Login,
            Timestamp = DateTime.UtcNow,
            IpAddress = ipAddress,
            UserAgent = userAgent,
            Success = true,
            CorrelationId = correlationId
        };

        await SaveAuditAsync(auditLog, cancellationToken);
    }

    public async Task LogLoginFailedAsync(string usernameOrEmail, string reason, string? ipAddress, string? userAgent, string? correlationId = null, CancellationToken cancellationToken = default)
    {
        var metadata = new { UsernameOrEmail = usernameOrEmail };

        AuthAuditLog auditLog = new()
        {
            Id = Guid.NewGuid(),
            UserId = null, // User doesn't exist or credentials invalid
            EventType = AuthEventType.LoginFailed,
            Timestamp = DateTime.UtcNow,
            IpAddress = ipAddress,
            UserAgent = userAgent,
            Success = false,
            FailureReason = reason,
            Metadata = JsonSerializer.Serialize(metadata),
            CorrelationId = correlationId
        };

        await SaveAuditAsync(auditLog, cancellationToken);
        _logging.LogWarning("[AuthAudit] Login failed for '{UsernameOrEmail}' from IP: {IpAddress} - Reason: {Reason}", usernameOrEmail, ipAddress, reason);
    }

    public async Task LogLogoutAsync(Guid userId, string? ipAddress, string? userAgent, string? correlationId = null, CancellationToken cancellationToken = default)
    {
        AuthAuditLog auditLog = new()
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            EventType = AuthEventType.Logout,
            Timestamp = DateTime.UtcNow,
            IpAddress = ipAddress,
            UserAgent = userAgent,
            Success = true,
            CorrelationId = correlationId
        };

        await SaveAuditAsync(auditLog, cancellationToken);
        _logging.LogInformation("[AuthAudit] Logout for UserId: {UserId} from IP: {IpAddress}", userId, ipAddress);
    }

    public async Task LogRegisterAsync(Guid userId, string? ipAddress, string? userAgent, string? correlationId = null, CancellationToken cancellationToken = default)
    {
        AuthAuditLog auditLog = new()
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            EventType = AuthEventType.Register,
            Timestamp = DateTime.UtcNow,
            IpAddress = ipAddress,
            UserAgent = userAgent,
            Success = true,
            CorrelationId = correlationId
        };

        await SaveAuditAsync(auditLog, cancellationToken);
        _logging.LogInformation("[AuthAudit] New user registered: UserId: {UserId} from IP: {IpAddress}", userId, ipAddress);
    }

    public async Task LogPasswordChangeAsync(Guid userId, string? ipAddress, string? userAgent, string? correlationId = null, CancellationToken cancellationToken = default)
    {
        AuthAuditLog auditLog = new()
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            EventType = AuthEventType.PasswordChange,
            Timestamp = DateTime.UtcNow,
            IpAddress = ipAddress,
            UserAgent = userAgent,
            Success = true,
            CorrelationId = correlationId
        };

        await SaveAuditAsync(auditLog, cancellationToken);
        _logging.LogInformation("[AuthAudit] Password changed for UserId: {UserId} from IP: {IpAddress}", userId, ipAddress);
    }

    public async Task LogPasswordResetInitiatedAsync(string email, string? ipAddress, string? userAgent, string? correlationId = null, CancellationToken cancellationToken = default)
    {
        var metadata = new { Email = email };

        AuthAuditLog auditLog = new()
        {
            Id = Guid.NewGuid(),
            UserId = null, // Could lookup user by email, but keeping null for simplicity
            EventType = AuthEventType.PasswordResetInitiated,
            Timestamp = DateTime.UtcNow,
            IpAddress = ipAddress,
            UserAgent = userAgent,
            Success = true,
            Metadata = JsonSerializer.Serialize(metadata),
            CorrelationId = correlationId
        };

        await SaveAuditAsync(auditLog, cancellationToken);
        _logging.LogInformation("[AuthAudit] Password reset initiated for email: {Email} from IP: {IpAddress}", email, ipAddress);
    }

    public async Task LogPasswordResetAsync(Guid userId, string? ipAddress, string? userAgent, string? correlationId = null, CancellationToken cancellationToken = default)
    {
        AuthAuditLog auditLog = new()
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            EventType = AuthEventType.PasswordReset,
            Timestamp = DateTime.UtcNow,
            IpAddress = ipAddress,
            UserAgent = userAgent,
            Success = true,
            CorrelationId = correlationId
        };

        await SaveAuditAsync(auditLog, cancellationToken);
        _logging.LogInformation("[AuthAudit] Password reset completed for UserId: {UserId} from IP: {IpAddress}", userId, ipAddress);
    }

    public async Task LogAccountLockedAsync(Guid userId, int attemptCount, TimeSpan lockoutDuration, string? ipAddress, string? correlationId = null, CancellationToken cancellationToken = default)
    {
        var metadata = new
        {
            AttemptCount = attemptCount,
            LockoutDurationMinutes = lockoutDuration.TotalMinutes
        };

        AuthAuditLog auditLog = new()
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            EventType = AuthEventType.AccountLocked,
            Timestamp = DateTime.UtcNow,
            IpAddress = ipAddress,
            UserAgent = null,
            Success = true,
            Metadata = JsonSerializer.Serialize(metadata),
            CorrelationId = correlationId
        };

        await SaveAuditAsync(auditLog, cancellationToken);
        _logging.LogWarning("[AuthAudit] Account locked for UserId: {UserId} after {AttemptCount} failed attempts. Lockout duration: {LockoutDurationTotalMinutes} minutes", userId, attemptCount, lockoutDuration.TotalMinutes);
    }

    public async Task LogAccountUnlockedAsync(Guid userId, string reason, string? ipAddress, string? correlationId = null, CancellationToken cancellationToken = default)
    {
        var metadata = new { Reason = reason };

        AuthAuditLog auditLog = new()
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            EventType = AuthEventType.AccountUnlocked,
            Timestamp = DateTime.UtcNow,
            IpAddress = ipAddress,
            UserAgent = null,
            Success = true,
            Metadata = JsonSerializer.Serialize(metadata),
            CorrelationId = correlationId
        };

        await SaveAuditAsync(auditLog, cancellationToken);
        _logging.LogInformation("[AuthAudit] Account unlocked for UserId: {UserId}. Reason: {Reason}", userId, reason);
    }

    public async Task LogRefreshTokenAsync(Guid userId, string? ipAddress, string? correlationId = null, CancellationToken cancellationToken = default)
    {
        AuthAuditLog auditLog = new()
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            EventType = AuthEventType.RefreshToken,
            Timestamp = DateTime.UtcNow,
            IpAddress = ipAddress,
            UserAgent = null,
            Success = true,
            CorrelationId = correlationId
        };

        await SaveAuditAsync(auditLog, cancellationToken);
        _logging.LogInformation("[AuthAudit] Token refreshed for UserId: {UserId} from IP: {IpAddress}", userId, ipAddress);
    }

    public async Task LogTokenRevokedAsync(Guid userId, Guid revokedByUserId, string reason, string? ipAddress, string? correlationId = null, CancellationToken cancellationToken = default)
    {
        var metadata = new
        {
            RevokedByUserId = revokedByUserId,
            Reason = reason
        };

        AuthAuditLog auditLog = new()
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            EventType = AuthEventType.TokenRevoked,
            Timestamp = DateTime.UtcNow,
            IpAddress = ipAddress,
            UserAgent = null,
            Success = true,
            Metadata = JsonSerializer.Serialize(metadata),
            CorrelationId = correlationId
        };

        await SaveAuditAsync(auditLog, cancellationToken);
        _logging.LogWarning("[AuthAudit] Token revoked for UserId: {UserId} by admin UserId: {RevokedByUserId}. Reason: {Reason}", userId, revokedByUserId, reason);
    }

    public async Task<List<AuthAuditLog>> GetUserAuditLogAsync(Guid userId, int pageSize = 50, int pageNumber = 1, CancellationToken cancellationToken = default)
    {
        return await _auditRepository.GetByUserIdAsync(userId, pageSize, pageNumber, cancellationToken);
    }

    public async Task<List<AuthAuditLog>> GetRecentFailedLoginsAsync(int count = 100, CancellationToken cancellationToken = default)
    {
        return await _auditRepository.GetRecentFailedLoginsAsync(count, cancellationToken);
    }

    public async Task<List<AuthAuditLog>> GetSecurityEventsAsync(DateTime? since = null, int pageSize = 100, CancellationToken cancellationToken = default)
    {
        return await _auditRepository.GetSecurityEventsAsync(since, pageSize, cancellationToken);
    }
}
