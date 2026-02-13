using Farm.Backend.Plugin.Core;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Farm.Backend.Plugin.PrusaLink;

/// <summary>
/// Plugin descriptor for PrusaLink backend client support.
/// All PrusaLink-specific implementations (client, status client, services) are contained within this plugin.
/// </summary>
public class PrusaLinkBackendPlugin : IExtendedBackendPlugin
{
    /// <summary>
    /// Gets the unique identifier for this backend client plugin.
    /// </summary>
    public string BackendType => "prusalink";

    /// <summary>
    /// Gets a human-readable display name for this backend.
    /// </summary>
    public string DisplayName => "PrusaLink";

    /// <summary>
    /// Gets a description of this backend client.
    /// </summary>
    public string Description => "Plugin for Prusa printers via PrusaLink API";

    /// <summary>
    /// Gets the backend client type provided by this plugin.
    /// </summary>
    public Type ClientType => typeof(PrusaLinkClient);

    /// <summary>
    /// Gets the backend client interface type that this plugin implements.
    /// </summary>
    public Type ClientInterfaceType => typeof(IPrusaLinkClient);

    /// <summary>
    /// Gets the status client type for real-time printer status updates.
    /// </summary>
    public Type? StatusClientType => typeof(PrusaLinkStatusClient);

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
        // Register the PrusaLink API client (internal helper)
        services.AddScoped<IPrusaLinkApiClient, PrusaLinkApiClient>();

        // Register the PrusaLink client interface with its implementation
        // Using AddScoped because it needs fresh instances per request
        services.AddScoped<IPrusaLinkClient>(provider =>
        {
            IHttpClientFactory httpClientFactory = provider.GetRequiredService<IHttpClientFactory>();
            HttpClient httpClient = httpClientFactory.CreateClient();

            // PrusaLink uses HttpClient.Timeout as the primary timeout mechanism (no per-request CTS).
            // Use the ceiling from BackendTimeoutSettings so uploads have enough headroom.
            var timeouts = provider.GetRequiredService<IOptions<Farm.Infrastructure.Settings.BackendTimeoutSettings>>().Value;
            httpClient.Timeout = timeouts.HttpClientTimeoutCeiling;
            IUnifiedLoggingService? logger = provider.GetService<IUnifiedLoggingService>();
            return new PrusaLinkClient(httpClient, logger);
        });

        // NOTE: Status clients are NOT registered in DI container. They are instantiated
        // on-demand by PrinterStatusClientFactory which properly handles their dependencies
        // on scoped services.

        // Register the PrusaLinkPollingService hosted service
        // This service polls PrusaLink printers for status updates every 5 seconds
        services.AddSingleton<IHostedService, PrusaLinkPollingService>();
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
            typeof(ISupportsFileUpload),
            typeof(ISupportsFileDelete),
            typeof(ISupportsStartPrint),
            typeof(ISupportsCamera),
            typeof(ISupportsPrinterInformation),
            typeof(ISupportsControlOperations),
            typeof(ISupportsMovement),
            typeof(ISupportsTemperatureControl)
        };
    }

    /// <summary>
    /// Gets optional configuration sections that this backend requires.
    /// </summary>
    /// <returns>An enumerable of configuration section names this backend uses.</returns>
    public IEnumerable<string> GetConfigurationSections()
    {
        return new[] { "PrusaLink" };
    }
}
