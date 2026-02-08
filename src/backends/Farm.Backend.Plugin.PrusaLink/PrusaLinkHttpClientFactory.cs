namespace Farm.Backend.Plugin.PrusaLink;

/// <summary>
/// Factory for creating HttpClient instances configured for PrusaLink authentication.
/// Uses HTTP Digest Authentication with credential Username and Password.
/// </summary>
public interface IPrusaLinkHttpClientFactory
{
    /// <summary>
    /// Creates an HttpClient configured for PrusaLink with optional digest authentication.
    /// </summary>
    /// <param name="username">Username for HTTP Digest Authentication (optional)</param>
    /// <param name="password">Password for HTTP Digest Authentication (optional)</param>
    /// <returns>An HttpClient configured with the appropriate authentication handler</returns>
    HttpClient CreateClient(string? username = null, string? password = null);
}

/// <summary>
/// Default implementation of IPrusaLinkHttpClientFactory.
/// Creates HttpClient instances with DigestAuthHandler when credentials are provided.
/// </summary>
public class PrusaLinkHttpClientFactory : IPrusaLinkHttpClientFactory
{
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly TimeSpan _timeout;

    /// <summary>
    /// Creates a new factory with optional IHttpClientFactory integration.
    /// </summary>
    /// <param name="httpClientFactory">Optional IHttpClientFactory for underlying handler creation</param>
    /// <param name="timeout">Request timeout (defaults to 30 seconds)</param>
    public PrusaLinkHttpClientFactory(IHttpClientFactory? httpClientFactory = null, TimeSpan? timeout = null)
    {
        _httpClientFactory = httpClientFactory;
        _timeout = timeout ?? TimeSpan.FromSeconds(30);
    }

    /// <inheritdoc />
    public HttpClient CreateClient(string? username = null, string? password = null)
    {
        HttpClient client;

        if (!string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(password))
        {
            // Create client with digest auth handler
            DigestAuthHandler handler = new(username, password);
            client = new HttpClient(handler, disposeHandler: true);
        }
        else if (_httpClientFactory != null)
        {
            // Use the factory's default client
            client = _httpClientFactory.CreateClient("PrusaLink");
        }
        else
        {
            // Create a basic client
            client = new HttpClient();
        }

        client.Timeout = _timeout;
        return client;
    }
}
