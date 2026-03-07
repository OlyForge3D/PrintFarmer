using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain.Webhooks;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Repositories.Webhooks;

/// <summary>
/// EF Core implementation of webhook repository.
/// </summary>
public class EfWebhookRepository(AppDbContext db) : IWebhookRepository
{
    private readonly AppDbContext _db = db;

    public async Task<List<WebhookSubscription>> GetAllAsync(CancellationToken ct = default)
    {
        return await _db.WebhookSubscriptions
            .OrderByDescending(w => w.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<WebhookSubscription?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        return await _db.WebhookSubscriptions.FindAsync([id], ct);
    }

    public async Task<bool> ExistsAsync(Guid id, CancellationToken ct = default)
    {
        return await _db.WebhookSubscriptions.AnyAsync(w => w.Id == id, ct);
    }

    public async Task AddAsync(WebhookSubscription webhook, CancellationToken ct = default)
    {
        _db.WebhookSubscriptions.Add(webhook);
        await _db.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(WebhookSubscription webhook, CancellationToken ct = default)
    {
        _db.WebhookSubscriptions.Update(webhook);
        await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(WebhookSubscription webhook, CancellationToken ct = default)
    {
        _db.WebhookSubscriptions.Remove(webhook);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<List<WebhookDeliveryLog>> GetDeliveryLogsAsync(Guid webhookId, int limit, CancellationToken ct = default)
    {
        return await _db.WebhookDeliveryLogs
            .Where(d => d.WebhookSubscriptionId == webhookId)
            .OrderByDescending(d => d.CreatedAt)
            .Take(Math.Clamp(limit, 1, 200))
            .ToListAsync(ct);
    }

    public Task SaveChangesAsync(CancellationToken ct = default)
    {
        return _db.SaveChangesAsync(ct);
    }
}
