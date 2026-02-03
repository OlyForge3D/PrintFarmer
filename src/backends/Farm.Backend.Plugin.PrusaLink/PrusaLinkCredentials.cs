namespace Farm.Backend.Plugin.PrusaLink;

/// <summary>
/// Credentials for PrusaLink authentication.
/// Supports both API key authentication and HTTP Digest Authentication.
/// </summary>
/// <param name="ApiKey">Optional API key for X-Api-Key header authentication</param>
/// <param name="Username">Optional username for HTTP Digest Authentication</param>
/// <param name="Password">Optional password for HTTP Digest Authentication</param>
public record PrusaLinkCredentials(
    string? ApiKey = null,
    string? Username = null,
    string? Password = null)
{
    /// <summary>
    /// Gets whether API key authentication is available.
    /// </summary>
    public bool HasApiKey => !string.IsNullOrWhiteSpace(ApiKey);

    /// <summary>
    /// Gets whether HTTP Digest Authentication credentials are available.
    /// </summary>
    public bool HasDigestAuth => !string.IsNullOrWhiteSpace(Username) && !string.IsNullOrWhiteSpace(Password);

    /// <summary>
    /// Gets whether any form of authentication is available.
    /// </summary>
    public bool HasAnyAuth => HasApiKey || HasDigestAuth;

    /// <summary>
    /// Creates credentials with only an API key.
    /// </summary>
    public static PrusaLinkCredentials FromApiKey(string? apiKey)
        => new(ApiKey: apiKey);

    /// <summary>
    /// Creates credentials with only digest authentication.
    /// </summary>
    public static PrusaLinkCredentials FromDigestAuth(string username, string password)
        => new(Username: username, Password: password);

    /// <summary>
    /// Creates credentials with both API key and digest authentication.
    /// </summary>
    public static PrusaLinkCredentials FromBoth(string? apiKey, string username, string password)
        => new(ApiKey: apiKey, Username: username, Password: password);
}
