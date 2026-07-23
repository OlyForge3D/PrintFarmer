using Farm.Infrastructure.Dtos;

namespace Farm.Infrastructure.Services.Sync;

/// <summary>
/// The bi-directional library sync service (#845). Exposes a cursor-based pull that pages the
/// append-only journal within the caller's visibility scope, and a transactional batch apply
/// that enforces optimistic concurrency, auto-merges genuinely independent membership changes,
/// and reports structured conflicts. Both operations preserve the exactly-once journal
/// semantics established by #844: entity mutations and their journal entries commit together
/// under a single unit of work.
/// </summary>
public interface ILibrarySyncService
{
    /// <summary>
    /// Pulls an ordered page of changes visible to the caller with a revision greater than the
    /// supplied cursor. Paging is deterministic (ascending revision), bounded, and visibility
    /// scoped so no other user's changes can leak.
    /// </summary>
    /// <param name="cursor">Opaque continuation cursor, or null/empty to start from the beginning.</param>
    /// <param name="limit">Requested page size; clamped to the service bounds.</param>
    /// <param name="callerUserId">The calling user.</param>
    /// <param name="callerIsAdmin">Whether the caller has administrator scope.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<LibrarySyncPullResultDto> PullAsync(string? cursor, int? limit, Guid callerUserId, bool callerIsAdmin, CancellationToken ct);

    /// <summary>
    /// Applies a batch of client mutations atomically. On any unresolvable conflict the entire
    /// batch is rolled back and a <see cref="Farm.Infrastructure.Exceptions.SyncConflictException"/>
    /// is thrown; otherwise all mutations and their journal entries commit under a single
    /// <c>SaveChangesAsync</c>.
    /// </summary>
    /// <param name="request">The batch of operations to apply.</param>
    /// <param name="callerUserId">The calling user (recorded as the actor).</param>
    /// <param name="callerIsAdmin">Whether the caller has administrator scope.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<ApplySyncResultDto> ApplyAsync(ApplySyncRequestDto request, Guid callerUserId, bool callerIsAdmin, CancellationToken ct);
}
