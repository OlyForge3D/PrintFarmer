using Farm.Infrastructure.Domain.Webhooks;

namespace Farm.Infrastructure.Repositories.Webhooks;

/// <summary>
/// Repository for managing webhook subscriptions and delivery logs.
/// </summary>
public interface IWebhookRepository
{
    /// <summary>
    /// Gets all webhook subscriptions ordered by creation date descending.
    /// </summary>
    Task<List<WebhookSubscription>> GetAllAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets a webhook subscription by ID.
    /// </summary>
    Task<WebhookSubscription?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Checks if a webhook subscription exists.
    /// </summary>
    Task<bool> ExistsAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Adds a new webhook subscription.
    /// </summary>
    Task AddAsync(WebhookSubscription webhook, CancellationToken ct = default);

    /// <summary>
    /// Updates an existing webhook subscription.
    /// </summary>
    Task UpdateAsync(WebhookSubscription webhook, CancellationToken ct = default);

    /// <summary>
    /// Deletes a webhook subscription and all its delivery logs.
    /// </summary>
    Task DeleteAsync(WebhookSubscription webhook, CancellationToken ct = default);

    /// <summary>
    /// Gets recent delivery logs for a webhook subscription.
    /// </summary>
    Task<List<WebhookDeliveryLog>> GetDeliveryLogsAsync(Guid webhookId, int limit, CancellationToken ct = default);

    /// <summary>
    /// Saves all pending changes to the database.
    /// </summary>
    Task SaveChangesAsync(CancellationToken ct = default);
}
