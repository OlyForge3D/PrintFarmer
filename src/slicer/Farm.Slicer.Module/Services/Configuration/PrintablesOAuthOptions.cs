namespace Farm.Slicer.Module.Services.Configuration;

/// <summary>
/// Runtime options for Printables OAuth2 account linking.
/// </summary>
public sealed class PrintablesOAuthOptions
{
    /// <summary>Configuration section key.</summary>
    public const string SectionName = "PrintablesOAuth";

    /// <summary>OAuth2 authorization endpoint.</summary>
    public string AuthorizationEndpoint { get; set; } = "https://www.printables.com/oauth2/authorize";

    /// <summary>OAuth2 token endpoint.</summary>
    public string TokenEndpoint { get; set; } = "https://www.printables.com/oauth2/token";

    /// <summary>OAuth2 client ID.</summary>
    public string? ClientId { get; set; }

    /// <summary>OAuth2 client secret. Keep this server-side only.</summary>
    public string? ClientSecret { get; set; }

    /// <summary>Redirect URI registered with Printables.</summary>
    public string? RedirectUri { get; set; }

    /// <summary>Requested scopes for account linking.</summary>
    public string Scope { get; set; } = "likes history";

    /// <summary>Authorization request state TTL in seconds.</summary>
    public int StateTtlSeconds { get; set; } = 600;

    /// <summary>
    /// Enables guarded liked/history query endpoints after successful token linking.
    /// </summary>
    public bool EnableAuthenticatedQueries { get; set; } = false;
}
