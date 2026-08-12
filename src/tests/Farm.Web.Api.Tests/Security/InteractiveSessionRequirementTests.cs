using System.Security.Claims;
using Farm.Infrastructure.Authorization;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Farm.Web.Api.Tests.Security;

/// <summary>
/// Unit coverage for <see cref="InteractiveSessionAuthorizationHandler"/>, the control that stops a
/// short-lived Desktop-exchange token being laundered into a durable credential (a long-lived API
/// key, a registered passkey) or into slicer profile-state mutation.
/// </summary>
public class InteractiveSessionRequirementTests
{
    private static ClaimsPrincipal DesktopExchangePrincipal() =>
        new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                new Claim(DesktopScopeClaims.TokenUse, DesktopScopeClaims.DesktopExchangeTokenUse),
            ],
            "TestAuth"));

    private static ClaimsPrincipal LoginPrincipal(params string[] roles)
    {
        List<Claim> claims = [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())];
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
    }

    private static async Task<AuthorizationHandlerContext> EvaluateAsync(
        ClaimsPrincipal user,
        bool simulateDevModeBypass = false)
    {
        InteractiveSessionRequirement requirement = new();
        AuthorizationHandlerContext context = new([requirement], user, new DefaultHttpContext());

        if (simulateDevModeBypass)
        {
            // DevModeAuthorizationHandler succeeds every pending requirement on safe methods when
            // enabled in Development. Simulate that having already run.
            context.Succeed(requirement);
        }

        await new InteractiveSessionAuthorizationHandler().HandleAsync(context);
        return context;
    }

    [Fact]
    public async Task DesktopExchangeToken_IsDenied()
    {
        AuthorizationHandlerContext context = await EvaluateAsync(DesktopExchangePrincipal());

        context.HasSucceeded.Should().BeFalse();
        context.HasFailed.Should().BeTrue();
    }

    /// <summary>
    /// The handler calls <c>Fail()</c> rather than merely declining to succeed, so an earlier
    /// success - including the Development-only bypass - cannot re-open the endpoint.
    /// </summary>
    [Fact]
    public async Task DesktopExchangeToken_IsDeniedEvenWhenAnotherHandlerAlreadySucceeded()
    {
        AuthorizationHandlerContext context = await EvaluateAsync(
            DesktopExchangePrincipal(),
            simulateDevModeBypass: true);

        context.HasFailed.Should().BeTrue("Fail() is decisive and outranks a prior Succeed()");
        context.HasSucceeded.Should().BeFalse();
    }

    /// <summary>
    /// Deny-by-marker: anything that is not an exchange token passes through untouched, so login
    /// sessions, the admin UI, and the slicer host's standalone principal are unaffected.
    /// </summary>
    [Theory]
    [InlineData()]
    [InlineData("farm_admin")]
    [InlineData("farm_user")]
    public async Task NonExchangePrincipals_PassThrough(params string[] roles)
    {
        AuthorizationHandlerContext context = await EvaluateAsync(LoginPrincipal(roles));

        context.HasSucceeded.Should().BeTrue();
        context.HasFailed.Should().BeFalse();
    }

    /// <summary>
    /// A principal that merely mentions the marker value in an unrelated claim type must not be
    /// denied - only the dedicated <c>token_use</c> claim counts.
    /// </summary>
    [Fact]
    public async Task PrincipalWithLookalikeClaim_IsNotDenied()
    {
        ClaimsPrincipal user = new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                new Claim("purpose", DesktopScopeClaims.DesktopExchangeTokenUse),
            ],
            "TestAuth"));

        AuthorizationHandlerContext context = await EvaluateAsync(user);

        context.HasSucceeded.Should().BeTrue();
    }
}
