namespace Farm.Infrastructure.Network;

/// <summary>
/// Abstraction for URL rewriting so consumers can depend on an interface.
/// </summary>
public interface INetworkUrlRewriteService
{
    /// <summary>
    /// Rewrites a URL to make it accessible from the current runtime environment.
    /// </summary>
    /// <param name="originalUrl">The original URL to rewrite.</param>
    /// <param name="serviceName">Optional service name for context-specific rewriting.</param>
    string RewriteUrl(string originalUrl, string? serviceName = null);
}
