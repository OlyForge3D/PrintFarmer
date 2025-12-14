namespace Farm.Backend.Plugin.Sdcp;

using Farm.Backend.Plugin.Core;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Plugin descriptor for SDCP (Simple Data Communication Protocol) backend client support.
/// The actual client implementation lives in Farm.Web.Api.Services.
/// This plugin now supports extended functionality for status clients and additional services.
/// </summary>
public class SdcpBackendPlugin : IExtendedBackendPlugin
{
    private const string ClientTypeName = "Farm.Web.Api.Services.SdcpClient";
    private const string InterfaceTypeName = "Farm.Web.Api.Services.Interfaces.ISdcpClient";
    private const string StatusClientTypeName = "Farm.Web.Api.Services.Printers.SdcpStatusClient";
    private const string StatusClientInterfaceTypeName = "Farm.Web.Api.Services.Printers.IPrinterStatusClient";

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
            System.Diagnostics.Debug.WriteLine($"Error registering status client for SDCP: {ex.Message}");
        }

        // Register the HTTP client for SDCP backend
        // This allows the SdcpClient to make HTTP requests with proper timeout handling
        try
        {
            var sdcpClientInterfaceTypeName = "Farm.Web.Api.Services.Interfaces.ISdcpClient";
            var sdcpClientTypeName = "Farm.Web.Api.Services.SdcpClient";
            
            var clientInterfaceType = GetTypeFromApi(sdcpClientInterfaceTypeName);
            var clientType = GetTypeFromApi(sdcpClientTypeName);
            
            // Register HTTP client with 10-second timeout
            services.AddHttpClientFromPlugin(clientInterfaceType, clientType, client =>
            {
                client.Timeout = TimeSpan.FromSeconds(10);
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error registering HTTP client for SDCP: {ex.Message}");
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
            "ISupportsFileUpload",
            "ISupportsStartPrint",
            "ISupportsHistory",
            "ISupportsTemperatureControl",
            "ISupportsMovement",
            "ISupportsControlOperations"
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
        return new[] { "Sdcp" };
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
