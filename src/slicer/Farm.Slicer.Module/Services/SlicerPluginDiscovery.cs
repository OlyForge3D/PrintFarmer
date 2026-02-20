using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using Farm.Slicer.Module.Contracts.Libraries;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Slicer.Module.Services;

/// <summary>
/// Discovers and loads slicer library plugins from referenced assemblies
/// and, optionally, from a runtime plugins directory.
/// Uses the SlicerPluginAttribute to find and register implementations.
/// </summary>
/// <remarks>
/// <para>
/// <b>Compile-time plugins</b>: Assemblies linked via ProjectReference are
/// automatically in <c>AppDomain.CurrentDomain.GetAssemblies()</c>.
/// </para>
/// <para>
/// <b>Runtime plugins</b>: Call <see cref="LoadPluginAssemblies"/> before
/// <see cref="DiscoverAndRegisterSlicerPlugins"/> to probe a directory for
/// <c>*.dll</c> files that carry <see cref="SlicerPluginAttribute"/>.
/// </para>
/// </remarks>
/// </summary>
public static class SlicerPluginDiscovery
{
    private static readonly List<ISlicerLibrary> RegisteredLibraries = [];
    private static readonly List<ISlicerUIProvider> RegisteredUIProviders = [];

    /// <summary>
    /// Loads all <c>*.dll</c> files from <paramref name="pluginsPath"/> into the
    /// default <see cref="AssemblyLoadContext"/>. Unknown or non-.NET files are
    /// silently skipped. Call this <b>before</b> <see cref="DiscoverAndRegisterSlicerPlugins"/>
    /// so the loaded assemblies appear in <c>AppDomain.CurrentDomain.GetAssemblies()</c>.
    /// </summary>
    /// <param name="pluginsPath">Absolute or relative path to the plugins directory.</param>
    public static void LoadPluginAssemblies(string? pluginsPath)
    {
        if (string.IsNullOrWhiteSpace(pluginsPath))
            return;

        string fullPath = Path.GetFullPath(pluginsPath);
        if (!Directory.Exists(fullPath))
        {
            Debug.WriteLine($"[SlicerPluginDiscovery] Plugins directory does not exist: {fullPath}");
            return;
        }

        string[] dlls = Directory.GetFiles(fullPath, "*.dll", SearchOption.TopDirectoryOnly);
        foreach (string dll in dlls)
        {
            try
            {
                // Skip if already loaded (e.g., via compile-time reference)
                string dllName = Path.GetFileNameWithoutExtension(dll);
                if (AppDomain.CurrentDomain.GetAssemblies().Any(a =>
                    string.Equals(a.GetName().Name, dllName, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                AssemblyLoadContext.Default.LoadFromAssemblyPath(dll);
                Debug.WriteLine($"[SlicerPluginDiscovery] Loaded assembly from plugins dir: {dll}");
            }
            catch (Exception ex)
            {
                // Non-.NET files, native DLLs, or incompatible assemblies — skip
                Debug.WriteLine($"[SlicerPluginDiscovery] Skipped {dll}: {ex.Message}");
            }
        }
    }

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

                            Debug.WriteLine(
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
