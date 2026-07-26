using Microsoft.AspNetCore.Authorization;

namespace Farm.Infrastructure.Authorization;

/// <summary>
/// Authorization requirement satisfied when the current principal either is not a
/// Desktop-exchange token (normal login/session/standalone principals pass through
/// unaffected) or carries the specific required <see cref="Domain.ApiKeyScope"/> claim.
/// </summary>
/// <param name="requiredScope">
/// The scope claim value (see <see cref="DesktopScopeClaims.Scope"/>) that a Desktop-exchange
/// token must carry for this requirement to succeed, e.g. "ModelRead".
/// </param>
public sealed class DesktopScopeRequirement(string requiredScope) : IAuthorizationRequirement
{
    /// <summary>
    /// The required scope value, matching one of the <see cref="Domain.ApiKeyScope"/> flag names.
    /// </summary>
    public string RequiredScope { get; } = requiredScope;
}
