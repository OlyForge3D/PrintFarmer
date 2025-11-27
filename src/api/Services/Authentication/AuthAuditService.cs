using System.Text.Json;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Telemetry;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Services.Authentication;

/// <summary>
/// Service for logging authentication and authorization events for security audit trail
/// </summary>
public class AuthAuditService : IAuthAuditService
{
    private readonly Microsoft.EntityFrameworkCore.IDbContextFactory<Farm.Infrastructure.Data.AppDbContext> _dbFactory;
    private readonly IUnifiedLoggingService _logging;

    public AuthAuditService(Microsoft.EntityFrameworkCore.IDbContextFactory<Farm.Infrastructure.Data.AppDbContext> dbFactory, IUnifiedLoggingService logging)
    {
        _dbFactory = dbFactory;
        _logging = logging;
    }

    // Centralized save helper so writes always use a factory-created context
    private async Task SaveAuditAsync(AuthAuditLog auditLog, CancellationToken cancellationToken = default)
    {
        using AppDbContext ctx = _dbFactory.CreateDbContext();
        ctx.AuthAuditLogs.Add(auditLog);
        await ctx.SaveChangesAsync(cancellationToken);
        _logging.LogInformation($"[AuthAudit] Saved audit {auditLog.EventType} Id={auditLog.Id}");
        try
        {
            string? provider = ctx.Database.ProviderName;
            string? conn = null;
            try
            { conn = ctx.Database.GetConnectionString(); }
            catch { }
            Console.WriteLine($"[AuthAuditService] Saved audit Id={auditLog.Id} Provider={provider} Conn={conn} EventType={auditLog.EventType}");
        }
        catch { }
    }

    public async Task LogLoginAsync(Guid userId, string? ipAddress, string? userAgent, string? correlationId = null, CancellationToken cancellationToken = default)
    {
        AuthAuditLog auditLog = new AuthAuditLog
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

        AuthAuditLog auditLog = new AuthAuditLog
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
        _logging.LogWarning($"[AuthAudit] Login failed for '{usernameOrEmail}' from IP: {ipAddress} - Reason: {reason}");
    }

    public async Task LogLogoutAsync(Guid userId, string? ipAddress, string? userAgent, string? correlationId = null, CancellationToken cancellationToken = default)
    {
        AuthAuditLog auditLog = new AuthAuditLog
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
        _logging.LogInformation($"[AuthAudit] Logout for UserId: {userId} from IP: {ipAddress}");
    }

    public async Task LogRegisterAsync(Guid userId, string? ipAddress, string? userAgent, string? correlationId = null, CancellationToken cancellationToken = default)
    {
        AuthAuditLog auditLog = new AuthAuditLog
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
        _logging.LogInformation($"[AuthAudit] New user registered: UserId: {userId} from IP: {ipAddress}");
    }

    public async Task LogPasswordChangeAsync(Guid userId, string? ipAddress, string? userAgent, string? correlationId = null, CancellationToken cancellationToken = default)
    {
        AuthAuditLog auditLog = new AuthAuditLog
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
        _logging.LogInformation($"[AuthAudit] Password changed for UserId: {userId} from IP: {ipAddress}");
    }

    public async Task LogPasswordResetInitiatedAsync(string email, string? ipAddress, string? userAgent, string? correlationId = null, CancellationToken cancellationToken = default)
    {
        var metadata = new { Email = email };

        AuthAuditLog auditLog = new AuthAuditLog
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
        _logging.LogInformation($"[AuthAudit] Password reset initiated for email: {email} from IP: {ipAddress}");
    }

    public async Task LogPasswordResetAsync(Guid userId, string? ipAddress, string? userAgent, string? correlationId = null, CancellationToken cancellationToken = default)
    {
        AuthAuditLog auditLog = new AuthAuditLog
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
        _logging.LogInformation($"[AuthAudit] Password reset completed for UserId: {userId} from IP: {ipAddress}");
    }

    public async Task LogAccountLockedAsync(Guid userId, int attemptCount, TimeSpan lockoutDuration, string? ipAddress, string? correlationId = null, CancellationToken cancellationToken = default)
    {
        var metadata = new
        {
            AttemptCount = attemptCount,
            LockoutDurationMinutes = lockoutDuration.TotalMinutes
        };

        AuthAuditLog auditLog = new AuthAuditLog
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
        _logging.LogWarning($"[AuthAudit] Account locked for UserId: {userId} after {attemptCount} failed attempts. Lockout duration: {lockoutDuration.TotalMinutes} minutes");
    }

    public async Task LogAccountUnlockedAsync(Guid userId, string reason, string? ipAddress, string? correlationId = null, CancellationToken cancellationToken = default)
    {
        var metadata = new { Reason = reason };

        AuthAuditLog auditLog = new AuthAuditLog
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
        _logging.LogInformation($"[AuthAudit] Account unlocked for UserId: {userId}. Reason: {reason}");
    }

    public async Task LogRefreshTokenAsync(Guid userId, string? ipAddress, string? correlationId = null, CancellationToken cancellationToken = default)
    {
        AuthAuditLog auditLog = new AuthAuditLog
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
        _logging.LogInformation($"[AuthAudit] Token refreshed for UserId: {userId} from IP: {ipAddress}");
    }

    public async Task LogTokenRevokedAsync(Guid userId, Guid revokedByUserId, string reason, string? ipAddress, string? correlationId = null, CancellationToken cancellationToken = default)
    {
        var metadata = new
        {
            RevokedByUserId = revokedByUserId,
            Reason = reason
        };

        AuthAuditLog auditLog = new AuthAuditLog
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
        _logging.LogWarning($"[AuthAudit] Token revoked for UserId: {userId} by admin UserId: {revokedByUserId}. Reason: {reason}");
    }

    public async Task<List<AuthAuditLog>> GetUserAuditLogAsync(Guid userId, int pageSize = 50, int pageNumber = 1, CancellationToken cancellationToken = default)
    {
        using AppDbContext ctx = _dbFactory.CreateDbContext();
        return await ctx.AuthAuditLogs
            .Where(aal => aal.UserId == userId)
            .OrderByDescending(aal => aal.Timestamp)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
    }

    public async Task<List<AuthAuditLog>> GetRecentFailedLoginsAsync(int count = 100, CancellationToken cancellationToken = default)
    {
        using AppDbContext ctx = _dbFactory.CreateDbContext();
        return await ctx.AuthAuditLogs
            .Where(aal => aal.EventType == AuthEventType.LoginFailed)
            .OrderByDescending(aal => aal.Timestamp)
            .Take(count)
            .ToListAsync(cancellationToken);
    }

    public async Task<List<AuthAuditLog>> GetSecurityEventsAsync(DateTime? since = null, int pageSize = 100, CancellationToken cancellationToken = default)
    {
        using AppDbContext ctx = _dbFactory.CreateDbContext();
        IQueryable<AuthAuditLog> query = ctx.AuthAuditLogs
            .Where(aal => aal.EventType == AuthEventType.AccountLocked ||
                         aal.EventType == AuthEventType.AccountUnlocked ||
                         aal.EventType == AuthEventType.PasswordReset ||
                         aal.EventType == AuthEventType.TokenRevoked);

        if (since.HasValue)
        {
            query = query.Where(aal => aal.Timestamp >= since.Value);
        }

        return await query
            .OrderByDescending(aal => aal.Timestamp)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
    }
}
