using Microsoft.AspNetCore.Authorization;

namespace Farm.Infrastructure.Authorization;

/// <summary>
/// Authorization requirement satisfied only by a principal from an <b>interactive session</b> -
/// a normal login/session token (or the slicer host's standalone principal) - and never by a
/// Desktop API-key exchange token.
/// </summary>
/// <remarks>
/// <para>
/// This closes a credential-laundering path. An exchange token is a deliberately short-lived,
/// narrowly scoped bearer credential that a desktop client holds on an end-user machine. Because
/// it carries the owner's <c>NameIdentifier</c>, it would otherwise satisfy <c>[Authorize]</c> plus
/// the "am I acting on my own user id?" check on the API-key management endpoints - letting anyone
/// who captured a 15-minute token mint a fresh API key valid for up to a year, and choose its
/// scopes. Credential management must therefore require a real interactive session.
/// </para>
/// <para>
/// Deny-by-marker (rather than allow-by-marker) is deliberate: any principal that is <i>not</i> an
/// exchange token passes through untouched, so login sessions, admin UI, and the standalone
/// slicer-host principal keep working exactly as before.
/// </para>
/// </remarks>
public sealed class InteractiveSessionRequirement : IAuthorizationRequirement
{
    /// <summary>
    /// The policy name registered for this requirement.
    /// </summary>
    public const string PolicyName = "RequireInteractiveSession";
}

/// <summary>
/// Handles <see cref="InteractiveSessionRequirement"/> by failing hard for any principal carrying
/// <see cref="DesktopScopeClaims.TokenUse"/> = <see cref="DesktopScopeClaims.DesktopExchangeTokenUse"/>.
/// </summary>
public sealed class InteractiveSessionAuthorizationHandler
    : AuthorizationHandler<InteractiveSessionRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        InteractiveSessionRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);

        bool isDesktopExchangeToken = context.User.HasClaim(
            DesktopScopeClaims.TokenUse,
            DesktopScopeClaims.DesktopExchangeTokenUse);

        if (isDesktopExchangeToken)
        {
            // Explicit Fail() (not merely "don't succeed"): this must not be satisfiable by any
            // other handler in the pipeline, including the Development-only bypass handler.
            context.Fail(new AuthorizationFailureReason(
                this,
                "API key management requires an interactive session."));
            return Task.CompletedTask;
        }

        context.Succeed(requirement);
        return Task.CompletedTask;
    }
}
