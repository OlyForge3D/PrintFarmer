namespace Farm.Backend.Plugin.OctoPrint;

using Farm.Backend.Plugin.Core;
using Farm.Infrastructure.Services.Printers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

/// <summary>
/// Plugin descriptor for OctoPrint backend client support.
/// All OctoPrint client implementation is now self-contained within this plugin.
/// </summary>
public class OctoPrintBackendPlugin : IExtendedBackendPlugin
{
    /// <summary>
    /// Gets the unique identifier for this backend client plugin.
    /// </summary>
    public string BackendType => "octoprint";

    /// <summary>
    /// Gets a human-readable display name for this backend.
    /// </summary>
    public string DisplayName => "OctoPrint";

    /// <summary>
    /// Gets a description of this backend client.
    /// </summary>
    public string Description => "Plugin for OctoPrint 3D printer management software";

    /// <summary>
    /// Gets the backend client type provided by this plugin.
    /// </summary>
    public Type ClientType => typeof(OctoPrintClient);

    /// <summary>
    /// Gets the backend client interface type that this plugin implements.
    /// </summary>
    public Type ClientInterfaceType => typeof(IOctoPrintClient);

    /// <summary>
    /// Gets the status client type for real-time printer status updates.
    /// </summary>
    public Type? StatusClientType => typeof(OctoPrintStatusClient);

    /// <summary>
    /// Gets the status client interface type.
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
        // Services are registered via RegisterAdditionalServices in the extended plugin pattern
    }

    /// <summary>
    /// Registers additional services for this backend plugin.
    /// </summary>
    /// <param name="services">The service collection to register with.</param>
    public void RegisterAdditionalServices(IServiceCollection services)
    {
        // Register the HTTP client for OctoPrint with proper timeout
        services.AddHttpClient<IOctoPrintClient, OctoPrintClient>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
        });

        // Register the status client as singleton for real-time status updates
        services.AddSingleton<IPrinterStatusClient, OctoPrintStatusClient>();

        // Register the polling service as hosted service
        services.AddSingleton<IHostedService, OctoPrintPollingService>();
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
            typeof(ISupportsFileDownload),
            typeof(ISupportsFileUpload),
            typeof(ISupportsStartPrint),
            typeof(ISupportsHistory),
            typeof(ISupportsTemperatureControl),
            typeof(ISupportsControlOperations),
            typeof(ISupportsCamera)
        };
    }

    /// <summary>
    /// Gets optional configuration sections that this backend requires.
    /// </summary>
    /// <returns>An enumerable of configuration section names this backend uses.</returns>
    public IEnumerable<string> GetConfigurationSections()
    {
        return new[] { "OctoPrint" };
    }
}
