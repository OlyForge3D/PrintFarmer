using Microsoft.AspNetCore.Http;

namespace Farm.Slicer.Module.Api;

/// <summary>
/// Helpers for authenticating SignalR hub connections served by the slicer host.
/// </summary>
public static class SlicerHubAuth
{
    /// <summary>
    /// Resolves the JWT for a SignalR hub request from the <c>?access_token=</c> query parameter.
    /// </summary>
    /// <remarks>
    /// SignalR's WebSocket and Server-Sent-Events transports cannot set the <c>Authorization</c>
    /// header on the browser handshake, so the client sends the JWT as a query-string parameter.
    /// JWT bearer auth only reads the header by default, so the WebSocket upgrade to an
    /// <c>[Authorize]</c> hub (e.g. <c>/hubs/slicers</c>) is rejected with 401 and SignalR silently
    /// downgrades to long-polling. Wiring this into <c>JwtBearerEvents.OnMessageReceived</c> restores
    /// the WebSocket transport. The negotiate POST still authenticates via the header.
    /// </remarks>
    /// <param name="request">The incoming request.</param>
    /// <returns>
    /// The query access token when the request targets a hub path (<c>/hubs/...</c>) and a token is
    /// present; otherwise <see langword="null"/> so the default header-based resolution is used.
    /// </returns>
    public static string? ResolveHubAccessToken(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        string accessToken = request.Query["access_token"].ToString();
        if (!string.IsNullOrEmpty(accessToken)
            && request.Path.StartsWithSegments("/hubs", StringComparison.OrdinalIgnoreCase))
        {
            return accessToken;
        }

        return null;
    }
}
