using Microsoft.Extensions.DependencyInjection;

namespace Farm.Backend.Plugin.Core;

/// <summary>
/// Backend-advertised telemetry timing used by physical safety gates.
/// </summary>
/// <param name="ExpectedUpdateInterval">Normal status update cadence.</param>
/// <param name="MaximumObservationAge">Oldest observation safe for physical actuation.</param>
public sealed record BackendTelemetryCadence(
    TimeSpan ExpectedUpdateInterval,
    TimeSpan MaximumObservationAge);

/// <summary>
/// Extended interface for backend plugins that provide additional functionality beyond basic client support.
/// This interface allows plugins to register custom services, status clients, and other components.
/// Plugins that implement this interface can provide more sophisticated backend implementations.
/// </summary>
public interface IExtendedBackendPlugin : IBackendClientPlugin
{
    /// <summary>
    /// Gets the status client type that this plugin provides for real-time printer status updates.
    /// If the plugin doesn't have a dedicated status client, return null.
    /// </summary>
    Type? StatusClientType { get; }

    /// <summary>
    /// Gets the interface type for the status client.
    /// If the plugin doesn't have a dedicated status client, return null.
    /// </summary>
    Type? StatusClientInterfaceType { get; }

    /// <summary>
    /// Gets the backend's actual status cadence and physical-safety freshness SLA.
    /// A missing advertisement makes physical dispatch fail closed.
    /// </summary>
    BackendTelemetryCadence? TelemetryCadence => null;

    /// <summary>
    /// Registers additional services beyond the basic client implementation.
    /// This is called during dependency injection setup and allows plugins to register
    /// background services, polling services, custom clients, and other infrastructure.
    /// </summary>
    /// <param name="services">The service collection to register with.</param>
    /// <remarks>
    /// The base RegisterServices method should register the core client and its interface.
    /// This method should register additional services like:
    /// - Background services (polling, subscriptions)
    /// - Additional HTTP clients or API clients
    /// - Utilities specific to this backend
    /// - Status clients and related services
    /// </remarks>
    void RegisterAdditionalServices(IServiceCollection services);

    /// <summary>
    /// Gets optional configuration sections that this backend requires.
    /// Can be used for validation or documentation purposes.
    /// </summary>
    /// <returns>An enumerable of configuration section names this backend uses.</returns>
    IEnumerable<string> GetConfigurationSections()
    {
        return [];
    }
}
