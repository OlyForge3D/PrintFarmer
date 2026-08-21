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

    /// <summary>
    /// True when <paramref name="user"/> is a Desktop-exchange token (issue #838) that was never
    /// granted ModelRead or LibrarySync scope. Since #1770 made stored-model lookups succeed for
    /// any existing library model rather than just the caller's own uploads, every endpoint that
    /// can resolve a caller-supplied model reference (model3DId, a modelFileUrl/modelFileUrls entry
    /// pointing at a stored model route, or a legacy model-by-ID submission) must apply this guard
    /// identically, or a submit-only-scoped Desktop token could route around it via whichever entry
    /// point skips the check. Normal login/session tokens carry no <see cref="TokenUse"/> claim and
    /// are unaffected, exactly as <c>DesktopScopeAuthorizationHandler</c> behaves for
    /// attribute-gated endpoints.
    /// </summary>
    /// <param name="user">The authenticated caller's claims principal.</param>
    /// <returns><see langword="true"/> if the caller must be refused model access.</returns>
    public static bool IsMissingModelScope(System.Security.Claims.ClaimsPrincipal user) =>
        user.HasClaim(TokenUse, DesktopExchangeTokenUse)
        && !user.HasClaim(Scope, "ModelRead")
        && !user.HasClaim(Scope, "LibrarySync");
}
