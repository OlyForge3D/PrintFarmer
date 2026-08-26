using System.Reflection;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Modules.Abstractions;

/// <summary>
/// Wires <see cref="IApiModule"/> discovery and registration into the ASP.NET Core host.
/// </summary>
/// <remarks>
/// <para>
/// Discovery is scoped to the assemblies the host explicitly passes to
/// <see cref="AddApiModules"/>, never to every assembly the process happens to have loaded.
/// An earlier draft of this seam scanned <c>AppDomain.CurrentDomain.GetAssemblies()</c>
/// instead; that was rejected in review for two reasons this repo has already hit in
/// practice for the analogous slicer-module seam
/// (<c>Farm.Slicer.Integration.SlicerIntegrationExtensions</c>,
/// <c>Farm.Web.Api.Tests.TestInfrastructure.SlicerModulePreloader</c>):
/// </para>
/// <list type="number">
/// <item><description>
/// .NET loads referenced assemblies lazily -- a <c>ProjectReference</c> alone does not put a
/// module's assembly into the AppDomain by the time the host builds its service collection.
/// A whole-AppDomain scan would silently discover nothing for a real module added in a later
/// phase unless something else happened to touch one of its types first, which is exactly
/// the kind of silent failure this epic's guardrails exist to prevent.
/// </description></item>
/// <item><description>
/// Scanning every loaded assembly is also broader than intended: any transitively loaded
/// assembly (a test host, a package, anything) that happens to expose a public
/// <see cref="IApiModule"/> would be activated implicitly, with discovery order dependent on
/// assembly load order rather than anything the host controls.
/// </description></item>
/// </list>
/// <para>
/// A vertical-slice module therefore ships as an ordinary project reference of
/// <c>Farm.Web.Api</c> (like <c>Farm.Slicer.Module.Api</c> today), and the host names that
/// module's assembly explicitly -- e.g. <c>typeof(SomePartsModule).Assembly</c> -- in the
/// <paramref name="moduleAssemblies"/> passed to <see cref="AddApiModules"/>. As of this
/// phase (#2035) no module assembly exists yet, so the host passes none: this seam adds no
/// application parts, no endpoints, and no routes until a later phase of epic #2019
/// introduces the first module and adds its assembly to that call.
/// </para>
/// </remarks>
public static class ApiModuleHostExtensions
{
    /// <summary>
    /// Scans the given assemblies for concrete <see cref="IApiModule"/> implementations, adds
    /// each module's declaring assembly as an MVC <see cref="ApplicationPart"/>, and invokes
    /// <see cref="IApiModule.ConfigureServices"/> for each discovered instance.
    /// </summary>
    /// <param name="services">The host's service collection.</param>
    /// <param name="mvcBuilder">The MVC builder (from <c>AddControllers()</c>) to add application parts to.</param>
    /// <param name="configuration">The host's configuration root.</param>
    /// <param name="moduleAssemblies">
    /// The assemblies to scan for <see cref="IApiModule"/> implementations. Only these
    /// assemblies are scanned -- discovery never falls back to
    /// <see cref="AppDomain.CurrentDomain"/>. Pass one representative type's
    /// <see cref="Type.Assembly"/> per module (e.g. <c>typeof(SomeModule).Assembly</c>).
    /// Duplicate assemblies are ignored. Omit entirely while no module exists yet.
    /// </param>
    /// <returns>The service collection, for chaining.</returns>
    public static IServiceCollection AddApiModules(
        this IServiceCollection services,
        IMvcBuilder mvcBuilder,
        IConfiguration configuration,
        params Assembly[] moduleAssemblies)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(mvcBuilder);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(moduleAssemblies);

        HashSet<string> seenNames = new(StringComparer.Ordinal);

        // Deduplicate and sort by assembly full name so registration order is deterministic
        // and independent of the order the host happened to list assemblies in.
        IEnumerable<Assembly> orderedAssemblies = moduleAssemblies
            .Distinct()
            .OrderBy(a => a.FullName, StringComparer.Ordinal);

        foreach (Assembly assembly in orderedAssemblies)
        {
            // Sort discovered types too, so instantiation/registration order never depends on
            // reflection metadata ordering (which is an implementation detail, not a contract).
            Type[] moduleTypes = [.. GetApiModuleTypes(assembly)
                .OrderBy(t => t.FullName, StringComparer.Ordinal)];

            if (moduleTypes.Length == 0)
            {
                continue;
            }

            mvcBuilder.AddApplicationPart(assembly);

            foreach (Type type in moduleTypes)
            {
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
