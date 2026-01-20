using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain.Notifications;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Repositories.Notifications;

public class EfNotificationRepository(AppDbContext context) : INotificationRepository
{
    public async Task AddAsync(Notification notification, CancellationToken cancellationToken = default)
    {
        await context.Notifications.AddAsync(notification, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<Notification?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        return await context.Notifications.AsNoTracking()
            .FirstOrDefaultAsync(n => n.Id == id, cancellationToken);
    }

    public async Task<IEnumerable<Notification>> GetUserNotificationsAsync(
        Guid userId,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        IQueryable<Notification> query = context.Notifications.AsNoTracking()
            .Where(n => n.UserId == userId)
            .OrderByDescending(n => n.CreatedAt);

        if (limit.HasValue)
        {
            query = query.Take(limit.Value);
        }

        return await query.ToListAsync(cancellationToken);
    }

    public async Task<IEnumerable<Notification>> GetUserUnreadNotificationsAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        return await context.Notifications.AsNoTracking()
            .Where(n => n.UserId == userId && !n.IsRead)
            .OrderByDescending(n => n.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<IEnumerable<Notification>> GetByTypeAsync(
        Guid userId,
        NotificationType type,
        CancellationToken cancellationToken = default)
    {
        return await context.Notifications.AsNoTracking()
            .Where(n => n.UserId == userId && n.Type == type)
            .OrderByDescending(n => n.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<IEnumerable<Notification>> GetByJobIdAsync(
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        return await context.Notifications.AsNoTracking()
            .Where(n => n.JobId == jobId)
            .OrderByDescending(n => n.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task MarkAsReadAsync(string id, CancellationToken cancellationToken = default)
    {
        // EF Core 10: Use ExecuteUpdateAsync for efficient single-statement update
        await context.Notifications
            .Where(n => n.Id == id)
            .ExecuteUpdateAsync(
                setters => setters
                .SetProperty(n => n.IsRead, true)
                .SetProperty(n => n.ReadAt, DateTime.UtcNow), cancellationToken);
    }

    public async Task MarkMultipleAsReadAsync(IEnumerable<string> ids, CancellationToken cancellationToken = default)
    {
        // EF Core 10: Use ExecuteUpdateAsync for efficient bulk update without loading entities
        var idList = ids.ToList();
        await context.Notifications
            .Where(n => idList.Contains(n.Id))
            .ExecuteUpdateAsync(
                setters => setters
                .SetProperty(n => n.IsRead, true)
                .SetProperty(n => n.ReadAt, DateTime.UtcNow), cancellationToken);
    }

    public async Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        // EF Core 10: Use ExecuteDeleteAsync for efficient single-statement delete
        await context.Notifications
            .Where(n => n.Id == id)
            .ExecuteDeleteAsync(cancellationToken);
    }

    public async Task DeleteExpiredAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        // EF Core 10: Use ExecuteDeleteAsync for efficient bulk delete without loading entities
        DateTime now = DateTime.UtcNow;
        await context.Notifications
            .Where(n => n.UserId == userId && n.ExpiresAt != null && n.ExpiresAt <= now)
            .ExecuteDeleteAsync(cancellationToken);
    }

    public async Task DeleteOldAsync(int retentionDays, CancellationToken cancellationToken = default)
    {
        // EF Core 10: Use ExecuteDeleteAsync for efficient bulk delete without loading entities
        DateTime cutoffDate = DateTime.UtcNow.AddDays(-retentionDays);
        await context.Notifications
            .Where(n => n.CreatedAt < cutoffDate)
            .ExecuteDeleteAsync(cancellationToken);
    }

    public async Task<int> GetUnreadCountAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        return await context.Notifications
            .CountAsync(n => n.UserId == userId && !n.IsRead, cancellationToken);
    }
}
