namespace Farm.Backend.Plugin.Core;

using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Default implementation of the backend plugin loader.
/// </summary>
public class BackendPluginLoader : IBackendPluginLoader
{
    /// <summary>
    /// Loads all plugins from the specified directory.
    /// </summary>
    /// <param name="pluginDirectory">The directory to scan for plugins.</param>
    /// <param name="registry">The plugin registry to register loaded plugins with.</param>
    /// <param name="services">The service collection for dependency injection setup.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public Task LoadPluginsAsync(string pluginDirectory, IBackendPluginRegistry registry, IServiceCollection services)
    {
        if (!Directory.Exists(pluginDirectory))
            return Task.CompletedTask;

        var dllFiles = Directory.GetFiles(pluginDirectory, "Farm.Backend.Plugin.*.dll");

        foreach (var dll in dllFiles)
        {
            try
            {
                var assembly = System.Reflection.Assembly.LoadFrom(dll);
                var pluginTypes = assembly.GetTypes()
                    .Where(t => typeof(IBackendClientPlugin).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract);

                foreach (var pluginType in pluginTypes)
                {
                    var instance = (IBackendClientPlugin?)Activator.CreateInstance(pluginType);
                    if (instance != null)
                    {
                        registry.Register(instance);
                        instance.RegisterServices(services);
                    }
                }
            }
            catch (Exception ex)
            {
                // Log plugin load failure but continue with other plugins
                System.Diagnostics.Debug.WriteLine($"Failed to load plugin from {dll}: {ex.Message}");
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Loads a specific plugin type.
    /// </summary>
    /// <typeparam name="T">The plugin type to load.</typeparam>
    /// <param name="registry">The plugin registry to register the plugin with.</param>
    /// <param name="services">The service collection for dependency injection setup.</param>
    public void LoadPlugin<T>(IBackendPluginRegistry registry, IServiceCollection services) where T : IBackendClientPlugin, new()
    {
        var plugin = new T();
        registry.Register(plugin);
        plugin.RegisterServices(services);
    }
}
