namespace Farm.Backend.Plugin.Moonraker;

using Farm.Backend.Plugin.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

/// <summary>
/// Plugin descriptor for Moonraker backend client support.
/// The actual client implementation lives in Farm.Web.Api.Services.
/// This plugin now supports extended functionality for status clients and additional services.
/// </summary>
public class MoonrakerBackendPlugin : IExtendedBackendPlugin
{
    private const string ClientTypeName = "Farm.Web.Api.Services.MoonrakerClient";
    private const string InterfaceTypeName = "Farm.Web.Api.Services.Interfaces.IMoonrakerClient";
    private const string StatusClientTypeName = "Farm.Web.Api.Services.Printers.MoonrakerStatusClient";
    private const string StatusClientInterfaceTypeName = "Farm.Web.Api.Services.Printers.IPrinterStatusClient";

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
    public Type ClientType => GetTypeFromApi(ClientTypeName);

    /// <summary>
    /// Gets the backend client interface type that this plugin implements.
    /// </summary>
    public Type ClientInterfaceType => GetTypeFromApi(InterfaceTypeName);

    /// <summary>
    /// Gets the status client type for real-time printer status updates.
    /// </summary>
    public Type? StatusClientType
    {
        get
        {
            try
            {
                return GetTypeFromApi(StatusClientTypeName);
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Gets the status client interface type.
    /// </summary>
    public Type? StatusClientInterfaceType
    {
        get
        {
            try
            {
                return GetTypeFromApi(StatusClientInterfaceTypeName);
            }
            catch
            {
                return null;
            }
        }
    }

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
        Console.WriteLine("[Moonraker Plugin] RegisterAdditionalServices called");
        Console.Out.Flush();
        
        // Register the status client for this backend
        // Status clients are instantiated on-demand by the PrinterStatusClientFactory
        try
        {
            // Get the status client type
            if (StatusClientType != null && StatusClientInterfaceType != null)
            {
                // Register the status client with the interface
                // We use AddSingleton because status clients are stateless (they use injected clients)
                var statusClientType = StatusClientType;
                var statusClientInterfaceType = StatusClientInterfaceType;
                
                services.AddSingleton(statusClientInterfaceType, serviceProvider =>
                {
                    // Dynamically create an instance of the status client using reflection
                    // The status client constructor should take dependencies from the service provider
                    var constructors = statusClientType.GetConstructors();
                    if (constructors.Length > 0)
                    {
                        var constructor = constructors[0];
                        var parameters = constructor.GetParameters();
                        var paramInstances = new object?[parameters.Length];
                        
                        for (int i = 0; i < parameters.Length; i++)
                        {
                            paramInstances[i] = serviceProvider.GetService(parameters[i].ParameterType);
                        }
                        
                        return Activator.CreateInstance(statusClientType, paramInstances)
                            ?? throw new InvalidOperationException($"Failed to create instance of {statusClientType.Name}");
                    }
                    
                    throw new InvalidOperationException($"No public constructors found for {statusClientType.Name}");
                });
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error registering status client for Moonraker: {ex.Message}");
        }

        // Register the MoonrakerSubscriptionService hosted service
        // This service manages real-time WebSocket subscriptions for printer status updates
        try
        {
            var subscriptionServiceTypeName = "Farm.Web.Api.Services.MoonrakerSubscriptionService";
            var subscriptionServiceType = GetTypeFromApi(subscriptionServiceTypeName);
            
            // Register as singleton that implements IHostedService
            services.AddSingleton(typeof(IHostedService), sp =>
            {
                var constructors = subscriptionServiceType.GetConstructors();
                if (constructors.Length > 0)
                {
                    var constructor = constructors[0];
                    var parameters = constructor.GetParameters();
                    var paramInstances = new object?[parameters.Length];
                    
                    for (int i = 0; i < parameters.Length; i++)
                    {
                        var paramType = parameters[i].ParameterType;
                        paramInstances[i] = sp.GetService(paramType);
                    }
                    
                    return Activator.CreateInstance(subscriptionServiceType, paramInstances)
                        ?? throw new InvalidOperationException($"Failed to create instance of {subscriptionServiceType.Name}");
                }
                
                throw new InvalidOperationException($"No public constructors found for {subscriptionServiceType.Name}");
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Moonraker Plugin] Error registering MoonrakerSubscriptionService: {ex.Message}");
            Console.WriteLine($"[Moonraker Plugin] Stack: {ex.StackTrace}");
        }

        // Register the HTTP client for Moonraker backend
        // This allows the MoonrakerClient to make HTTP requests with proper timeout handling
        try
        {
            Console.WriteLine("[Moonraker Plugin] Registering HTTP client...");
            Console.Out.Flush();
            
            var moonrakerClientInterfaceTypeName = "Farm.Web.Api.Services.Interfaces.IMoonrakerClient";
            var moonrakerClientTypeName = "Farm.Web.Api.Services.MoonrakerClient";
            
            try
            {
                var clientInterfaceType = GetTypeFromApi(moonrakerClientInterfaceTypeName);
                Console.WriteLine($"[Moonraker Plugin]   Interface: {clientInterfaceType.FullName}");
                Console.Out.Flush();
                
                var clientType = GetTypeFromApi(moonrakerClientTypeName);
                Console.WriteLine($"[Moonraker Plugin]   Implementation: {clientType.FullName}");
                Console.Out.Flush();
                
                // Register HTTP client with 10-second timeout
                services.AddHttpClientFromPlugin(clientInterfaceType, clientType, client =>
                {
                    client.Timeout = TimeSpan.FromSeconds(10);
                });
                
                Console.WriteLine("[Moonraker Plugin] HTTP client registered successfully");
                Console.Out.Flush();
            }
            catch (Exception exType)
            {
                Console.WriteLine($"[Moonraker Plugin] ERROR getting types: {exType.GetType().Name}: {exType.Message}");
                Console.Out.Flush();
                throw;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error registering HTTP client for Moonraker: {ex.Message}");
        }
    }

    /// <summary>
    /// Gets the capabilities supported by this backend client.
    /// </summary>
    /// <returns>An enumerable of capability interface types.</returns>
    public IEnumerable<Type> GetCapabilities()
    {
        var capabilityNames = new[]
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
            .Select(name => GetTypeFromApi($"Farm.Web.Api.Services.Interfaces.{name}"))
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

    private static Type GetTypeFromApi(string fullyQualifiedTypeName)
    {
        var assembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "Farm.Web.Api");

        if (assembly == null)
            throw new InvalidOperationException($"Assembly Farm.Web.Api not found");

        var type = assembly.GetType(fullyQualifiedTypeName);
        if (type == null)
            throw new InvalidOperationException($"Type {fullyQualifiedTypeName} not found in Farm.Web.Api assembly");

        return type;
    }
}
