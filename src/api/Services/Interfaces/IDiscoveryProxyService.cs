using Farm.Infrastructure;

namespace Farm.Web.Api.Services.Interfaces;

/// <summary>
/// Service for proxying discovery requests to the printer-discovery microservice.
/// Enables the API to act as a gateway for network discovery operations.
/// </summary>
public interface IDiscoveryProxyService
{
    /// <summary>
    /// Starts a discovery stream by forwarding the request to the printer-discovery microservice.
    /// Returns a session ID that can be used to receive discovery progress via SignalR.
    /// </summary>
    /// <param name="backends">Optional list of backends to filter discovery</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Session ID and message from the discovery service</returns>
    Task<DiscoveryStreamResponse> StartDiscoveryStreamAsync(
        IReadOnlyList<PrinterBackend>? backends = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancels an active discovery stream.
    /// </summary>
    /// <param name="sessionId">The session ID to cancel</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Cancellation result message</returns>
    Task<DiscoveryCancelResponse> CancelDiscoveryStreamAsync(
        string sessionId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Response from starting a discovery stream.
/// </summary>
public record DiscoveryStreamResponse(string SessionId, string Message);

/// <summary>
/// Response from cancelling a discovery stream.
/// </summary>
public record DiscoveryCancelResponse(string Message);
