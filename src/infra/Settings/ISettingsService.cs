using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;

namespace Farm.Infrastructure.Settings;

public interface ISettingsService
{
    T Get<T>() where T : class;
    object GetByKey(string key);
    IEnumerable<object> All { get; }
    void Reload(IConfiguration config);
    IEnumerable<SettingMetadata> GetAllMetadata();
    void Save<T>(T settings) where T : class, IAppSetting;

    /// <summary>
    /// Attempts to acquire a distributed lock for a given key.
    /// Returns true if the lock was acquired (key did not exist or was in a completion state).
    /// </summary>
    Task<bool> TryAcquireLockAsync(string lockKey, CancellationToken ct = default);

    /// <summary>
    /// Marks a distributed lock as completed.
    /// </summary>
    Task CompleteLockAsync(string lockKey, CancellationToken ct = default);

    /// <summary>
    /// Clears a distributed lock to allow retry.
    /// </summary>
    Task ClearLockAsync(string lockKey, CancellationToken ct = default);
}
