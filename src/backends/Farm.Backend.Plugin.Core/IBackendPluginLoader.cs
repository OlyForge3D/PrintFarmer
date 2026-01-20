using Microsoft.Extensions.DependencyInjection;

namespace Farm.Backend.Plugin.Core;

/// <summary>
/// Loader for discovering and initializing backend client plugins.
/// </summary>
public interface IBackendPluginLoader
{
    /// <summary>
    /// Loads all plugins from the specified directory.
    /// </summary>
    /// <param name="pluginDirectory">The directory to scan for plugins.</param>
    /// <param name="registry">The plugin registry to register loaded plugins with.</param>
    /// <param name="services">The service collection for dependency injection setup.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task LoadPluginsAsync(string pluginDirectory, IBackendPluginRegistry registry, IServiceCollection services);

    /// <summary>
    /// Loads a specific plugin type.
    /// </summary>
    /// <typeparam name="T">The plugin type to load.</typeparam>
    /// <param name="registry">The plugin registry to register the plugin with.</param>
    /// <param name="services">The service collection for dependency injection setup.</param>
    void LoadPlugin<T>(IBackendPluginRegistry registry, IServiceCollection services)
        where T : IBackendClientPlugin, new();
}
