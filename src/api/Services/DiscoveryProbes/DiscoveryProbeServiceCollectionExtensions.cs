using System;
using System.Linq;
using System.Reflection;
using Farm.Web.Api.Services.DiscoveryProbes;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Web.Api.Services.DiscoveryProbes;

public static class DiscoveryProbeServiceCollectionExtensions
{
    /// <summary>
    /// Registers all INetworkDiscoveryProbe implementations in the current AppDomain with DI as singletons.
    /// </summary>
    public static IServiceCollection AddAllNetworkDiscoveryProbes(this IServiceCollection services)
    {
        Type probeType = typeof(INetworkDiscoveryProbe);
        Type attrType = typeof(DiscoveryProbeAttribute);
        IEnumerable<Type?> probeImplementations = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a =>
            {
                try
                {
                    return a.GetTypes();
                }
                catch (ReflectionTypeLoadException rex)
                {
                    // Some dynamically generated proxy assemblies may throw when enumerating types.
                    // Use the types that successfully loaded and skip the rest.
                    return rex.Types.Where(t => t != null)!;
                }
                catch
                {
                    // Ignore any assembly we can't reflect over (native, dynamic, etc.)
                    return Array.Empty<Type>();
                }
            })
            .Where(t => t != null)
            .Where(t => probeType.IsAssignableFrom(t!) && !t!.IsInterface && !t!.IsAbstract)
            .Where(t => t!.GetCustomAttributes(attrType, false).Length > 0);

        foreach (Type? impl in probeImplementations)
        {
            Type implType = impl!; // we filtered nulls above
            _ = services.AddSingleton(typeof(INetworkDiscoveryProbe), implType);
        }
        return services;
    }
}
