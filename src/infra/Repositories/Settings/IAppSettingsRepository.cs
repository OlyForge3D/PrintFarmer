using System;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Repositories.Settings
{
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
    public class EfAppSettingsRepository : IAppSettingsRepository
    {
        private readonly AppDbContext _db;

        public EfAppSettingsRepository(AppDbContext db)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
        }

        public async Task<AppSettingsEntity?> GetAsync(string key, CancellationToken ct = default)
        {
            return await _db.AppSettingsEntities.FirstOrDefaultAsync(s => s.Key == key, ct);
        }

        public async Task SetAsync(string key, string value, CancellationToken ct = default)
        {
            var existing = await GetAsync(key, ct);
            
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

        public async Task<bool> DeleteAsync(string key, CancellationToken ct = default)
        {
            var existing = await GetAsync(key, ct);
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
}
