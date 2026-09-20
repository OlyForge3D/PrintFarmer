using Farm.Infrastructure.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Web.Api.Tests.Startup;

/// <summary>
/// Regression guardrail for epic #2019's module-decomposition phases (issue #2035, Phase 7).
///
/// <para>
/// Captures the full controller-action route table -- HTTP verb(s), attribute-route template,
/// and assembly-qualified controller/action identity -- and asserts it against a checked-in
/// snapshot (<c>Startup/RouteTableSnapshot.txt</c>). Every subsequent phase that moves a
/// controller into a <c>Farm.Modules.*</c> assembly (phases 8-18) must leave this snapshot
/// byte-identical: a diff here means a route silently changed template, verb, or moved to a
/// different controller/action pair during a "seam only" refactor, which is exactly the class
/// of regression the module migration must never introduce. The identity includes the
/// declaring assembly's name (not just the controller's namespace-qualified type name) so a
/// future move that accidentally leaves a stale copy of a controller behind in the old
/// assembly, alongside the moved copy in the new one, produces two distinct lines instead of
/// silently collapsing to one.
/// </para>
/// <para>
/// Renaming a controller/action or intentionally changing a route requires regenerating the
/// snapshot deliberately (see <see cref="BuildRouteTable"/>) and reviewing the diff -- it must
/// never be regenerated reflexively to make a failing test pass.
/// </para>
/// </summary>
public sealed class RouteTableSnapshotTests
{
    private static readonly string SnapshotPath = Path.GetFullPath(
        Path.Join(AppContext.BaseDirectory, "..", "..", "..", "Startup", "RouteTableSnapshot.txt"));

    [Fact]
    public void ControllerActionRouteTable_MatchesCheckedInSnapshot()
    {
        using CustomWebApplicationFactory factory = new();

        EndpointDataSource endpointDataSource = factory.Services.GetRequiredService<EndpointDataSource>();

        string[] actual = BuildRouteTable(endpointDataSource);
        string[] expected = File.ReadAllLines(SnapshotPath);

        AssertSnapshotMatches(actual, expected);
    }

    [Fact]
    public void ControllerActionRouteTable_RejectsAnonymousRegressionOnPrivilegedRoute()
    {
        using CustomWebApplicationFactory factory = new();

        EndpointDataSource endpointDataSource = factory.Services.GetRequiredService<EndpointDataSource>();
        string[] expected = BuildRouteTable(endpointDataSource);
        string privilegedRoute = expected.Single(line =>
            line.Contains(
                "DELETE /api/admin/roles/{roleId:guid}",
                StringComparison.Ordinal) &&
            line.Contains("permission=roles:admin", StringComparison.Ordinal));
        string[] regressed = expected
            .Select(line => line == privilegedRoute
                ? line[..line.IndexOf(" [", StringComparison.Ordinal)] + " [auth=anonymous]"
                : line)
            .ToArray();

        Action assertRegression = () => AssertSnapshotMatches(regressed, expected);

        _ = assertRegression.Should().Throw<Xunit.Sdk.XunitException>();
    }

    [Fact]
    public void ControllerActionRouteTable_RecordsEffectiveAuthorizationMetadata()
    {
        using CustomWebApplicationFactory factory = new();

        string[] actual = BuildRouteTable(factory.Services.GetRequiredService<EndpointDataSource>());

        _ = actual.Should().Contain(line =>
            line.Contains(
                "GET /api/admin/overview",
                StringComparison.Ordinal) &&
            line.Contains("permission=system_settings:admin", StringComparison.Ordinal));
        _ = actual.Should().Contain(line =>
            line.Contains(
                "GET /api/admin/roles",
                StringComparison.Ordinal) &&
            line.Contains("permission=roles:admin", StringComparison.Ordinal));
        _ = actual.Should().Contain(line =>
            line.Contains(
                "GET /api/schema-health/ready",
                StringComparison.Ordinal) &&
            line.EndsWith("[auth=anonymous]", StringComparison.Ordinal));
        _ = actual.Should().Contain(line =>
            line.Contains(
                "UnifiedSettingsController.GetSettingsByKeyName",
                StringComparison.Ordinal) &&
            line.EndsWith("[auth=anonymous]", StringComparison.Ordinal));
        _ = actual.Count(line => line.Contains("permission=", StringComparison.Ordinal))
            .Should().BeGreaterThan(0);
        _ = actual.Count(line => line.EndsWith("[auth=anonymous]", StringComparison.Ordinal))
            .Should().BeGreaterThan(0);
    }

    /// <summary>
    /// Builds the sorted, checked-in-snapshot line format: one line per runtime controller
    /// endpoint, each listing every HTTP verb it accepts, its attribute-route template,
    /// effective authorization metadata, and its <c>Assembly::Controller.Action</c> identity.
    /// The assembly qualifier deliberately makes
    /// two identically-named controllers in different assemblies produce distinct lines (see
    /// class remarks); no <c>Distinct()</c> is applied afterward, so a genuine duplicate route
    /// registration -- which this format could otherwise mask -- instead surfaces as a real
    /// diff against the snapshot rather than being silently deduplicated away.
    /// </summary>
    private static string[] BuildRouteTable(EndpointDataSource endpointDataSource)
    {
        return endpointDataSource.Endpoints
            .OfType<RouteEndpoint>()
            .Select(endpoint => new
            {
                Endpoint = endpoint,
                Action = endpoint.Metadata.GetMetadata<ControllerActionDescriptor>()
            })
            .Where(item => item.Action is not null)
            .Select(item =>
            {
                RouteEndpoint endpoint = item.Endpoint;
                ControllerActionDescriptor action = item.Action!;
                string methods = string.Join(
                    "+",
                    endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(m => m, StringComparer.Ordinal)
                    ?? Enumerable.Empty<string>());
                if (methods.Length == 0)
                {
                    methods = "ANY";
                }

                string template = action.AttributeRouteInfo?.Template ?? string.Empty;
                string assemblyName = action.ControllerTypeInfo.Assembly.GetName().Name ?? "?";
                string identity = $"{assemblyName}::{action.ControllerTypeInfo.FullName}.{action.MethodInfo.Name}";
                string authorization = BuildAuthorization(endpoint.Metadata);
                return $"{methods} /{template} -> {identity} [{authorization}]";
            })
            .OrderBy(line => line, StringComparer.Ordinal)
            .ToArray();
    }

    private static string BuildAuthorization(EndpointMetadataCollection metadata)
    {
        if (metadata.GetMetadata<IAllowAnonymous>() is not null)
        {
            return "auth=anonymous";
        }

        string[] requirements = metadata
            .GetOrderedMetadata<IAuthorizeData>()
            .Select(data =>
            {
                string policy = data.Policy ?? string.Empty;
                string roles = data.Roles ?? string.Empty;
                string schemes = data.AuthenticationSchemes ?? string.Empty;
                return $"policy={policy};roles={roles};schemes={schemes}";
            })
            .Concat(
                metadata
                    .GetOrderedMetadata<IPermissionMetadata>()
                    .Select(permission => $"permission={permission.Permission}"))
            .ToArray();

        return requirements.Length == 0
            ? "auth=none"
            : $"auth={string.Join("|", requirements)}";
    }

    private static void AssertSnapshotMatches(string[] actual, string[] expected)
    {
        actual.Should().Equal(
            expected,
            "the controller-action route table and effective authorization must not change while " +
            "Farm.Modules.Abstractions lands the module host seam (issue #2035) -- if this is a " +
            "deliberate route or authorization change, regenerate Startup/RouteTableSnapshot.txt " +
            "and review the diff carefully");
    }
}
