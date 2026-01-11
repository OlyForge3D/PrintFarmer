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
        var notification = await context.Notifications.FirstOrDefaultAsync(
            n => n.Id == id, cancellationToken);

        if (notification != null)
        {
            notification.IsRead = true;
            notification.ReadAt = DateTime.UtcNow;
            context.Notifications.Update(notification);
            await context.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task MarkMultipleAsReadAsync(IEnumerable<string> ids, CancellationToken cancellationToken = default)
    {
        var notifications = await context.Notifications
            .Where(n => ids.Contains(n.Id))
            .ToListAsync(cancellationToken);

        var now = DateTime.UtcNow;
        foreach (var notification in notifications)
        {
            notification.IsRead = true;
            notification.ReadAt = now;
        }

        context.Notifications.UpdateRange(notifications);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        var notification = await context.Notifications.FirstOrDefaultAsync(
            n => n.Id == id, cancellationToken);

        if (notification != null)
        {
            context.Notifications.Remove(notification);
            await context.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task DeleteExpiredAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var expired = await context.Notifications
            .Where(n => n.UserId == userId && n.ExpiresAt != null && n.ExpiresAt <= now)
            .ToListAsync(cancellationToken);

        if (expired.Count > 0)
        {
            context.Notifications.RemoveRange(expired);
            await context.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task DeleteOldAsync(int retentionDays, CancellationToken cancellationToken = default)
    {
        var cutoffDate = DateTime.UtcNow.AddDays(-retentionDays);
        var old = await context.Notifications
            .Where(n => n.CreatedAt < cutoffDate)
            .ToListAsync(cancellationToken);

        if (old.Count > 0)
        {
            context.Notifications.RemoveRange(old);
            await context.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task<int> GetUnreadCountAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        return await context.Notifications
            .CountAsync(n => n.UserId == userId && !n.IsRead, cancellationToken);
    }
}
