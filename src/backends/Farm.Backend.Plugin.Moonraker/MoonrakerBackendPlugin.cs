using Farm.Backend.Plugin.Core;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

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
        // Register the Moonraker client interface with its implementation
        services.AddScoped<IMoonrakerClient>(provider =>
        {
            IHttpClientFactory httpClientFactory = provider.GetRequiredService<IHttpClientFactory>();
            HttpClient httpClient = httpClientFactory.CreateClient();
            httpClient.Timeout = TimeSpan.FromSeconds(10);
            IUnifiedLoggingService logger = provider.GetRequiredService<IUnifiedLoggingService>();
            return new MoonrakerClient(httpClient, logger);
        });

        // NOTE: Status clients are NOT registered in DI container. They are instantiated
        // on-demand by PrinterStatusClientFactory which properly handles their dependencies
        // on scoped services.

        // Register the Moonraker diagnostics service for API diagnostics endpoints
        services.AddScoped<IMoonrakerDiagnosticsService, MoonrakerDiagnosticsService>();

        // Register the MoonrakerSubscriptionService hosted service
        // This service manages real-time WebSocket subscriptions for printer status updates
        // and updates the shared cache before broadcasting via SignalR
        services.AddSingleton<IHostedService, MoonrakerSubscriptionService>();
    }

    /// <summary>
    /// Gets the capabilities supported by this backend client.
    /// </summary>
    /// <returns>An enumerable of capability interface types.</returns>
    public IEnumerable<Type> GetCapabilities()
    {
        string[] capabilityNames = new[]
        {
            "ISupportsFileList",
            "ISupportsFileDownload",
            "ISupportsFileUpload",
            "ISupportsStartPrint",
            "ISupportsHistory",
            "ISupportsTemperatureControl",
            "ISupportsMovement",
            "ISupportsControlOperations",
            "ISupportsCamera",
            "ISupportsFileMetadata",
            "ISupportsPrinterInformation"
        };

        return capabilityNames
            .Select(name =>
            {
                Type? type = typeof(ISupportsFileList).Assembly.GetType($"Farm.Infrastructure.Services.Printers.{name}");
                return type;
            })
            .Where(t => t != null)
            .Cast<Type>()
            .ToList();
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
