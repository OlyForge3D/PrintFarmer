namespace Farm.Infrastructure.Domain;

/// <summary>
/// Strongly-typed credential container for printer backend authentication.
/// Backend clients can access whatever properties they need without parsing strings.
/// </summary>
/// <remarks>
/// Usage by backend type:
/// - PrusaLink: Uses Username + Password for HTTP Digest Auth
/// - OctoPrint: Uses ApiKey for X-Api-Key header
/// - Moonraker: Typically no auth required, but ApiKey supported
/// - SDCP: May use ApiKey or no auth
/// </remarks>
public sealed class PrinterCredential
{
    /// <summary>
    /// API key for backends that use key-based authentication (OctoPrint, some Moonraker setups).
    /// </summary>
    public string? ApiKey { get; init; }

    /// <summary>
    /// Username for HTTP Digest Authentication (PrusaLink).
    /// </summary>
    public string? Username { get; init; }

    /// <summary>
    /// Password for HTTP Digest Authentication (PrusaLink).
    /// </summary>
    public string? Password { get; init; }

    /// <summary>
    /// Returns true if this credential has digest auth credentials (username and password).
    /// </summary>
    public bool HasDigestAuth => !string.IsNullOrEmpty(Username) && !string.IsNullOrEmpty(Password);

    /// <summary>
    /// Returns true if this credential has an API key.
    /// </summary>
    public bool HasApiKey => !string.IsNullOrEmpty(ApiKey);

    /// <summary>
    /// Returns true if this credential has any authentication method available.
    /// </summary>
    public bool HasAnyAuth => HasDigestAuth || HasApiKey;

    /// <summary>
    /// Creates an empty credential (no authentication).
    /// </summary>
    public static PrinterCredential Empty => new();

    /// <summary>
    /// Creates a credential with only an API key.
    /// </summary>
    public static PrinterCredential FromApiKey(string apiKey) => new() { ApiKey = apiKey };

    /// <summary>
    /// Creates a credential with username and password for digest auth.
    /// </summary>
    public static PrinterCredential FromDigestAuth(string username, string password) =>
        new() { Username = username, Password = password };

    /// <summary>
    /// Creates a credential with all available authentication methods.
    /// </summary>
    public static PrinterCredential FromAll(string? apiKey, string? username, string? password) =>
        new() { ApiKey = apiKey, Username = username, Password = password };

    public override string ToString()
    {
        if (HasDigestAuth && HasApiKey)
        {
            return $"[DigestAuth:{Username}+ApiKey]";
        }

        if (HasDigestAuth)
        {
            return $"[DigestAuth:{Username}]";
        }

        if (HasApiKey)
        {
            return "[ApiKey]";
        }

        return "[NoAuth]";
    }
}
