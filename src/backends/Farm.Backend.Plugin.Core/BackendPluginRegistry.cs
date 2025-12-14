namespace Farm.Backend.Plugin.Core;

/// <summary>
/// Default implementation of the backend plugin registry.
/// </summary>
public class BackendPluginRegistry : IBackendPluginRegistry
{
    private readonly Dictionary<string, IBackendClientPlugin> _plugins = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    /// <summary>
    /// Registers a backend client plugin.
    /// </summary>
    /// <param name="plugin">The plugin to register.</param>
    /// <exception cref="ArgumentNullException">Thrown when plugin is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when a plugin is already registered for the backend type.</exception>
    public void Register(IBackendClientPlugin plugin)
    {
        if (plugin == null)
            throw new ArgumentNullException(nameof(plugin));

        lock (_lock)
        {
            if (_plugins.ContainsKey(plugin.BackendType))
                throw new InvalidOperationException($"A plugin is already registered for backend type '{plugin.BackendType}'.");

            _plugins[plugin.BackendType] = plugin;
        }
    }

    /// <summary>
    /// Gets a registered plugin by backend type.
    /// </summary>
    /// <param name="backendType">The backend type identifier.</param>
    /// <returns>The plugin if found; otherwise null.</returns>
    public IBackendClientPlugin? GetPlugin(string backendType)
    {
        lock (_lock)
        {
            _plugins.TryGetValue(backendType, out var plugin);
            return plugin;
        }
    }

    /// <summary>
    /// Gets all registered plugins.
    /// </summary>
    /// <returns>An enumerable of all registered plugins.</returns>
    public IEnumerable<IBackendClientPlugin> GetAllPlugins()
    {
        lock (_lock)
        {
            return _plugins.Values.ToList();
        }
    }

    /// <summary>
    /// Checks if a plugin is registered for a specific backend type.
    /// </summary>
    /// <param name="backendType">The backend type identifier.</param>
    /// <returns>True if a plugin is registered; otherwise false.</returns>
    public bool IsRegistered(string backendType)
    {
        lock (_lock)
        {
            return _plugins.ContainsKey(backendType);
        }
    }

    /// <summary>
    /// Gets an extended plugin that supports additional functionality.
    /// </summary>
    /// <param name="backendType">The backend type identifier.</param>
    /// <returns>The extended plugin if found and implements IExtendedBackendPlugin; otherwise null.</returns>
    public IExtendedBackendPlugin? GetExtendedPlugin(string backendType)
    {
        lock (_lock)
        {
            if (_plugins.TryGetValue(backendType, out var plugin) && plugin is IExtendedBackendPlugin extendedPlugin)
            {
                return extendedPlugin;
            }
            return null;
        }
    }

    /// <summary>
    /// Gets all extended plugins that support additional functionality.
    /// </summary>
    /// <returns>An enumerable of all registered extended plugins.</returns>
    public IEnumerable<IExtendedBackendPlugin> GetAllExtendedPlugins()
    {
        lock (_lock)
        {
            return _plugins.Values
                .OfType<IExtendedBackendPlugin>()
                .ToList();
        }
    }
}
