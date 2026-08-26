using System.Reflection;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Modules.Abstractions;

/// <summary>
/// Wires <see cref="IApiModule"/> discovery and registration into the ASP.NET Core host, with
/// no compile-time dependency on any particular module assembly.
/// </summary>
/// <remarks>
/// Mirrors the discovery shape already used for slicer plugin modules
/// (<c>Farm.Slicer.Integration.SlicerIntegrationExtensions</c>), scoped to modules that are
/// referenced directly by the host project rather than loaded from a plugins directory: a
/// vertical-slice module is expected to ship as an ordinary project reference of
/// <c>Farm.Web.Api</c> (like <c>Farm.Slicer.Module.Api</c> today), so its assembly is already
/// present in <see cref="AppDomain.CurrentDomain"/> by the time the host calls
/// <see cref="AddApiModules"/>. As of this phase (#2035) zero assemblies implement
/// <see cref="IApiModule"/>, so discovery is guaranteed to find nothing -- this seam adds no
/// application parts, no endpoints, and no routes until a later phase of epic #2019 introduces
/// the first module.
/// </remarks>
public static class ApiModuleHostExtensions
{
    /// <summary>
    /// Scans loaded assemblies for concrete <see cref="IApiModule"/> implementations, adds each
    /// module's declaring assembly as an MVC <see cref="ApplicationPart"/>, and invokes
    /// <see cref="IApiModule.ConfigureServices"/> for each discovered instance.
    /// </summary>
    /// <param name="services">The host's service collection.</param>
    /// <param name="mvcBuilder">The MVC builder (from <c>AddControllers()</c>) to add application parts to.</param>
    /// <param name="configuration">The host's configuration root.</param>
    /// <returns>The service collection, for chaining.</returns>
    public static IServiceCollection AddApiModules(
        this IServiceCollection services,
        IMvcBuilder mvcBuilder,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(mvcBuilder);
        ArgumentNullException.ThrowIfNull(configuration);

        HashSet<Assembly> assembliesWithParts = [];
        HashSet<Type> seenTypes = [];
        HashSet<string> seenNames = new(StringComparer.Ordinal);

        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.IsDynamic || string.IsNullOrEmpty(assembly.Location))
            {
                continue;
            }

            foreach (Type type in GetApiModuleTypes(assembly))
            {
                if (!seenTypes.Add(type))
                {
                    continue;
                }

                if (assembliesWithParts.Add(assembly))
                {
                    mvcBuilder.AddApplicationPart(assembly);
                }

                IApiModule module = (IApiModule)(Activator.CreateInstance(type)
                    ?? throw new InvalidOperationException(
                        $"Failed to create API module instance for {type.FullName}"));

                if (!seenNames.Add(module.Name))
                {
                    throw new InvalidOperationException(
                        $"Duplicate API module name '{module.Name}' ({type.FullName}); module names must be unique.");
                }

                module.ConfigureServices(services, configuration);
                services.AddSingleton(module);
            }
        }

        return services;
    }

    /// <summary>
    /// Invokes <see cref="IApiModule.MapEndpoints"/> for every module registered by
    /// <see cref="AddApiModules"/>. Call after <c>app.Build()</c>, typically alongside
    /// <c>app.MapControllers()</c> during endpoint configuration.
    /// </summary>
    /// <param name="endpoints">The host's endpoint route builder.</param>
    /// <returns>The endpoint route builder, for chaining.</returns>
    public static IEndpointRouteBuilder MapApiModules(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        foreach (IApiModule module in endpoints.ServiceProvider.GetServices<IApiModule>())
        {
            module.MapEndpoints(endpoints);
        }

        return endpoints;
    }

    private static IEnumerable<Type> GetApiModuleTypes(Assembly assembly)
    {
        Type apiModule = typeof(IApiModule);

        return SafeGetTypes(assembly).Where(t =>
            !t.IsAbstract &&
            !t.IsInterface &&
            t.IsPublic &&
            apiModule.IsAssignableFrom(t));
    }

    /// <summary>
    /// Enumerates the public types of <paramref name="assembly"/>, tolerating a transient
    /// <see cref="ReflectionTypeLoadException"/> the same way slicer module discovery does (see
    /// <c>Farm.Slicer.Integration.SlicerIntegrationExtensions.SafeGetTypes</c>) so discovery
    /// stays deterministic under concurrent host startup (e.g. parallel integration tests).
    /// </summary>
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
}
