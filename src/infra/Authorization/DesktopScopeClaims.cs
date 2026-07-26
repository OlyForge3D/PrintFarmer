namespace Farm.Infrastructure.Authorization;

/// <summary>
/// Claim type/value constants used by the Desktop API-key exchange (issue #838).
/// Centralized here so the JWT issuer (main API) and the JWT consumers (main API and
/// slicer host) never drift out of sync on claim naming.
/// </summary>
public static class DesktopScopeClaims
{
    /// <summary>
    /// Claim type marking a token as a scoped Desktop-exchange token (as opposed to a
    /// normal login/session token). Only present on tokens issued by the exchange endpoint.
    /// </summary>
    public const string TokenUse = "token_use";

    /// <summary>
    /// The <see cref="TokenUse"/> value assigned to Desktop-exchange tokens.
    /// </summary>
    public const string DesktopExchangeTokenUse = "desktop_exchange";

    /// <summary>
    /// Claim type carrying one granted <see cref="Domain.ApiKeyScope"/> flag per claim
    /// instance (e.g. "ModelRead", "ModelWrite", "LibrarySync").
    /// </summary>
    public const string Scope = "scope";

    /// <summary>
    /// Claim type carrying the originating <see cref="Domain.ApiKey"/>'s identifier, for
    /// audit traceability. Not a secret - safe to include and to log.
    /// </summary>
    public const string ApiKeyId = "api_key_id";
}
