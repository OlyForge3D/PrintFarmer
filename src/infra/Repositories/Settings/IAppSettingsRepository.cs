using System;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Repositories.Settings;

/// <summary>
/// Repository for managing application-wide settings and configuration values.
/// </summary>
/// <remarks>
/// Provides access to AppSettingsEntity records which store system-level configuration,
/// flags, and state that must persist across application restarts.
///
/// Common use cases:
/// - Distributed locks for one-time operations (e.g., system profile seeding)
/// - Feature flags and configuration toggles
/// - System initialization state tracking
/// - Cross-instance state synchronization
/// </remarks>
public interface IAppSettingsRepository
{
    /// <summary>
    /// Retrieves a setting value by its unique key.
    /// </summary>
    /// <param name="key">The setting key to retrieve</param>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>The AppSettingsEntity if found; null if not found</returns>
    Task<AppSettingsEntity?> GetAsync(string key, CancellationToken ct = default);

    /// <summary>
    /// Retrieves a read-only setting snapshot directly from persisted state.
    /// </summary>
    /// <param name="key">The setting key to retrieve</param>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>The untracked AppSettingsEntity snapshot if found; null if not found</returns>
    /// <remarks>
    /// Unlike <see cref="GetAsync"/>, this query never resolves an entity from the current
    /// DbContext identity map. Use it for runtime gates that must observe changes committed
    /// by another context while the current scope is still active.
    /// </remarks>
    Task<AppSettingsEntity?> GetReadOnlyAsync(string key, CancellationToken ct = default);

    /// <summary>
    /// Sets or updates a setting value.
    /// </summary>
    /// <param name="key">The setting key</param>
    /// <param name="value">The setting value</param>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <remarks>
    /// Creates a new setting if the key doesn't exist, or updates the existing value
    /// if the key already exists. Sets UpdatedAt to current UTC time.
    /// </remarks>
    Task SetAsync(string key, string value, CancellationToken ct = default);

    /// <summary>
    /// Attempts to atomically insert a brand-new setting row, relying solely on the unique
    /// index on <see cref="AppSettingsEntity.Key"/> to arbitrate concurrent first-writers.
    /// </summary>
    /// <param name="key">The setting key</param>
    /// <param name="value">The setting value</param>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>
    /// <see langword="true"/> if this call's row was the one durably committed;
    /// <see langword="false"/> if a concurrent caller committed a row for the same key first
    /// (the unique-index violation is caught and swallowed rather than propagated).
    /// </returns>
    /// <remarks>
    /// Unlike <see cref="SetAsync"/>, this method performs no existence check before writing:
    /// it always attempts an unconditional insert and lets the database's unique constraint be
    /// the single source of truth for "does this key already exist?" This avoids the
    /// check-then-act race inherent in read-then-upsert, where two concurrent callers can each
    /// observe "no row" and one can end up overwriting the other's already-committed value via
    /// an unintended update rather than hitting a constraint violation. Callers that need
    /// generate-once-ever semantics (e.g. a durable server identity) should use this method for
    /// the initial write and re-read on a <see langword="false"/> result instead of calling
    /// <see cref="SetAsync"/>.
    ///
    /// A <see cref="DbUpdateException"/> is only ever treated as "lost the insert race" after
    /// this method independently confirms a row for <paramref name="key"/> now exists in the
    /// database. If no such row is found, the failure was not a duplicate-key conflict (e.g. a
    /// genuine connectivity or constraint failure unrelated to this key) and the original
    /// exception is rethrown rather than being reported as a benign race loss.
    /// </remarks>
    Task<bool> TryInsertIfAbsentAsync(string key, string value, CancellationToken ct = default);

    /// <summary>
    /// Deletes a setting by its key.
    /// </summary>
    /// <param name="key">The setting key to delete</param>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>True if the setting was found and deleted; false if not found</returns>
    Task<bool> DeleteAsync(string key, CancellationToken ct = default);

    /// <summary>
    /// Saves all pending changes to the database.
    /// </summary>
    /// <param name="ct">Cancellation token for async operation</param>
    Task SaveChangesAsync(CancellationToken ct = default);
}

/// <summary>
/// Entity Framework implementation of IAppSettingsRepository.
/// </summary>
public class EfAppSettingsRepository(AppDbContext db) : IAppSettingsRepository
{
    private readonly AppDbContext _db = db ?? throw new ArgumentNullException(nameof(db));

    public async Task<AppSettingsEntity?> GetAsync(string key, CancellationToken ct = default)
    {
        return await _db.AppSettingsEntities.FirstOrDefaultAsync(s => s.Key == key, ct);
    }

    public async Task<AppSettingsEntity?> GetReadOnlyAsync(string key, CancellationToken ct = default)
    {
        return await _db.AppSettingsEntities
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Key == key, ct);
    }

    public async Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        AppSettingsEntity? existing = await GetAsync(key, ct);

        if (existing != null)
        {
            existing.SettingsJson = value;
            existing.UpdatedAt = DateTime.UtcNow;
        }
        else
        {
            var setting = new AppSettingsEntity
            {
                Key = key,
                SettingsJson = value,
                UpdatedAt = DateTime.UtcNow
            };
            await _db.AppSettingsEntities.AddAsync(setting, ct);
        }
    }

    public async Task<bool> TryInsertIfAbsentAsync(string key, string value, CancellationToken ct = default)
    {
        var setting = new AppSettingsEntity
        {
            Key = key,
            SettingsJson = value,
            UpdatedAt = DateTime.UtcNow
        };

        _ = await _db.AppSettingsEntities.AddAsync(setting, ct);

        try
        {
            _ = await _db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException)
        {
            // Detach our losing (or failed) entity so the context doesn't keep retrying the
            // same failed insert on a later SaveChangesAsync call from this same scope.
            _db.Entry(setting).State = EntityState.Detached;

            // A DbUpdateException here is only a benign "lost the insert race" outcome if a
            // row for this key genuinely exists now - i.e. a concurrent caller's insert won.
            // Confirm that independently (bypassing the identity map with AsNoTracking) rather
            // than assuming every DbUpdateException is a duplicate-key conflict: an unrelated
            // failure (connectivity loss, a different constraint, etc.) must propagate instead
            // of being silently swallowed and misreported as "someone else won the race".
            bool rowExists = await _db.AppSettingsEntities
                .AsNoTracking()
                .AnyAsync(s => s.Key == key, ct);

            if (!rowExists)
            {
                throw;
            }

            return false;
        }
    }

    public async Task<bool> DeleteAsync(string key, CancellationToken ct = default)
    {
        AppSettingsEntity? existing = await GetAsync(key, ct);
        if (existing == null)
        {
            return false;
        }

        _db.AppSettingsEntities.Remove(existing);
        return true;
    }

    public async Task SaveChangesAsync(CancellationToken ct = default)
    {
        await _db.SaveChangesAsync(ct);
    }
}
