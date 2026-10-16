using Farm.Infrastructure.Authorization;
using Farm.Modules.Devices.Filters;
using Farm.Slicer.Module.Api.Filters;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using SlicerRequirePermissionAttribute = Farm.Slicer.Module.Api.Filters.RequirePermissionAttribute;

namespace Farm.Web.Api.Tests.Startup;

/// <summary>
/// Regression guardrail for epic #2019's module-decomposition phases (issue #2035, Phase 7).
///
/// <para>
/// Captures the full controller-action route table -- HTTP verb(s), attribute-route template,
/// declared endpoint authorization metadata, and assembly-qualified controller/action identity --
/// and asserts it against a checked-in
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
/// This intentionally covers controller-action endpoints only. Minimal APIs (including health,
/// liveness, and network-discovery mappings) and SignalR hub endpoints are outside this snapshot.
/// </para>
/// <para>
/// Each line records two independent authorization views. The first bracket is declared endpoint
/// metadata only: <see cref="IAllowAnonymous"/>, <see cref="IAuthorizeData"/>, and
/// <see cref="IPermissionMetadata"/>, falling back to the configured fallback policy's requirement
/// types. The optional second bracket (<c>[filter-auth=...]</c>) is projected from the action's
/// runtime MVC filter pipeline (<see cref="ActionDescriptor.FilterDescriptors"/>, without
/// re-parsing attributes): every <see cref="IAuthorizationFilter"/> /
/// <see cref="IAsyncAuthorizationFilter"/> (for example the OctoPrint API-key filter), plus the
/// known authorization action filters in the slicer module (filter-based
/// <c>RequirePermissionAttribute</c> and the slicer API-key filters). It is emitted even when the
/// route is anonymous at the endpoint-metadata level. Not observed: authorization performed by other
/// action filters, filter factories, middleware, or handler bodies, and resolved policy
/// requirements behind named policies.
/// </para>
/// <para>
/// Renaming a controller/action or intentionally changing a route requires regenerating the
/// snapshot deliberately (see <see cref="BuildRouteTableAsync"/>) and reviewing the diff -- it must
/// never be regenerated reflexively to make a failing test pass.
/// </para>
/// </summary>
public sealed class RouteTableSnapshotTests
{
    private static readonly string SnapshotPath = Path.GetFullPath(
        Path.Join(AppContext.BaseDirectory, "..", "..", "..", "Startup", "RouteTableSnapshot.txt"));

    [Fact]
    public async Task ControllerActionRouteTable_MatchesCheckedInSnapshot()
    {
        using CustomWebApplicationFactory factory = new();

        EndpointDataSource endpointDataSource = factory.Services.GetRequiredService<EndpointDataSource>();
        IAuthorizationPolicyProvider policyProvider =
            factory.Services.GetRequiredService<IAuthorizationPolicyProvider>();

        string[] actual = await BuildRouteTableAsync(endpointDataSource, policyProvider);
        string[] expected = File.ReadAllLines(SnapshotPath);

        AssertSnapshotMatches(actual, expected);
    }

    [Fact]
    public async Task ControllerActionRouteTable_RejectsAnonymousRegressionOnPrivilegedRoute()
    {
        using CustomWebApplicationFactory factory = new();

        EndpointDataSource endpointDataSource = factory.Services.GetRequiredService<EndpointDataSource>();
        IAuthorizationPolicyProvider policyProvider =
            factory.Services.GetRequiredService<IAuthorizationPolicyProvider>();
        string[] expected = await BuildRouteTableAsync(endpointDataSource, policyProvider);
        string privilegedRoute = expected.Should().ContainSingle(
            line =>
                line.Contains(
                    "DELETE /api/admin/roles/{roleId:guid}",
                    StringComparison.Ordinal) &&
                line.Contains("|permission=roles:admin", StringComparison.Ordinal),
            "the privileged route used by this regression test must be unique").Which;
        string[] regressed = expected
            .Select(line => line == privilegedRoute
                ? line[..line.IndexOf(" [", StringComparison.Ordinal)] + " [auth=anonymous]"
                : line)
            .ToArray();

        Action assertRegression = () => AssertSnapshotMatches(regressed, expected);

        _ = assertRegression.Should().Throw<Xunit.Sdk.XunitException>()
            .Which.Message.Should().Contain("DELETE /api/admin/roles/{roleId:guid}");
    }

    [Fact]
    public async Task ControllerActionRouteTable_RecordsDeclaredAuthorizationMetadata()
    {
        using CustomWebApplicationFactory factory = new();

        string[] actual = await BuildRouteTableAsync(
            factory.Services.GetRequiredService<EndpointDataSource>(),
            factory.Services.GetRequiredService<IAuthorizationPolicyProvider>());

        _ = actual.Should().Contain(line =>
            line.Contains(
                "GET /api/admin/overview",
                StringComparison.Ordinal) &&
            line.Contains("|permission=system_settings:admin", StringComparison.Ordinal));
        _ = actual.Should().Contain(line =>
            line.Contains(
                "GET /api/admin/roles",
                StringComparison.Ordinal) &&
            line.Contains("|permission=roles:admin", StringComparison.Ordinal));
        _ = actual.Should().Contain(line =>
            line.Contains(
                "GET /api/schema-health/ready",
                StringComparison.Ordinal) &&
            line.EndsWith("[auth=anonymous:AllowAnonymousAttribute]", StringComparison.Ordinal));
        _ = actual.Should().Contain(line =>
            line.Contains(
                "UnifiedSettingsController.GetSettingsByKeyName",
                StringComparison.Ordinal) &&
            line.EndsWith("[auth=anonymous:AllowAnonymousAttribute]", StringComparison.Ordinal));
        _ = actual.Should().Contain(line =>
            line.Contains(
                "POST /api/files/local",
                StringComparison.Ordinal) &&
            line.Contains("catalog-permission=queue:write", StringComparison.Ordinal));
        _ = actual.Count(line => line.Contains("|permission=", StringComparison.Ordinal))
            .Should().BeGreaterThan(0);
        _ = actual.Count(line => line.Contains("[auth=anonymous:", StringComparison.Ordinal))
            .Should().BeGreaterThan(0);
        _ = actual.Count(line => line.EndsWith("[auth=fallback:DenyAnonymousAuthorizationRequirement]", StringComparison.Ordinal))
            .Should().BeGreaterThan(0);
    }

    [Fact]
    public void ControllerActionRouteTable_BindsOctoPrintApiKeyFiltersToPublicActions()
    {
        using CustomWebApplicationFactory factory = new();

        EndpointDataSource endpointDataSource = factory.Services.GetRequiredService<EndpointDataSource>();
        ControllerActionDescriptor[] actions = endpointDataSource.Endpoints
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.Metadata.GetMetadata<ControllerActionDescriptor>())
            .Where(action => action is not null &&
                action.ControllerTypeInfo.AsType() == typeof(Farm.Web.Api.Controllers.OctoPrintCompatController) &&
                action.ActionName is "GetVersion" or "GetServer")
            .Cast<ControllerActionDescriptor>()
            .ToArray();

        _ = actions.Should().ContainSingle(action => action.ActionName == "GetVersion")
            .Which.FilterDescriptors.Should().Contain(
                descriptor => descriptor.Filter is OctoPrintApiKeyAttribute,
                "GetVersion must remain bound to its OctoPrint API-key authorization filter");
        _ = actions.Should().ContainSingle(action => action.ActionName == "GetServer")
            .Which.FilterDescriptors.Should().Contain(
                descriptor => descriptor.Filter is OctoPrintApiKeyAttribute,
                "GetServer must remain bound to its OctoPrint API-key authorization filter");
    }

    [Fact]
    public async Task ControllerActionRouteTable_ReportsOctoPrintApiKeyFilterOnPublicActions()
    {
        using CustomWebApplicationFactory factory = new();

        string[] actual = await BuildRouteTableAsync(
            factory.Services.GetRequiredService<EndpointDataSource>(),
            factory.Services.GetRequiredService<IAuthorizationPolicyProvider>());

        const string octoPrintSuffix =
            "[auth=anonymous:AllowAnonymousAttribute] [filter-auth=octoprint-api-key:requireValidKeyForAnonymous=false]";
        _ = actual.Should().ContainSingle(line =>
                line.StartsWith("GET /api/version -> ", StringComparison.Ordinal) &&
                line.Contains("OctoPrintCompatController.GetVersion", StringComparison.Ordinal))
            .Which.Should().EndWith(octoPrintSuffix);
        _ = actual.Should().ContainSingle(line =>
                line.StartsWith("GET /api/server -> ", StringComparison.Ordinal) &&
                line.Contains("OctoPrintCompatController.GetServer", StringComparison.Ordinal))
            .Which.Should().EndWith(octoPrintSuffix);
    }

    [Theory]
    [InlineData("GET /api/file-consistency/audits/history -> ")]
    [InlineData("GET /api/file-consistency/gcode/{gcodeId}/health -> ")]
    [InlineData("GET /api/file-consistency/health/summary -> ")]
    [InlineData("GET /api/file-consistency/issues -> ")]
    [InlineData("GET /api/file-consistency/model3d/{modelId}/health -> ")]
    public async Task ControllerActionRouteTable_ReportsSlicerFilterPermissionOnFileConsistencyRoutes(string routePrefix)
    {
        using CustomWebApplicationFactory factory = new();

        string[] actual = await BuildRouteTableAsync(
            factory.Services.GetRequiredService<EndpointDataSource>(),
            factory.Services.GetRequiredService<IAuthorizationPolicyProvider>());

        _ = actual.Should().ContainSingle(line =>
                line.StartsWith(routePrefix, StringComparison.Ordinal) &&
                line.Contains("FileConsistencyController.", StringComparison.Ordinal))
            .Which.Should().EndWith(" [filter-auth=permission=slicer_engines:admin]");
    }

    [Fact]
    public void BuildFilterAuthorization_ProjectsAuthorizationFiltersCanonically()
    {
        FilterDescriptor[] descriptors =
        [
            new(new RequireSlicerServiceApiKeyAttribute(), FilterScope.Action),
            new(new NonAuthorizationActionFilter(), FilterScope.Global),
            new(new SlicerRequirePermissionAttribute("slicer_engines:admin"), FilterScope.Controller),
            new(new OctoPrintApiKeyAttribute { RequireValidKeyForAnonymous = true }, FilterScope.Action),
            new(new RequireSlicerApiKeyAttribute(), FilterScope.Action),
            new(new UnknownAuthorizationFilter(), FilterScope.Global),
        ];

        string projected = BuildFilterAuthorization(descriptors);

        _ = projected.Should().Be(
            "filter-auth=" +
            $"authorization-filter={typeof(UnknownAuthorizationFilter).FullName}|" +
            "octoprint-api-key:requireValidKeyForAnonymous=true|" +
            "permission=slicer_engines:admin|" +
            "slicer-api-key|" +
            "slicer-service-api-key");
        _ = BuildFilterAuthorization(descriptors.Reverse()).Should().Be(
            projected,
            "the projection is canonical and independent of filter registration order");
    }

    [Fact]
    public void BuildFilterAuthorization_ChangesWhenAuthorizationFilterIsRemovedOrReconfigured()
    {
        FilterDescriptor octoPrint = new(new OctoPrintApiKeyAttribute(), FilterScope.Action);
        FilterDescriptor permission = new(
            new SlicerRequirePermissionAttribute("slicer_engines:admin"), FilterScope.Controller);
        FilterDescriptor nonAuthorization = new(new NonAuthorizationActionFilter(), FilterScope.Global);

        _ = BuildFilterAuthorization([nonAuthorization]).Should().BeEmpty(
            "ordinary action filters are not authorization and must not be reported");
        _ = BuildFilterAuthorization([octoPrint, nonAuthorization]).Should().NotBe(
            BuildFilterAuthorization([nonAuthorization]));
        _ = BuildFilterAuthorization([permission]).Should().NotBe(
            BuildFilterAuthorization(
                [new FilterDescriptor(new SlicerRequirePermissionAttribute("slicer_engines:read"), FilterScope.Controller)]));
        _ = BuildFilterAuthorization([octoPrint]).Should().NotBe(
            BuildFilterAuthorization(
                [new FilterDescriptor(new OctoPrintApiKeyAttribute { RequireValidKeyForAnonymous = true }, FilterScope.Action)]));
    }

    [Fact]
    public void AssertSnapshotMatches_ReportsDeepDifferenceAndLengthMismatch()
    {
        string[] expected = Enumerable.Range(0, 881)
            .Select(index => $"route-{index}")
            .ToArray();
        string[] actual = expected.ToArray();
        actual[640] = "route-640-regressed";

        Action assertDeepDifference = () => AssertSnapshotMatches(actual, expected);

        string deepDifferenceMessage = assertDeepDifference.Should().Throw<Xunit.Sdk.XunitException>()
            .Which.Message;
        deepDifferenceMessage.Should().Contain("index 640");
        deepDifferenceMessage.Should().Contain("Expected: route-640");
        deepDifferenceMessage.Should().Contain("Actual:   route-640-regressed");

        Action assertLengthDifference = () => AssertSnapshotMatches(expected[..^1], expected);

        string lengthDifferenceMessage = assertLengthDifference.Should().Throw<Xunit.Sdk.XunitException>()
            .Which.Message;
        lengthDifferenceMessage.Should().Contain("expected 881 routes but found 880");
    }

    /// <summary>
    /// Builds the sorted, checked-in-snapshot line format: one line per runtime controller
    /// endpoint, each listing every HTTP verb it accepts, its attribute-route template,
    /// declared endpoint authorization metadata, its <c>Assembly::Controller.Action</c>
    /// identity, and (when present) its filter-based authorization projected by
    /// <see cref="BuildFilterAuthorization"/>. Handler-body authorization is not observed.
    /// The assembly qualifier deliberately makes
    /// two identically-named controllers in different assemblies produce distinct lines (see
    /// class remarks); no <c>Distinct()</c> is applied afterward, so a genuine duplicate route
    /// registration -- which this format could otherwise mask -- instead surfaces as a real
    /// diff against the snapshot rather than being silently deduplicated away.
    /// </summary>
    private static async Task<string[]> BuildRouteTableAsync(
        EndpointDataSource endpointDataSource,
        IAuthorizationPolicyProvider policyProvider)
    {
        Task<string>[] lines = endpointDataSource.Endpoints
            .OfType<RouteEndpoint>()
            .Select(endpoint => new
            {
                Endpoint = endpoint,
                Action = endpoint.Metadata.GetMetadata<ControllerActionDescriptor>()
            })
            .Where(item => item.Action is not null)
            .Select(async item =>
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
                string authorization = await BuildAuthorizationAsync(endpoint.Metadata, policyProvider);
                string filterAuthorization = BuildFilterAuthorization(action.FilterDescriptors);
                return filterAuthorization.Length == 0
                    ? $"{methods} /{template} -> {identity} [{authorization}]"
                    : $"{methods} /{template} -> {identity} [{authorization}] [{filterAuthorization}]";
            })
            .ToArray();

        return (await Task.WhenAll(lines))
            .OrderBy(line => line, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// Projects the runtime MVC filter pipeline of an action (its
    /// <see cref="ActionDescriptor.FilterDescriptors"/>, i.e. the filter instances MVC
    /// will actually execute, including inherited controller-level and global filters) into a
    /// canonical, order-independent authorization annotation. Known authorization action filters
    /// are rendered with their configuration; any other <see cref="IAuthorizationFilter"/> or
    /// <see cref="IAsyncAuthorizationFilter"/> is rendered by type name so a newly introduced
    /// authorization filter surfaces as a snapshot diff instead of being silently ignored.
    /// Returns an empty string when the action has no authorization filters. This is computed
    /// independently of the endpoint-metadata view, so it is reported even for anonymous routes.
    /// </summary>
    private static string BuildFilterAuthorization(IEnumerable<FilterDescriptor> filterDescriptors)
    {
        string[] filters = filterDescriptors
            .Select(descriptor => DescribeAuthorizationFilter(descriptor.Filter))
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .OrderBy(filter => filter, StringComparer.Ordinal)
            .ToArray();

        return filters.Length == 0 ? string.Empty : $"filter-auth={string.Join("|", filters)}";
    }

    private static string? DescribeAuthorizationFilter(IFilterMetadata filter) => filter switch
    {
        OctoPrintApiKeyAttribute octoPrint =>
            $"octoprint-api-key:requireValidKeyForAnonymous={(octoPrint.RequireValidKeyForAnonymous ? "true" : "false")}",
        SlicerRequirePermissionAttribute slicerPermission => $"permission={slicerPermission.Permission}",
        RequireSlicerApiKeyAttribute => "slicer-api-key",
        RequireSlicerServiceApiKeyAttribute => "slicer-service-api-key",
        IAuthorizationFilter or IAsyncAuthorizationFilter => $"authorization-filter={filter.GetType().FullName}",
        _ => null
    };

    private static async Task<string> BuildAuthorizationAsync(
        EndpointMetadataCollection metadata,
        IAuthorizationPolicyProvider policyProvider)
    {
        string[] anonymousMetadata = metadata
            .GetOrderedMetadata<IAllowAnonymous>()
            .Select(item => item.GetType().Name)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        if (anonymousMetadata.Length > 0)
        {
            return $"auth=anonymous:{string.Join("+", anonymousMetadata)}";
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
                    .Select(permission => permission is IAuthorizationRequirementData data &&
                        data.GetRequirements().Any()
                        ? $"permission={permission.Permission}"
                        : $"catalog-permission={permission.Permission}"))
            .ToArray();

        if (requirements.Length > 0)
        {
            return $"auth={string.Join("|", requirements)}";
        }

        AuthorizationPolicy? fallbackPolicy = await policyProvider.GetFallbackPolicyAsync();
        if (fallbackPolicy is not null)
        {
            string[] fallbackRequirements = fallbackPolicy.Requirements
                .Select(requirement => requirement.GetType().Name)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
            return $"auth=fallback:{(fallbackRequirements.Length == 0
                ? "empty"
                : string.Join("+", fallbackRequirements))}";
        }

        return "auth=none";
    }

    private static void AssertSnapshotMatches(string[] actual, string[] expected)
    {
        actual.Length.Should().Be(
            expected.Length,
            "the route snapshot must contain the same number of ordered controller-action routes " +
            "(expected {0} routes but found {1})",
            expected.Length,
            actual.Length);

        int firstDifference = Enumerable.Range(0, expected.Length)
            .FirstOrDefault(index => !string.Equals(actual[index], expected[index], StringComparison.Ordinal), -1);
        if (firstDifference >= 0)
        {
            throw new Xunit.Sdk.XunitException(
                "Route snapshot differs at index " + firstDifference + Environment.NewLine +
                "Expected: " + expected[firstDifference] + Environment.NewLine +
                "Actual:   " + actual[firstDifference] + Environment.NewLine +
                "An authorization annotation changed -- confirm this is intended before " +
                "regenerating Startup/RouteTableSnapshot.txt.");
        }

        actual.Should().Equal(
            expected,
            "the controller-action route table and declared endpoint authorization metadata must not change; " +
            "confirm an authorization annotation change is intended before regenerating " +
            "Startup/RouteTableSnapshot.txt");
    }

    private sealed class NonAuthorizationActionFilter : IActionFilter
    {
        public void OnActionExecuting(ActionExecutingContext context)
        {
        }

        public void OnActionExecuted(ActionExecutedContext context)
        {
        }
    }

    private sealed class UnknownAuthorizationFilter : IAuthorizationFilter
    {
        public void OnAuthorization(AuthorizationFilterContext context)
        {
        }
    }
}
