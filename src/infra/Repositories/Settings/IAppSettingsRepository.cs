using System;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

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
public enum AppSettingsCreateResult
{
    Created,
    DuplicateKey,
}

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
    /// Creates a setting only when the key is absent and commits it atomically.
    /// </summary>
    /// <returns><c>true</c> when this call inserted the row; <c>false</c> when a concurrent writer won.</returns>
    Task<bool> TryCreateAsync(string key, string value, CancellationToken ct = default);

    async Task<AppSettingsCreateResult> TryCreateDetailedAsync(string key, string value, CancellationToken ct = default) =>
        await TryCreateAsync(key, value, ct).ConfigureAwait(false)
            ? AppSettingsCreateResult.Created
            : AppSettingsCreateResult.DuplicateKey;

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
            AppSettingsEntity setting = new AppSettingsEntity
            {
                Key = key,
                SettingsJson = value,
                UpdatedAt = DateTime.UtcNow
            };
            _ = await _db.AppSettingsEntities.AddAsync(setting, ct);
        }
    }

    public async Task<bool> TryCreateAsync(string key, string value, CancellationToken ct = default)
    {
        AppSettingsEntity setting = new AppSettingsEntity
        {
            Key = key,
            SettingsJson = value,
            UpdatedAt = DateTime.UtcNow
        };

        EntityEntry<AppSettingsEntity> entry = await _db.AppSettingsEntities.AddAsync(setting, ct);
        try
        {
            _ = await _db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException exception)
        {
            entry.State = EntityState.Detached;
            if (IsDuplicateKey(exception))
            {
                return false;
            }

            throw new Farm.Infrastructure.Services.HostUpdates.HostUpdateSubsystemUnavailableException(
                "host_update_manifest_binding_database_unavailable", exception);
        }
    }

    public async Task<AppSettingsCreateResult> TryCreateDetailedAsync(string key, string value, CancellationToken ct = default) =>
        await TryCreateAsync(key, value, ct).ConfigureAwait(false)
            ? AppSettingsCreateResult.Created
            : AppSettingsCreateResult.DuplicateKey;

    internal static bool IsDuplicateKey(DbUpdateException exception)
    {
        for (Exception? current = exception.InnerException; current is not null; current = current.InnerException)
        {
            int? number = current.GetType().GetProperty("Number")?.GetValue(current) as int?;
            string? sqlState = current.GetType().GetProperty("SqlState")?.GetValue(current) as string;
            int? sqliteCode = current.GetType().GetProperty("SqliteErrorCode")?.GetValue(current) as int?;
            if (number is 2601 or 2627 || sqliteCode is 19 or 1555 or 2067 || sqlState == "23505")
            {
                return true;
            }
        }

        return false;
    }

    public async Task<bool> DeleteAsync(string key, CancellationToken ct = default)
    {
        AppSettingsEntity? existing = await GetAsync(key, ct);
        if (existing == null)
        {
            return false;
        }

        _ = _db.AppSettingsEntities.Remove(existing);
        return true;
    }

    public async Task SaveChangesAsync(CancellationToken ct = default)
    {
        _ = await _db.SaveChangesAsync(ct);
    }
}
