namespace Farm.Backend.Plugin.Core;

using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Base interface for all backend client plugins.
/// </summary>
public interface IBackendClientPlugin
{
    /// <summary>
    /// Gets the unique identifier for this backend client plugin.
    /// </summary>
    string BackendType { get; }

    /// <summary>
    /// Gets a human-readable display name for this backend.
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// Gets a description of this backend client.
    /// </summary>
    string Description { get; }

    /// <summary>
    /// Gets the backend client type provided by this plugin.
    /// </summary>
    Type ClientType { get; }

    /// <summary>
    /// Gets the backend client interface type that this plugin implements.
    /// </summary>
    Type ClientInterfaceType { get; }

    /// <summary>
    /// Gets the version of this plugin.
    /// </summary>
    Version Version { get; }

    /// <summary>
    /// Registers the backend client with the dependency injection container.
    /// </summary>
    /// <param name="services">The service collection to register with.</param>
    void RegisterServices(IServiceCollection services);

    /// <summary>
    /// Gets the capabilities supported by this backend client.
    /// </summary>
    /// <returns>An enumerable of capability interface types.</returns>
    IEnumerable<Type> GetCapabilities();
}
