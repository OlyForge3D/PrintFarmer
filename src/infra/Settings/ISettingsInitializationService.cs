using System;

namespace Farm.Infrastructure.Settings;

/// <summary>
/// Contract for initializing settings from environment on first run.
/// </summary>
public interface ISettingsInitializationService
{
    /// <summary>
    /// Initializes settings of the specified type from environment variables on first application run.
    /// </summary>
    /// <typeparam name="T">The settings type implementing IAppSetting.</typeparam>
    void InitializeFromEnvironment<T>()
        where T : class, IAppSetting, new();
}
