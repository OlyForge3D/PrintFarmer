using Microsoft.Extensions.DependencyInjection;
using System;
using System.Linq;
using Farm.Web.Api.Services.DiscoveryProbes;

namespace Farm.Web.Api.Services.DiscoveryProbes;

public static class DiscoveryProbeServiceCollectionExtensions
{
    /// <summary>
    /// Registers all INetworkDiscoveryProbe implementations in the current AppDomain with DI as singletons.
    /// </summary>
    public static IServiceCollection AddAllNetworkDiscoveryProbes(this IServiceCollection services)
    {
        var probeType = typeof(INetworkDiscoveryProbe);
        var attrType = typeof(DiscoveryProbeAttribute);
        var probeImplementations = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a => a.GetTypes())
            .Where(t => probeType.IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract)
            .Where(t => t.GetCustomAttributes(attrType, false).Length > 0);

        foreach (var impl in probeImplementations)
        {
            services.AddSingleton(typeof(INetworkDiscoveryProbe), impl);
        }
        return services;
    }
}
