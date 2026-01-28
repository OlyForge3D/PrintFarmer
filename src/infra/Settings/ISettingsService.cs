using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;

namespace Farm.Infrastructure.Settings;

/// <summary>
/// Service for managing application settings with persistence and runtime configuration.
/// Provides access to typed settings, metadata for UI rendering, and distributed locking.
/// </summary>
public interface ISettingsService
{
    /// <summary>
    /// Gets a typed settings instance.
    /// </summary>
    /// <typeparam name="T">The settings type to retrieve.</typeparam>
    /// <returns>The settings instance of the specified type.</returns>
    T Get<T>()
        where T : class;

    /// <summary>
    /// Gets a settings object by its unique key.
    /// </summary>
    /// <param name="key">The settings key identifier.</param>
    /// <returns>The settings object, or null if not found.</returns>
    object GetByKey(string key);

    /// <summary>
    /// Gets all registered settings instances.
    /// </summary>
    IEnumerable<object> All { get; }

    /// <summary>
    /// Reloads all settings from the specified configuration source.
    /// </summary>
    /// <param name="config">The configuration to reload from.</param>
    void Reload(IConfiguration config);

    /// <summary>
    /// Gets metadata for all registered settings for UI rendering and validation.
    /// </summary>
    /// <returns>Collection of settings metadata.</returns>
    IEnumerable<SettingMetadata> GetAllMetadata();

    /// <summary>
    /// Returns metadata for all discovered settings groups for UI organization.
    /// Groups are derived from [SettingGroup] attributes on settings classes.
    /// </summary>
    IEnumerable<SettingGroupMetadata> GetAllGroupMetadata();

    /// <summary>
    /// Saves the specified settings instance to persistent storage.
    /// </summary>
    /// <typeparam name="T">The settings type implementing IAppSetting.</typeparam>
    /// <param name="settings">The settings instance to save.</param>
    void Save<T>(T settings)
        where T : class, IAppSetting;

    /// <summary>
    /// Attempts to acquire a distributed lock for a given key.
    /// Returns true if the lock was acquired (key did not exist or was in a completion state).
    /// </summary>
    /// <param name="lockKey">The unique key identifying the lock.</param>
    /// <param name="ct">Cancellation token for async operation.</param>
    /// <returns>True if the lock was acquired; otherwise, false.</returns>
    Task<bool> TryAcquireLockAsync(string lockKey, CancellationToken ct = default);

    /// <summary>
    /// Marks a distributed lock as completed.
    /// </summary>
    /// <param name="lockKey">The unique key identifying the lock to complete.</param>
    /// <param name="ct">Cancellation token for async operation.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task CompleteLockAsync(string lockKey, CancellationToken ct = default);

    /// <summary>
    /// Clears a distributed lock to allow retry.
    /// </summary>
    /// <param name="lockKey">The unique key identifying the lock to clear.</param>
    /// <param name="ct">Cancellation token for async operation.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task ClearLockAsync(string lockKey, CancellationToken ct = default);
}
