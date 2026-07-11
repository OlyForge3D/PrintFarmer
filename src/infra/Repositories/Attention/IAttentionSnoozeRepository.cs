using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Repositories.Attention;

/// <summary>
/// Persistence for per-user attention snoozes. Implementations MUST enforce per-user
/// isolation; a snooze created by one user MUST never suppress items for another user.
/// </summary>
public interface IAttentionSnoozeRepository
{
    /// <summary>
    /// Returns all snoozes for <paramref name="userId"/> that are still effective at
    /// <paramref name="nowUtc"/>. Expired rows are filtered out server-side.
    /// </summary>
    Task<IReadOnlyList<AttentionSnooze>> GetActiveForUserAsync(
        Guid userId,
        DateTime nowUtc,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Upserts a snooze for the (<paramref name="userId"/>, <paramref name="attentionItemId"/>) pair.
    /// If a row exists it is updated; otherwise a new row is inserted.
    /// </summary>
    /// <param name="userId">Owning user.</param>
    /// <param name="attentionItemId">Computed attention item id.</param>
    /// <param name="snoozedUntilUtc">Snooze expiry.</param>
    /// <param name="nowUtc">Current UTC instant for audit.</param>
    /// <param name="attentionItemAnchorAtUtc">
    /// Optional item <c>OccurredAt</c> at snooze time. When set, the feed treats a
    /// fresh occurrence whose <c>OccurredAt</c> is strictly greater as unsuppressed.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<AttentionSnooze> UpsertAsync(
        Guid userId,
        string attentionItemId,
        DateTime snoozedUntilUtc,
        DateTime nowUtc,
        DateTime? attentionItemAnchorAtUtc,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes any snooze row for the (<paramref name="userId"/>, <paramref name="attentionItemId"/>) pair.
    /// Returns true if a row was removed, false otherwise.
    /// </summary>
    Task<bool> RemoveAsync(
        Guid userId,
        string attentionItemId,
        CancellationToken cancellationToken = default);
}
