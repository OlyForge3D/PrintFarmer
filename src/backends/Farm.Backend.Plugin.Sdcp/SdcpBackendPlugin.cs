using Farm.Backend.Plugin.Core;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Farm.Backend.Plugin.Sdcp;

/// <summary>
/// Plugin descriptor for SDCP (Simple Data Communication Protocol) backend client support.
/// All SDCP-specific implementations (client, status client) are contained within this plugin.
/// </summary>
public class SdcpBackendPlugin : IExtendedBackendPlugin
{
    /// <summary>
    /// Gets the unique identifier for this backend client plugin.
    /// </summary>
    public string BackendType => "sdcp";

    /// <summary>
    /// Gets a human-readable display name for this backend.
    /// </summary>
    public string DisplayName => "SDCP";

    /// <summary>
    /// Gets a description of this backend client.
    /// </summary>
    public string Description => "Plugin for SDCP (Simple Data Communication Protocol) 3D printers";

    /// <summary>
    /// Gets the backend client type provided by this plugin.
    /// </summary>
    public Type ClientType => typeof(SdcpClient);

    /// <summary>
    /// Gets the backend client interface type that this plugin implements.
    /// </summary>
    public Type ClientInterfaceType => typeof(ISdcpClient);

    /// <summary>
    /// Gets the status client type for real-time printer status updates.
    /// </summary>
    public Type? StatusClientType => typeof(SdcpStatusClient);

    /// <summary>
    /// Gets the interface type for the status client.
    /// </summary>
    public Type? StatusClientInterfaceType => typeof(IPrinterStatusClient);

    /// <summary>
    /// Gets the version of this plugin.
    /// </summary>
    public Version Version => new(1, 0, 0);

    /// <summary>
    /// Registers the backend client with the dependency injection container.
    /// </summary>
    /// <param name="services">The service collection to register with.</param>
    public void RegisterServices(IServiceCollection services)
    {
        // Services are registered by the API itself; this plugin just describes the capability
        // The extended plugin interface allows for additional service registration via RegisterAdditionalServices
    }

    /// <summary>
    /// Registers additional services beyond the basic client implementation.
    /// </summary>
    /// <param name="services">The service collection to register with.</param>
    public void RegisterAdditionalServices(IServiceCollection services)
    {
        // Register the SDCP client interface with its implementation
        // Using AddScoped because HTTP clients need fresh instances per request scope
        services.AddScoped<ISdcpClient>(provider =>
        {
            IHttpClientFactory httpClientFactory = provider.GetRequiredService<IHttpClientFactory>();
            HttpClient httpClient = httpClientFactory.CreateClient();
            httpClient.Timeout = TimeSpan.FromSeconds(10);
            IUnifiedLoggingService logger = provider.GetRequiredService<IUnifiedLoggingService>();
            return new SdcpClient(httpClient, logger);
        });

        // NOTE: Status clients are NOT registered in DI container. They are instantiated
        // on-demand by PrinterStatusClientFactory which properly handles their dependencies
        // on scoped services.

        // Register the SdcpPollingService hosted service
        // This service polls SDCP printers for status updates every 5 seconds
        services.AddSingleton<IHostedService, SdcpPollingService>();
    }

    /// <summary>
    /// Gets the capabilities supported by this backend client.
    /// </summary>
    /// <returns>An enumerable of capability interface types.</returns>
    public IEnumerable<Type> GetCapabilities()
    {
        return new[]
        {
            typeof(ISupportsFileList),
            typeof(ISupportsHistory)
        };
    }

    /// <summary>
    /// Gets optional configuration sections that this backend requires.
    /// </summary>
    /// <returns>An enumerable of configuration section names this backend uses.</returns>
    public IEnumerable<string> GetConfigurationSections()
    {
        return new[] { "SDCP" };
    }
}
