using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Repositories.Authentication;

/// <summary>
/// Entity Framework implementation of IAuthAuditLogRepository
/// Uses IDbContextFactory for better testability and multi-operation scenarios
/// </summary>
public class EfAuthAuditLogRepository : IAuthAuditLogRepository
{
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;

    public EfAuthAuditLogRepository(IDbContextFactory<AppDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    public async Task AddAsync(AuthAuditLog auditLog, CancellationToken cancellationToken = default)
    {
        using var context = _dbContextFactory.CreateDbContext();
        _ = context.AuthAuditLogs.Add(auditLog);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        using var context = _dbContextFactory.CreateDbContext();
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<List<AuthAuditLog>> GetByUserIdAsync(Guid userId, int pageSize = 50, int pageNumber = 1, CancellationToken cancellationToken = default)
    {
        using var context = _dbContextFactory.CreateDbContext();
        return await context.AuthAuditLogs
            .AsNoTracking()
            .Where(x => x.UserId == userId)
            .OrderByDescending(x => x.Timestamp)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
    }

    public async Task<List<AuthAuditLog>> GetRecentFailedLoginsAsync(int count = 100, CancellationToken cancellationToken = default)
    {
        using var context = _dbContextFactory.CreateDbContext();
        return await context.AuthAuditLogs
            .AsNoTracking()
            .Where(x => x.EventType == AuthEventType.LoginFailed)
            .OrderByDescending(x => x.Timestamp)
            .Take(count)
            .ToListAsync(cancellationToken);
    }

    public async Task<List<AuthAuditLog>> GetSecurityEventsAsync(DateTime? since = null, int pageSize = 100, CancellationToken cancellationToken = default)
    {
        using var context = _dbContextFactory.CreateDbContext();
        var query = context.AuthAuditLogs.AsNoTracking();

        // Filter by security event types
        var securityEventTypes = new[]
        {
            AuthEventType.AccountLocked,
            AuthEventType.AccountUnlocked,
            AuthEventType.PasswordReset,
            AuthEventType.PasswordResetInitiated,
            AuthEventType.TokenRevoked,
            AuthEventType.LoginFailed
        };
        query = query.Where(x => securityEventTypes.Contains(x.EventType));

        if (since.HasValue)
        {
            query = query.Where(x => x.Timestamp >= since.Value);
        }

        return await query
            .OrderByDescending(x => x.Timestamp)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
    }

    public async Task<List<AuthAuditLog>> GetByEventTypeAsync(string eventType, int pageSize = 100, int pageNumber = 1, CancellationToken cancellationToken = default)
    {
        using var context = _dbContextFactory.CreateDbContext();
        // Try to parse the event type as enum
        if (!Enum.TryParse<AuthEventType>(eventType, out var parsedEventType))
        {
            return new List<AuthAuditLog>();
        }

        return await context.AuthAuditLogs
            .AsNoTracking()
            .Where(x => x.EventType == parsedEventType)
            .OrderByDescending(x => x.Timestamp)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
    }

    public async Task<List<AuthAuditLog>> GetByDateRangeAsync(DateTime startDate, DateTime endDate, int pageSize = 100, CancellationToken cancellationToken = default)
    {
        using var context = _dbContextFactory.CreateDbContext();
        return await context.AuthAuditLogs
            .AsNoTracking()
            .Where(x => x.Timestamp >= startDate && x.Timestamp <= endDate)
            .OrderByDescending(x => x.Timestamp)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
    }

    public async Task<int> CountAsync(CancellationToken cancellationToken = default)
    {
        using var context = _dbContextFactory.CreateDbContext();
        return await context.AuthAuditLogs.CountAsync(cancellationToken);
    }

    public async Task<int> CountRecentFailedLoginsAsync(string? usernameOrEmail, TimeSpan timeWindow, CancellationToken cancellationToken = default)
    {
        using var context = _dbContextFactory.CreateDbContext();
        var cutoffTime = DateTime.UtcNow - timeWindow;

        // Load all matching records first (required for SQLite compatibility with string Contains)
        var allLogs = await context.AuthAuditLogs
            .Where(x => x.EventType == AuthEventType.LoginFailed && x.Timestamp >= cutoffTime)
            .ToListAsync(cancellationToken);

        // Apply client-side filtering for username/email search
        if (!string.IsNullOrEmpty(usernameOrEmail))
        {
            return allLogs.Count(x => x.Metadata != null && x.Metadata.Contains(usernameOrEmail, StringComparison.Ordinal));
        }

        return allLogs.Count;
    }
}
