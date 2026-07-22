using Farm.Backend.Plugin.Core;
using Farm.Infrastructure.Contracts.Printers.Moonraker;
using Farm.Infrastructure.Discovery;
using Farm.Infrastructure.Services.Printers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Farm.Backend.Plugin.Moonraker;

/// <summary>
/// Plugin descriptor for Moonraker backend client support.
/// All Moonraker-specific implementations (client, status client, services) are contained within this plugin.
/// </summary>
public class MoonrakerBackendPlugin : IExtendedBackendPlugin
{
    /// <summary>
    /// Gets the unique identifier for this backend client plugin.
    /// </summary>
    public string BackendType => "moonraker";

    /// <summary>
    /// Gets a human-readable display name for this backend.
    /// </summary>
    public string DisplayName => "Moonraker";

    /// <summary>
    /// Gets a description of this backend client.
    /// </summary>
    public string Description => "Plugin for Klipper firmware via Moonraker API";

    /// <summary>
    /// Gets the backend client type provided by this plugin.
    /// </summary>
    public Type ClientType => typeof(MoonrakerClient);

    /// <summary>
    /// Gets the backend client interface type that this plugin implements.
    /// </summary>
    public Type ClientInterfaceType => typeof(IMoonrakerClient);

    /// <summary>
    /// Gets the status client type for real-time printer status updates.
    /// </summary>
    public Type? StatusClientType => typeof(MoonrakerStatusClient);

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
        services.AddScoped<IPrinterCameraProbe, MoonrakerPrinterCameraProbe>();
        services.AddSingleton<IMoonrakerJsonRpcClient, MoonrakerJsonRpcClient>();
        services.AddSingleton<ISnapmakerU1CameraMonitorManager, SnapmakerU1CameraMonitorManager>();

        // Register the Moonraker client interface with its implementation
        services.AddScoped<IMoonrakerClient>(provider =>
        {
            IHttpClientFactory httpClientFactory = provider.GetRequiredService<IHttpClientFactory>();
            HttpClient httpClient = httpClientFactory.CreateClient();

            // IMPORTANT: do not rely on HttpClient.Timeout for Moonraker.
            // MoonrakerClient applies per-request cancellation timeouts via linked CTS
            // using values from BackendTimeoutSettings.
            var timeouts = provider.GetRequiredService<IOptions<Farm.Infrastructure.Settings.BackendTimeoutSettings>>().Value;
            httpClient.Timeout = timeouts.HttpClientTimeoutCeiling;
            ILogger<MoonrakerClient> logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger<MoonrakerClient>();
            ISnapmakerU1CameraMonitorManager monitorManager = provider.GetRequiredService<ISnapmakerU1CameraMonitorManager>();
            return new MoonrakerClient(httpClient, logger, timeouts, monitorManager);
        });

        // NOTE: Status clients are NOT registered in DI container. They are instantiated
        // on-demand by PrinterStatusClientFactory which properly handles their dependencies
        // on scoped services.

        // Register the MoonrakerSubscriptionService hosted service
        // This service manages real-time WebSocket subscriptions for printer status updates
        // and updates the shared cache before broadcasting via SignalR
        services.AddSingleton<MoonrakerSubscriptionService>();
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<MoonrakerSubscriptionService>());
        services.AddSingleton<IPrinterConnectionHealthProvider>(sp => sp.GetRequiredService<MoonrakerSubscriptionService>());
        services.AddSingleton<IPrinterStatusRefreshService>(sp => sp.GetRequiredService<MoonrakerSubscriptionService>());
    }

    /// <summary>
    /// Gets optional configuration sections that this backend requires.
    /// </summary>
    /// <returns>An enumerable of configuration section names this backend uses.</returns>
    public IEnumerable<string> GetConfigurationSections()
    {
        return new[] { "Moonraker" };
    }
}
