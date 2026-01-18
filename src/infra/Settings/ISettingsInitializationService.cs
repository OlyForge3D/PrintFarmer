using System;

namespace Farm.Infrastructure.Settings;

/// <summary>
/// Contract for initializing settings from environment on first run.
/// </summary>
public interface ISettingsInitializationService
{
    void InitializeFromEnvironment<T>() where T : class, IAppSetting, new();
}
