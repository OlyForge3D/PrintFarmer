#pragma warning disable CA1303, S3885 // Debug logging strings don't need localization; Assembly.LoadFrom intentional for plugin discovery

using System.Reflection;
using Farm.Backend.Plugin.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Web.Api.Extensions;

/// <summary>
/// Extension methods for registering and discovering backend client plugins.
/// Supports both standard plugins and extended plugins that provide additional services.
/// </summary>
public static class BackendPluginExtensions
{
    /// <summary>
    /// Adds backend client plugin discovery to the service collection.
    /// Dynamically discovers and registers plugins without direct references.
    /// Supports both standard plugins and extended plugins with additional services.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">Optional: application configuration used to read
    /// <c>BackendPlugins:PluginsPath</c> for runtime-loaded plugin DLLs.</param>
    /// <param name="pluginAssemblies">Optional: specific assemblies to search for plugins. If null, searches all loaded assemblies.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddBackendClientPlugins(
        this IServiceCollection services,
        IConfiguration? configuration = null,
        IEnumerable<System.Reflection.Assembly>? pluginAssemblies = null)
    {
        // Register the plugin registry as singleton
        var registry = new BackendPluginRegistry();
        services.AddSingleton<IBackendPluginRegistry>(registry);
        services.AddSingleton<IBackendPluginLoader, BackendPluginLoader>();

        // Discover and load plugins dynamically
        string? pluginsPath = configuration?["BackendPlugins:PluginsPath"];
        DiscoverAndLoadPlugins(registry, services, pluginsPath, pluginAssemblies);

        return services;
    }

    /// <summary>
    /// Dynamically discovers plugin implementations in loaded assemblies.
    /// Supports both standard IBackendClientPlugin and extended IExtendedBackendPlugin implementations.
    /// Uses the BackendPluginAttribute to identify valid plugin assemblies.
    /// </summary>
    /// <param name="registry">The plugin registry to register discovered plugins with.</param>
    /// <param name="services">The service collection for dependency injection setup.</param>
    /// <param name="pluginsPath">Optional: directory to scan for additional runtime-loaded plugin DLLs
    /// (e.g. <c>BackendPlugins:PluginsPath</c>). Relative paths are resolved against
    /// <see cref="AppDomain.CurrentDomain.BaseDirectory"/>.</param>
    /// <param name="assembliesToSearch">Optional: specific assemblies to search. If null, searches all loaded assemblies.</param>
    private static void DiscoverAndLoadPlugins(
        BackendPluginRegistry registry,
        IServiceCollection services,
        string? pluginsPath = null,
        IEnumerable<System.Reflection.Assembly>? assembliesToSearch = null)
    {
        // If no assemblies specified, load plugin DLLs from the app directory and the
        // explicitly configured plugins path (BackendPlugins:PluginsPath).
        if (assembliesToSearch == null)
        {
            LoadPluginDllsFromDirectory(AppDomain.CurrentDomain.BaseDirectory);

            if (!string.IsNullOrWhiteSpace(pluginsPath))
            {
                if (!Path.IsPathRooted(pluginsPath))
                {
                    pluginsPath = Path.GetFullPath(
                        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, pluginsPath));
                }

                LoadPluginDllsFromDirectory(pluginsPath);
            }
        }

        IEnumerable<Assembly> assemblies = assembliesToSearch ?? AppDomain.CurrentDomain.GetAssemblies();

        int discoveredCount = 0;
        foreach (Assembly assembly in assemblies)
        {
            try
            {
                // Check if assembly has the BackendPluginAttribute
                var pluginAttribute = assembly.GetCustomAttributes(typeof(BackendPluginAttribute), false)
                    .FirstOrDefault() as BackendPluginAttribute;

                string assemblyName = assembly.GetName().Name ?? "Unknown";

                if (pluginAttribute != null)
                {
                    Console.WriteLine($"[Plugin Discovery] ✓ Plugin found: {assemblyName} ({pluginAttribute.Name})");
                    discoveredCount++;
                }
                else if (assemblyName?.StartsWith("Farm.Backend.Plugin.") == true)
                {
                    Console.WriteLine($"[Plugin Discovery] ⚠ {assemblyName} missing BackendPluginAttribute - skipping");
                    continue;
                }
                else
                {
                    // Skip non-plugin assemblies without the attribute
                    continue;
                }

                // Find all types that implement IBackendClientPlugin (including extended plugins)
                var pluginTypes = assembly.GetTypes()
                    .Where(t => typeof(IBackendClientPlugin).IsAssignableFrom(t) &&
                               !t.IsInterface &&
                               !t.IsAbstract &&
                               t.GetConstructor(Type.EmptyTypes) != null).ToList();

                foreach (Type? pluginType in pluginTypes)
                {
                    try
                    {
                        // Create instance using default constructor
                        var plugin = (IBackendClientPlugin?)Activator.CreateInstance(pluginType);
                        if (plugin != null && !registry.IsRegistered(plugin.BackendType))
                        {
                            registry.Register(plugin);
                            Console.WriteLine($"[Plugin Discovery]   Registered plugin: {plugin.BackendType}");

                            // If this is an extended plugin, call RegisterAdditionalServices
                            if (plugin is IExtendedBackendPlugin extendedPlugin)
                            {
                                try
                                {
                                    extendedPlugin.RegisterAdditionalServices(services);
                                    Console.WriteLine($"[Plugin Discovery]     ✓ RegisterAdditionalServices completed");
                                }
                                catch (Exception exSvc)
                                {
                                    Console.WriteLine($"[Plugin Discovery]     ✗ RegisterAdditionalServices error: {exSvc.GetType().Name}: {exSvc.Message}");
                                    Console.WriteLine($"[Plugin Discovery]        Stack: {exSvc.StackTrace}");
                                    throw; // Re-throw to show the error in the main logs
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Plugin Discovery]   Error instantiating {pluginType.FullName}: {ex.GetType().Name}: {ex.Message}");
                        throw; // Re-throw so we can see it
                    }
                }
            }
            catch (System.Reflection.ReflectionTypeLoadException)
            {
                // Ignore assemblies that can't be scanned for types
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Plugin Discovery] Error scanning assembly: {ex.Message}");
            }
        }

        Console.WriteLine($"[Plugin Discovery] Plugin discovery complete. Discovered {discoveredCount} plugins");
    }

    private static void LoadPluginDllsFromDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (string dllPath in Directory.GetFiles(directory, "Farm.Backend.Plugin.*.dll"))
        {
            try
            {
                System.Reflection.Assembly.LoadFrom(dllPath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Plugin Discovery] Failed to load {dllPath}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Gets the capabilities supported by a specific backend type from the plugin registry.
    /// </summary>
    /// <param name="registry">The plugin registry.</param>
    /// <param name="backendType">The backend type identifier.</param>
    /// <returns>An enumerable of supported capability types, or empty if plugin not found.</returns>
    public static IEnumerable<Type> GetCapabilities(this IBackendPluginRegistry registry, string backendType)
    {
        IBackendClientPlugin? plugin = registry.GetPlugin(backendType);
        return plugin?.GetCapabilities() ?? [];
    }

    /// <summary>
    /// Gets the client type for a specific backend plugin.
    /// </summary>
    /// <param name="registry">The plugin registry.</param>
    /// <param name="backendType">The backend type identifier.</param>
    /// <returns>The client type, or null if plugin not found.</returns>
    public static Type? GetClientType(this IBackendPluginRegistry registry, string backendType)
    {
        IBackendClientPlugin? plugin = registry.GetPlugin(backendType);
        return plugin?.ClientType;
    }

    /// <summary>
    /// Gets the client interface type for a specific backend plugin.
    /// </summary>
    /// <param name="registry">The plugin registry.</param>
    /// <param name="backendType">The backend type identifier.</param>
    /// <returns>The client interface type, or null if plugin not found.</returns>
    public static Type? GetClientInterfaceType(this IBackendPluginRegistry registry, string backendType)
    {
        IBackendClientPlugin? plugin = registry.GetPlugin(backendType);
        return plugin?.ClientInterfaceType;
    }

    /// <summary>
    /// Gets the status client type for a specific backend plugin if it supports extended functionality.
    /// </summary>
    /// <param name="registry">The plugin registry.</param>
    /// <param name="backendType">The backend type identifier.</param>
    /// <returns>The status client type, or null if plugin not found or doesn't support extended functionality.</returns>
    public static Type? GetStatusClientType(this IBackendPluginRegistry registry, string backendType)
    {
        IExtendedBackendPlugin? extendedPlugin = registry.GetExtendedPlugin(backendType);
        return extendedPlugin?.StatusClientType;
    }

    /// <summary>
    /// Gets the status client interface type for a specific backend plugin if it supports extended functionality.
    /// </summary>
    /// <param name="registry">The plugin registry.</param>
    /// <param name="backendType">The backend type identifier.</param>
    /// <returns>The status client interface type, or null if plugin not found or doesn't support extended functionality.</returns>
    public static Type? GetStatusClientInterfaceType(this IBackendPluginRegistry registry, string backendType)
    {
        IExtendedBackendPlugin? extendedPlugin = registry.GetExtendedPlugin(backendType);
        return extendedPlugin?.StatusClientInterfaceType;
    }

    /// <summary>
    /// Gets all registered backend plugins.
    /// </summary>
    /// <param name="registry">The plugin registry.</param>
    /// <returns>An enumerable of all registered plugins.</returns>
    public static IEnumerable<IBackendClientPlugin> GetAllPlugins(this IBackendPluginRegistry registry)
    {
        return registry.GetAllPlugins();
    }

    /// <summary>
    /// Gets all registered extended backend plugins.
    /// </summary>
    /// <param name="registry">The plugin registry.</param>
    /// <returns>An enumerable of all registered extended plugins.</returns>
    public static IEnumerable<IExtendedBackendPlugin> GetAllExtendedPlugins(this IBackendPluginRegistry registry)
    {
        return registry.GetAllExtendedPlugins();
    }
}
