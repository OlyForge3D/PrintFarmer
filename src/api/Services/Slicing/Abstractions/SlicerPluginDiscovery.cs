using System.Reflection;
using Farm.Web.Shared.Contracts.Slicing.Libraries;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Web.Api.Services.Slicing.Abstractions;

/// <summary>
/// Discovers and loads slicer library plugins from referenced assemblies.
/// Uses the SlicerPluginAttribute to find and register implementations.
/// </summary>
public static class SlicerPluginDiscovery
{
    private static readonly List<ISlicerLibrary> RegisteredLibraries = [];
    private static readonly List<ISlicerUIProvider> RegisteredUIProviders = [];

    /// <summary>
    /// Discovers all slicer plugins in the current application domain and registers them.
    /// Should be called during application startup before AddSlicerRegistry().
    /// </summary>
    /// <param name="services">The service collection to register plugins into</param>
    /// <returns>The service collection for chaining</returns>
    /// <remarks>
    /// This method:
    /// 1. Scans all loaded assemblies for SlicerPluginAttribute
    /// 2. Instantiates library and UI provider types from attributes
    /// 3. Collects them for registry initialization
    /// 
    /// Example:
    /// <code>
    /// services.DiscoverAndRegisterSlicerPlugins()
    ///         .AddSlicerRegistry();
    /// </code>
    /// </remarks>
    public static IServiceCollection DiscoverAndRegisterSlicerPlugins(this IServiceCollection services)
    {
        try
        {
            // Get all assemblies in the current domain
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();

            foreach (Assembly assembly in assemblies)
            {
                try
                {
                    // Look for SlicerPluginAttribute on the assembly
                    List<SlicerPluginAttribute> pluginAttributes = assembly
                        .GetCustomAttributes(typeof(SlicerPluginAttribute), inherit: false)
                        .OfType<SlicerPluginAttribute>()
                        .ToList();

                    foreach (SlicerPluginAttribute attribute in pluginAttributes)
                    {
                        try
                        {
                            // Verify types implement required interfaces
                            if (!typeof(ISlicerLibrary).IsAssignableFrom(attribute.LibraryType))
                            {
                                throw new InvalidOperationException(
                                    $"Slicer library type {attribute.LibraryType.FullName} must implement ISlicerLibrary");
                            }

                            if (!typeof(ISlicerUIProvider).IsAssignableFrom(attribute.UIProviderType))
                            {
                                throw new InvalidOperationException(
                                    $"Slicer UI provider type {attribute.UIProviderType.FullName} must implement ISlicerUIProvider");
                            }

                            // Instantiate library and UI provider
                            ISlicerLibrary library = (ISlicerLibrary?)Activator.CreateInstance(attribute.LibraryType)
                                ?? throw new InvalidOperationException(
                                    $"Failed to instantiate slicer library type {attribute.LibraryType.FullName}");

                            ISlicerUIProvider uiProvider = (ISlicerUIProvider?)Activator.CreateInstance(attribute.UIProviderType)
                                ?? throw new InvalidOperationException(
                                    $"Failed to instantiate slicer UI provider type {attribute.UIProviderType.FullName}");

                            // Register instances
                            RegisteredLibraries.Add(library);
                            RegisteredUIProviders.Add(uiProvider);

                            System.Diagnostics.Debug.WriteLine(
                                $"[SlicerPluginDiscovery] Loaded plugin: {library.SlicerName} v{library.SlicerVersion} " +
                                $"from assembly {assembly.GetName().Name}");
                        }
                        catch (Exception ex)
                        {
                            throw new InvalidOperationException(
                                $"Error loading slicer plugin from attribute in assembly {assembly.GetName().Name}: {ex.Message}",
                                ex);
                        }
                    }
                }
                catch (Exception ex) when (ex is not InvalidOperationException)
                {
                    // Skip assemblies that fail to load attributes (e.g., dynamic assemblies, native modules)
                    // But log InvalidOperationException since those indicate plugin loading issues
                    continue;
                }
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Error during slicer plugin discovery: {ex.Message}", ex);
        }

        return services;
    }

    /// <summary>
    /// Initializes the slicer registry with discovered plugins.
    /// Must be called after DiscoverAndRegisterSlicerPlugins().
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddSlicerRegistry(this IServiceCollection services)
    {
        SlicerRegistry registry = new SlicerRegistry(RegisteredLibraries, RegisteredUIProviders);
        return services.AddSingleton<ISlicerRegistry>(registry);
    }
}
