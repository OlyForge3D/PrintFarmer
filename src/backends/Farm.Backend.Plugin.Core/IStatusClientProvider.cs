using Microsoft.Extensions.DependencyInjection;

namespace Farm.Backend.Plugin.Core;

/// <summary>
/// Interface for plugins that need to register status clients.
/// Provides standardized way for plugins to instantiate and register their status clients.
/// </summary>
public interface IStatusClientProvider
{
    /// <summary>
    /// Gets the type of the status client implementation.
    /// </summary>
    Type StatusClientType { get; }

    /// <summary>
    /// Gets the interface type that the status client implements.
    /// </summary>
    Type StatusClientInterfaceType { get; }

    /// <summary>
    /// Registers the status client with the service collection.
    /// </summary>
    /// <param name="services">The service collection.</param>
    void RegisterStatusClient(IServiceCollection services);
}
