using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Farm.Backend.Plugin.Core.Extensions;

/// <summary>
/// Extension methods for configuring backend client HTTP clients in the service collection.
/// Consolidates common HTTP client configuration patterns across all backend plugins.
/// </summary>
public static class BackendClientServiceCollectionExtensions
{
    /// <summary>
    /// Registers a backend client with configured HTTP client timeout.
    /// Standardizes HTTP client creation across all backend implementations (Moonraker, PrusaLink, OctoPrint, SDCP, etc.).
    /// </summary>
    /// <typeparam name="TInterface">The client interface to register (e.g., IMoonrakerClient)</typeparam>
    /// <typeparam name="TImplementation">The client implementation (e.g., MoonrakerClient)</typeparam>
    /// <param name="services">The service collection to register with</param>
    /// <param name="clientFactory">Factory function to create the client instance with configured HttpClient</param>
    /// <param name="timeoutSeconds">HTTP client timeout in seconds (default: 300). Set high because
    /// individual operations should use per-request CancellationTokenSource timeouts; the HttpClient
    /// timeout is only a safety net to avoid permanently hung connections.</param>
    public static void AddBackendClient<TInterface, TImplementation>(
        this IServiceCollection services,
        Func<HttpClient, TImplementation> clientFactory,
        int timeoutSeconds = 300)
        where TInterface : class
        where TImplementation : class, TInterface
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(clientFactory);

        services.AddScoped<TInterface>(provider =>
        {
            IHttpClientFactory httpClientFactory = provider.GetRequiredService<IHttpClientFactory>();
            HttpClient httpClient = httpClientFactory.CreateClient();
            httpClient.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
            return clientFactory(httpClient);
        });
    }

    /// <summary>
    /// Registers a backend client with configured HTTP client and logger.
    /// Standardizes HTTP client creation for backend implementations that require logging.
    /// </summary>
    /// <typeparam name="TInterface">The client interface to register</typeparam>
    /// <typeparam name="TImplementation">The client implementation</typeparam>
    /// <typeparam name="TLogger">The logger type for dependency injection</typeparam>
    /// <param name="services">The service collection to register with</param>
    /// <param name="clientFactory">Factory function to create the client with HttpClient and Logger</param>
    /// <param name="timeoutSeconds">HTTP client timeout in seconds (default: 300). Set high because
    /// individual operations should use per-request CancellationTokenSource timeouts; the HttpClient
    /// timeout is only a safety net to avoid permanently hung connections.</param>
    public static void AddBackendClientWithLogging<TInterface, TImplementation, TLogger>(
        this IServiceCollection services,
        Func<HttpClient, Microsoft.Extensions.Logging.ILogger<TLogger>?, TImplementation> clientFactory,
        int timeoutSeconds = 300)
        where TInterface : class
        where TImplementation : class, TInterface
        where TLogger : class
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(clientFactory);

        services.AddScoped<TInterface>(provider =>
        {
            IHttpClientFactory httpClientFactory = provider.GetRequiredService<IHttpClientFactory>();
            HttpClient httpClient = httpClientFactory.CreateClient();
            httpClient.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
            ILogger<TLogger>? logger = provider.GetService<Microsoft.Extensions.Logging.ILogger<TLogger>>();
            return clientFactory(httpClient, logger);
        });
    }
}
