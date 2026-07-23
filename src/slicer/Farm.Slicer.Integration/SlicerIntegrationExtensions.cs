using System.Reflection;
using System.Runtime.Loader;
using Farm.Slicer.Module.Contracts.Libraries;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Farm.Slicer.Integration;

/// <summary>
/// Extension methods for wiring the slicer module into the ASP.NET Core host at runtime,
/// with no compile-time dependency on EF Core, SignalR hubs, OrcaSlicer, or migration assemblies.
/// </summary>
public static class SlicerIntegrationExtensions
{
    /// <summary>
    /// Loads slicer plugin DLLs from <c>Slicer:PluginsPath</c>, adds them as MVC
    /// <see cref="ApplicationPart"/>s, and invokes each discovered
    /// <see cref="ISlicerModule"/> to register its DI services.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="mvcBuilder">The MVC builder (from <c>AddControllers()</c>) to add application parts to.</param>
    /// <param name="configuration">The application configuration.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddSlicerIntegration(
        this IServiceCollection services,
        IMvcBuilder mvcBuilder,
        IConfiguration configuration)
    {
        string? pluginsPath = configuration["Slicer:PluginsPath"];
        List<Assembly> loaded = LoadSlicerAssemblies(pluginsPath);

        if (loaded.Count == 0)
        {
            return services;
        }

        // Add each assembly as an MVC ApplicationPart so its controllers and filters
        // are discovered by the routing infrastructure.
        foreach (Assembly assembly in loaded)
        {
            mvcBuilder.AddApplicationPart(assembly);
        }

        // For each plugin-contract type found across all loaded assemblies,
        // create a single instance (may implement multiple interfaces) and wire it up.
        HashSet<Type> seen = [];
        foreach (Assembly assembly in loaded)
        {
            foreach (Type type in GetPluginTypes(assembly))
            {
                if (!seen.Add(type))
                {
                    continue;
                }

                object instance = Activator.CreateInstance(type)
                    ?? throw new InvalidOperationException(
                        $"Failed to create slicer plugin instance for {type.FullName}");

                // Phase 1: call RegisterServices immediately (DI container not yet built).
                if (instance is ISlicerModule module)
                {
                    module.RegisterServices(services, configuration);
                    services.AddSingleton<ISlicerModule>(module);
                }

                // Store hub registrar for deferred MapHubs call (after app.Build()).
                if (instance is ISlicerHubRegistrar hubRegistrar)
                {
                    services.AddSingleton<ISlicerHubRegistrar>(hubRegistrar);
                }
            }
        }

        return services;
    }

    /// <summary>
    /// Maps slicer SignalR hubs registered by any loaded <see cref="ISlicerHubRegistrar"/>.
    /// Call after <c>app.Build()</c> during endpoint configuration.
    /// </summary>
    public static IEndpointRouteBuilder MapSlicerIntegrationHubs(
        this IEndpointRouteBuilder endpoints)
    {
        IEnumerable<ISlicerHubRegistrar> registrars =
            endpoints.ServiceProvider.GetServices<ISlicerHubRegistrar>();

        foreach (ISlicerHubRegistrar registrar in registrars)
        {
            registrar.MapHubs(endpoints);
        }

        return endpoints;
    }

    /// <summary>
    /// Runs post-build configuration for each loaded <see cref="ISlicerModule"/>
    /// (metrics thresholds, alert subscriptions, etc.).
    /// Call after <c>app.Build()</c>.
    /// </summary>
    public static WebApplication UseSlicerIntegration(this WebApplication app)
    {
        foreach (ISlicerModule module in app.Services.GetServices<ISlicerModule>())
        {
            module.Configure(app);
        }

        return app;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────
    private static List<Assembly> LoadSlicerAssemblies(string? pluginsPath)
    {
        List<Assembly> result = [];

        // Resolve relative paths against the application's base directory so the config value
        // "./plugins/slicer" works correctly regardless of the process working directory.
        if (!string.IsNullOrWhiteSpace(pluginsPath) && !Path.IsPathRooted(pluginsPath))
        {
            pluginsPath = Path.GetFullPath(
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, pluginsPath));
        }

        if (string.IsNullOrWhiteSpace(pluginsPath) || !Directory.Exists(pluginsPath))
        {
            // No plugins directory configured (e.g. test environments where slicer assemblies
            // are already loaded via direct project references).  Scan the current AppDomain for
            // any assemblies that expose ISlicerModule / ISlicerHubRegistrar implementations and
            // treat them as the plugin set.
            return AppDomain.CurrentDomain
                .GetAssemblies()
                .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location) && HasPluginTypes(a))
                .ToList();
        }

        HashSet<string> alreadyLoaded = AppDomain.CurrentDomain
            .GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => Path.GetFullPath(a.Location))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (string dll in Directory.GetFiles(pluginsPath, "*.dll"))
        {
            string fullPath = Path.GetFullPath(dll);
            if (alreadyLoaded.Contains(fullPath))
            {
                continue;
            }

            try
            {
                Assembly assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(fullPath);
                result.Add(assembly);
                alreadyLoaded.Add(fullPath);
            }
            catch (Exception ex)
            {
                // Non-fatal: log and continue so a bad DLL doesn't prevent others from loading.
#pragma warning disable CA1848 // Use the LoggerMessage delegates — startup path, not hot path
                using ILoggerFactory? lf = LoggerFactory.Create(b => b.AddConsole());
                ILogger logger = lf.CreateLogger(nameof(SlicerIntegrationExtensions));
                logger.LogWarning(ex, "Failed to load slicer plugin from {Path}", fullPath);
#pragma warning restore CA1848
            }
        }

        return result;
    }

    private static IEnumerable<Type> GetPluginTypes(Assembly assembly)
    {
        Type slicerModule = typeof(ISlicerModule);
        Type hubRegistrar = typeof(ISlicerHubRegistrar);

        return SafeGetTypes(assembly).Where(t =>
            !t.IsAbstract &&
            !t.IsInterface &&
            t.IsPublic &&
            (slicerModule.IsAssignableFrom(t) || hubRegistrar.IsAssignableFrom(t)));
    }

    /// <summary>
    /// Enumerates the public types of <paramref name="assembly"/>, tolerating a transient
    /// <see cref="ReflectionTypeLoadException"/>.
    /// </summary>
    /// <remarks>
    /// When several hosts build concurrently (e.g. parallel integration tests),
    /// <see cref="Assembly.GetTypes"/> can transiently throw while another thread is still loading
    /// part of the assembly's dependency closure. The successfully-loaded types are still exposed on
    /// <see cref="ReflectionTypeLoadException.Types"/> and are sufficient to discover the plugin
    /// contract implementations. Returning those (instead of dropping the whole assembly) makes
    /// slicer-module discovery deterministic regardless of load timing, preventing intermittent
    /// "No service for type 'SlicerDbContext' has been registered" failures.
    /// </remarks>
    private static Type[] SafeGetTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t is not null).ToArray()!;
        }
    }

    /// <summary>Returns <c>true</c> if <paramref name="assembly"/> contains at least one concrete
    /// <see cref="ISlicerModule"/> or <see cref="ISlicerHubRegistrar"/> implementation.</summary>
    private static bool HasPluginTypes(Assembly assembly) => GetPluginTypes(assembly).Any();
}
