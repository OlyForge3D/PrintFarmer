using Microsoft.AspNetCore.Authorization;

namespace Farm.Infrastructure.Authorization;

/// <summary>
/// Handles <see cref="DesktopScopeRequirement"/>. Principals that were not issued by the
/// Desktop API-key exchange (i.e. normal login/session tokens, and the slicer host's
/// standalone-mode principal) are unaffected and pass through unchanged, preserving all
/// existing authorization behavior exactly as before issue #838. Only Desktop-exchange
/// tokens (marked by <see cref="DesktopScopeClaims.TokenUse"/>) are actually scope-checked,
/// and only the exact scope required by the endpoint's policy is accepted - a Desktop
/// token issued with e.g. only "ModelRead" can never satisfy a "ModelWrite" requirement.
/// </summary>
public sealed class DesktopScopeAuthorizationHandler : AuthorizationHandler<DesktopScopeRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, DesktopScopeRequirement requirement)
    {
        bool isDesktopExchangeToken = context.User.HasClaim(
            DesktopScopeClaims.TokenUse,
            DesktopScopeClaims.DesktopExchangeTokenUse);

        if (!isDesktopExchangeToken)
        {
            // Not a scoped Desktop token (normal session, or standalone slicer-host
            // principal) - defer entirely to the endpoint's other authorization
            // requirements (e.g. [Authorize], [Authorize(Roles = ...)]).
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        if (context.User.HasClaim(DesktopScopeClaims.Scope, requirement.RequiredScope))
        {
            context.Succeed(requirement);
        }

        // Intentionally no explicit Fail(): leaving the requirement unsatisfied results in
        // the overall policy evaluation failing (403), without revealing which scope was
        // missing to the caller.
        return Task.CompletedTask;
    }
}
