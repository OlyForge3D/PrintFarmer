using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;

namespace Farm.Infrastructure.Settings;

public interface ISettingsService
{
    T Get<T>()
        where T : class;

    object GetByKey(string key);

    IEnumerable<object> All { get; }

    void Reload(IConfiguration config);

    IEnumerable<SettingMetadata> GetAllMetadata();

    /// <summary>
    /// Returns metadata for all discovered settings groups for UI organization.
    /// Groups are derived from [SettingGroup] attributes on settings classes.
    /// </summary>
    IEnumerable<SettingGroupMetadata> GetAllGroupMetadata();

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
