namespace Farm.Web.Api.Extensions;

#pragma warning disable CA1303, S3885 // Debug logging strings don't need localization; Assembly.LoadFrom intentional for plugin discovery

using Farm.Backend.Plugin.Core;
using Microsoft.Extensions.DependencyInjection;

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
    /// <param name="pluginAssemblies">Optional: specific assemblies to search for plugins. If null, searches all loaded assemblies.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddBackendClientPlugins(
        this IServiceCollection services,
        IEnumerable<System.Reflection.Assembly>? pluginAssemblies = null)
    {
        // Write marker to console immediately so we know this method was called
        Console.WriteLine("=== AddBackendClientPlugins called ===");
        Console.Out.Flush();
        
        // Register the plugin registry as singleton
        var registry = new BackendPluginRegistry();
        services.AddSingleton<IBackendPluginRegistry>(registry);
        services.AddSingleton<IBackendPluginLoader, BackendPluginLoader>();

        // Discover and load plugins dynamically
        DiscoverAndLoadPlugins(registry, services, pluginAssemblies);
        
        Console.WriteLine("=== AddBackendClientPlugins completed ===");
        Console.Out.Flush();

        return services;
    }

    /// <summary>
    /// Dynamically discovers plugin implementations in loaded assemblies.
    /// Supports both standard IBackendClientPlugin and extended IExtendedBackendPlugin implementations.
    /// Uses the BackendPluginAttribute to identify valid plugin assemblies.
    /// </summary>
    /// <param name="registry">The plugin registry to register discovered plugins with.</param>
    /// <param name="services">The service collection for dependency injection setup.</param>
    /// <param name="assembliesToSearch">Optional: specific assemblies to search. If null, searches all loaded assemblies.</param>
    private static void DiscoverAndLoadPlugins(
        BackendPluginRegistry registry,
        IServiceCollection services,
        IEnumerable<System.Reflection.Assembly>? assembliesToSearch = null)
    {
        Console.WriteLine("[Plugin Discovery] Starting plugin discovery");

        // If no assemblies specified, first try to explicitly load plugin DLLs from the app directory
        if (assembliesToSearch == null)
        {
            try
            {
                var appDirectory = AppDomain.CurrentDomain.BaseDirectory;
                Console.WriteLine($"[Plugin Discovery] Plugin search directory: {appDirectory}");
                
                var pluginDlls = Directory.GetFiles(appDirectory, "Farm.Backend.Plugin.*.dll");
                Console.WriteLine($"[Plugin Discovery] Found {pluginDlls.Length} plugin DLLs to load");
                
                foreach (var dllPath in pluginDlls)
                {
                    try
                    {
                        Console.WriteLine($"[Plugin Discovery] Loading: {dllPath}");
                        System.Reflection.Assembly.LoadFrom(dllPath);
                        Console.WriteLine($"[Plugin Discovery]   ✓ Loaded");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Plugin Discovery]   ✗ Error: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Plugin Discovery] Error during plugin DLL loading: {ex.Message}");
            }
        }

        var assemblies = assembliesToSearch ?? AppDomain.CurrentDomain.GetAssemblies();
        Console.WriteLine($"[Plugin Discovery] Scanning {assemblies.Count()} assemblies for plugins");

        var discoveredCount = 0;
        foreach (var assembly in assemblies)
        {
            try
            {
                // Check if assembly has the BackendPluginAttribute
                var pluginAttribute = assembly.GetCustomAttributes(typeof(BackendPluginAttribute), false)
                    .FirstOrDefault() as BackendPluginAttribute;
                
                var assemblyName = assembly.GetName().Name ?? "Unknown";
                
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

                foreach (var pluginType in pluginTypes)
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

    /// <summary>
    /// Gets the capabilities supported by a specific backend type from the plugin registry.
    /// </summary>
    /// <param name="registry">The plugin registry.</param>
    /// <param name="backendType">The backend type identifier.</param>
    /// <returns>An enumerable of supported capability types, or empty if plugin not found.</returns>
    public static IEnumerable<Type> GetCapabilities(this IBackendPluginRegistry registry, string backendType)
    {
        var plugin = registry.GetPlugin(backendType);
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
        var plugin = registry.GetPlugin(backendType);
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
        var plugin = registry.GetPlugin(backendType);
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
        var extendedPlugin = registry.GetExtendedPlugin(backendType);
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
        var extendedPlugin = registry.GetExtendedPlugin(backendType);
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
