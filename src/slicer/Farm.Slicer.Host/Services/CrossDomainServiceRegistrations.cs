using Farm.Slicer.Module.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Slicer.Host.Services;

/// <summary>
/// Extension methods for registering HTTP-backed cross-domain lookup services
/// used by the standalone slicer host to resolve entities owned by the main API
/// (printers, catalog models, manufacturers).
/// </summary>
public static class CrossDomainServiceRegistrations
{
    /// <summary>
    /// Registers <see cref="HttpPrinterLookupService"/> and
    /// <see cref="HttpCatalogServiceAdapter"/> with a shared named <c>MainApi</c>
    /// HTTP client and an in-memory cache.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">Application configuration for reading the main API base URL.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddCrossDomainLookupServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Resolve main API base URL from configuration with fallback chain
        string mainApiBaseUrl = configuration["MainApi:BaseUrl"]
            ?? configuration["SlicerApi:BaseUrl"]
            ?? "http://api:5245";

        services.AddHttpClient("MainApi", client =>
        {
            client.BaseAddress = new Uri(mainApiBaseUrl.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(10);
        });

        services.AddMemoryCache();

        services.AddSingleton<IPrinterLookupService, HttpPrinterLookupService>();
        services.AddSingleton<ICatalogServiceAdapter, HttpCatalogServiceAdapter>();

        return services;
    }
}
