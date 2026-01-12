namespace Farm.Backend.Plugin.Core;

/// <summary>
/// Registry for managing backend client plugins.
/// </summary>
public interface IBackendPluginRegistry
{
    /// <summary>
    /// Registers a backend client plugin.
    /// </summary>
    /// <param name="plugin">The plugin to register.</param>
    void Register(IBackendClientPlugin plugin);

    /// <summary>
    /// Gets a registered plugin by backend type.
    /// </summary>
    /// <param name="backendType">The backend type identifier.</param>
    /// <returns>The plugin if found; otherwise null.</returns>
    IBackendClientPlugin? GetPlugin(string backendType);

    /// <summary>
    /// Gets all registered plugins.
    /// </summary>
    /// <returns>An enumerable of all registered plugins.</returns>
    IEnumerable<IBackendClientPlugin> GetAllPlugins();

    /// <summary>
    /// Checks if a plugin is registered for a specific backend type.
    /// </summary>
    /// <param name="backendType">The backend type identifier.</param>
    /// <returns>True if a plugin is registered; otherwise false.</returns>
    bool IsRegistered(string backendType);

    /// <summary>
    /// Gets an extended plugin that supports additional functionality.
    /// </summary>
    /// <param name="backendType">The backend type identifier.</param>
    /// <returns>The extended plugin if found and implements IExtendedBackendPlugin; otherwise null.</returns>
    IExtendedBackendPlugin? GetExtendedPlugin(string backendType);

    /// <summary>
    /// Gets all extended plugins that support additional functionality.
    /// </summary>
    /// <returns>An enumerable of all registered extended plugins.</returns>
    IEnumerable<IExtendedBackendPlugin> GetAllExtendedPlugins();
}
