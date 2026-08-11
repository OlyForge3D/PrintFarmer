using System.Security.Claims;
using Farm.Infrastructure.Authorization;
using Farm.Infrastructure.Security;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging.Abstractions;

namespace Farm.Web.Api.Tests.Security;

/// <summary>
/// Covers the resource:admin implication added by
/// <see href="https://github.com/OlyForge3D/PrintFarmer/issues/1447">#1447</see>: a principal
/// holding "{resource}:admin" must satisfy every finer-grained action check on that same
/// resource, without leaking the grant to any other resource, and without introducing any
/// broader action hierarchy (e.g. "update" does not imply "read").
/// </summary>
public sealed class PermissionAuthorizationHandlerTests
{
    /// <summary>
    /// The 15 canonical resources seeded by <c>DatabaseInitializer.SeedResourcesAsync</c>
    /// (src/api/Services/Startup/DatabaseInitializer.cs), kept in sync so every resource in the
    /// permission contract is covered by the admin-implication test.
    /// </summary>
    public static IEnumerable<object[]> CanonicalResources()
    {
        yield return new object[] { "printers" };
        yield return new object[] { "gcode_harvest" };
        yield return new object[] { "gcode_library" };
        yield return new object[] { "job_queue" };
        yield return new object[] { "slicer_engines" };
        yield return new object[] { "users" };
        yield return new object[] { "roles" };
        yield return new object[] { "system_settings" };
        yield return new object[] { "spoolman" };
        yield return new object[] { "network_discovery" };
        yield return new object[] { "calibration" };
        yield return new object[] { "queue" };
        yield return new object[] { "slicing" };
        yield return new object[] { "dispatch-settings" };
        yield return new object[] { "obico" };
    }

    [Theory]
    [MemberData(nameof(CanonicalResources))]
    public async Task HandleRequirementAsync_ResourceAdmin_SatisfiesReadCheck(string resource)
    {
        AuthorizationHandlerContext context = CreateContext(
            resource,
            action: "read",
            grantedPermission: $"{resource}:admin");

        var handler = new PermissionAuthorizationHandler(NullLogger<PermissionAuthorizationHandler>.Instance);
        await handler.HandleAsync(context);

        context.HasSucceeded.Should().BeTrue(
            $"a '{resource}:admin' grant must imply '{resource}:read' per issue #1447");
    }

    [Theory]
    [InlineData("printers", "write")]
    [InlineData("calibration", "delete")]
    [InlineData("job_queue", "create")]
    public async Task HandleRequirementAsync_ResourceAdmin_SatisfiesNonReadActions(string resource, string action)
    {
        // The implication is not coupled to "read" specifically: an admin grant on a
        // resource satisfies every action check on that resource.
        AuthorizationHandlerContext context = CreateContext(
            resource,
            action,
            grantedPermission: $"{resource}:admin");

        var handler = new PermissionAuthorizationHandler(NullLogger<PermissionAuthorizationHandler>.Instance);
        await handler.HandleAsync(context);

        context.HasSucceeded.Should().BeTrue(
            $"a '{resource}:admin' grant must imply '{resource}:{action}'");
    }

    [Theory]
    [MemberData(nameof(CanonicalResources))]
    public async Task HandleRequirementAsync_ResourceRead_DoesNotSatisfyAdminCheck(string resource)
    {
        AuthorizationHandlerContext context = CreateContext(
            resource,
            action: "admin",
            grantedPermission: $"{resource}:read");

        var handler = new PermissionAuthorizationHandler(NullLogger<PermissionAuthorizationHandler>.Instance);
        await handler.HandleAsync(context);

        context.HasSucceeded.Should().BeFalse(
            $"a '{resource}:read' grant must not imply '{resource}:admin'");
    }

    [Fact]
    public async Task HandleRequirementAsync_AdminGrant_DoesNotLeakToAnotherResource()
    {
        AuthorizationHandlerContext context = CreateContext(
            resource: "queue",
            action: "read",
            grantedPermission: "printers:admin");

        var handler = new PermissionAuthorizationHandler(NullLogger<PermissionAuthorizationHandler>.Instance);
        await handler.HandleAsync(context);

        context.HasSucceeded.Should().BeFalse("printers:admin must not grant anything on the queue resource");
    }

    [Fact]
    public async Task HandleRequirementAsync_ExactPermissionMatch_StillSucceeds()
    {
        AuthorizationHandlerContext context = CreateContext(
            resource: "calibration",
            action: "read",
            grantedPermission: "calibration:read");

        var handler = new PermissionAuthorizationHandler(NullLogger<PermissionAuthorizationHandler>.Instance);
        await handler.HandleAsync(context);

        context.HasSucceeded.Should().BeTrue("exact resource:action permission matches must keep working unchanged");
    }

    [Fact]
    public async Task HandleRequirementAsync_NoMatchingPermission_Fails()
    {
        AuthorizationHandlerContext context = CreateContext(
            resource: "calibration",
            action: "read",
            grantedPermission: "calibration:update");

        var handler = new PermissionAuthorizationHandler(NullLogger<PermissionAuthorizationHandler>.Instance);
        await handler.HandleAsync(context);

        context.HasSucceeded.Should().BeFalse("an unrelated permission on the same resource must not satisfy the check");
    }

    [Fact]
    public async Task HandleRequirementAsync_ClaimValueComparisonIsCaseSensitive()
    {
        // ClaimsIdentity.HasClaim(type, value) compares the claim VALUE with
        // StringComparison.Ordinal (case-sensitive) — only the claim TYPE comparison is
        // case-insensitive. A mixed-case permission claim must not satisfy a lowercase
        // resource:admin check (or vice versa).
        AuthorizationHandlerContext context = CreateContext(
            resource: "printers",
            action: "read",
            grantedPermission: "Printers:Admin");

        var handler = new PermissionAuthorizationHandler(NullLogger<PermissionAuthorizationHandler>.Instance);
        await handler.HandleAsync(context);

        context.HasSucceeded.Should().BeFalse("permission claim values are matched case-sensitively; 'Printers:Admin' must not satisfy 'printers:read'");
    }

    [Fact]
    public async Task HandleRequirementAsync_FarmAdminRole_StillBypassesRegardlessOfClaims()
    {
        var requirement = new RequirePermissionAttribute("printers", "read");
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Role, PrintFarmerPermissions.FarmAdminRole),
        ], authenticationType: "TestAuth");
        var principal = new ClaimsPrincipal(identity);
        var context = new AuthorizationHandlerContext([requirement], principal, resource: null);

        var handler = new PermissionAuthorizationHandler(NullLogger<PermissionAuthorizationHandler>.Instance);
        await handler.HandleAsync(context);

        context.HasSucceeded.Should().BeTrue("the farm_admin role bypass must remain unaffected by the resource-admin implication");
    }

    private static AuthorizationHandlerContext CreateContext(string resource, string action, string grantedPermission)
    {
        var requirement = new RequirePermissionAttribute(resource, action);
        var identity = new ClaimsIdentity(
        [
            new Claim(PrintFarmerPermissions.ClaimType, grantedPermission),
        ], authenticationType: "TestAuth");
        var principal = new ClaimsPrincipal(identity);
        return new AuthorizationHandlerContext([requirement], principal, resource: null);
    }
}
