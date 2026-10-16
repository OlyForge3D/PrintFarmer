using System.Globalization;
using System.Reflection;
using Farm.Infrastructure.Authorization;
using Farm.Modules.Devices.Filters;
using Farm.Slicer.Module.Api.Filters;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using RequirePermissionAttribute = Farm.Infrastructure.Authorization.RequirePermissionAttribute;
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
/// Non-controller endpoints are covered too (issue #2821): minimal APIs (health, liveness,
/// metrics, network discovery) render as <c>endpoint</c> keyed by verb and route pattern, and
/// SignalR hubs render their connect (<c>hub:</c>) and negotiate (<c>hub-negotiate:</c>)
/// endpoints plus one <c>INVOKE /route#Method</c> line per client-invocable hub method.
/// </para>
/// <para>
/// Each line records independent authorization views. The first bracket is declared endpoint
/// metadata only: <see cref="IAllowAnonymous"/>, <see cref="IAuthorizeData"/>, and
/// <see cref="IPermissionMetadata"/>, falling back to the configured fallback policy's requirement
/// types. The second bracket (<c>[effective=...]</c>, issue #2831) is the policy the authorization
/// middleware actually evaluates, with named, default, and fallback policies resolved to their
/// requirements and configuration (see <see cref="BuildEffectiveAuthorizationAsync"/>), so
/// redefining a policy without touching any endpoint still changes the snapshot. The optional third
/// bracket (<c>[filter-auth=...]</c>) is projected from the action's
/// runtime MVC filter pipeline (<see cref="ActionDescriptor.FilterDescriptors"/>, without
/// re-parsing attributes): every <see cref="IAuthorizationFilter"/> /
/// <see cref="IAsyncAuthorizationFilter"/> (for example the OctoPrint API-key filter), plus the
/// known authorization action filters in the slicer module (filter-based
/// <c>RequirePermissionAttribute</c> and the slicer API-key filters). It is emitted even when the
/// route is anonymous at the endpoint-metadata level. Not observed: authorization performed by other
/// action filters, filter factories, middleware, hub filters, or handler/hub-method bodies.
/// </para>
/// <para>
/// Renaming a controller/action or intentionally changing a route requires regenerating the
/// snapshot deliberately (see <see cref="BuildRouteTableAsync"/>) and reviewing the diff -- it must
/// never be regenerated reflexively to make a failing test pass.
/// </para>
/// </summary>
[Collection("SlicerDisabled")]
public sealed class RouteTableSnapshotTests
{
    private static readonly string SnapshotPath = Path.GetFullPath(
        Path.Join(AppContext.BaseDirectory, "..", "..", "..", "Startup", "RouteTableSnapshot.txt"));

    [Fact]
    public async Task ControllerActionRouteTable_MatchesCheckedInSnapshot()
    {
        using RouteTopologyWebApplicationFactory factory = RouteTopologyWebApplicationFactory.SlicerEnabled();

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
        using RouteTopologyWebApplicationFactory factory = RouteTopologyWebApplicationFactory.SlicerEnabled();

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
                ? line[..line.IndexOf(" [", StringComparison.Ordinal)] + " [auth=anonymous] [effective=anonymous]"
                : line)
            .ToArray();

        Action assertRegression = () => AssertSnapshotMatches(regressed, expected);

        _ = assertRegression.Should().Throw<Xunit.Sdk.XunitException>()
            .Which.Message.Should().Contain("DELETE /api/admin/roles/{roleId:guid}");
    }

    [Fact]
    public async Task ControllerActionRouteTable_RecordsDeclaredAuthorizationMetadata()
    {
        using RouteTopologyWebApplicationFactory factory = RouteTopologyWebApplicationFactory.SlicerEnabled();

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
            line.EndsWith("[auth=anonymous:AllowAnonymousAttribute] [effective=anonymous]", StringComparison.Ordinal));
        _ = actual.Should().Contain(line =>
            line.Contains(
                "UnifiedSettingsController.GetSettingsByKeyName",
                StringComparison.Ordinal) &&
            line.EndsWith("[auth=anonymous:AllowAnonymousAttribute] [effective=anonymous]", StringComparison.Ordinal));
        _ = actual.Should().Contain(line =>
            line.Contains(
                "POST /api/files/local",
                StringComparison.Ordinal) &&
            line.Contains("catalog-permission=queue:write", StringComparison.Ordinal));
        _ = actual.Count(line => line.Contains("|permission=", StringComparison.Ordinal))
            .Should().BeGreaterThan(0);
        _ = actual.Count(line => line.Contains("[auth=anonymous:", StringComparison.Ordinal))
            .Should().BeGreaterThan(0);
        _ = actual.Count(line => line.EndsWith("[auth=fallback:DenyAnonymousAuthorizationRequirement] [effective=authenticated]", StringComparison.Ordinal))
            .Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task RouteTable_CoversMinimalApiAndSignalRHubEndpoints()
    {
        using RouteTopologyWebApplicationFactory factory = RouteTopologyWebApplicationFactory.SlicerEnabled();

        string[] actual = await BuildRouteTableAsync(
            factory.Services.GetRequiredService<EndpointDataSource>(),
            factory.Services.GetRequiredService<IAuthorizationPolicyProvider>());

        _ = actual.Should().Contain(
            "GET /livez -> endpoint [auth=anonymous:AllowAnonymousAttribute] [effective=anonymous]");
        _ = actual.Should().Contain(
            "POST /api/network-discovery/auto-detect -> endpoint " +
            "[auth=policy=;roles=;schemes=|permission=network_discovery:admin] " +
            "[effective=authenticated+permission:network_discovery:admin]");
        _ = actual.Should().Contain(line =>
            line.Contains(" /hubs/printers -> hub:", StringComparison.Ordinal) &&
            line.Contains(typeof(Farm.Infrastructure.Services.SignalR.PrinterHub).FullName!, StringComparison.Ordinal) &&
            line.EndsWith("[effective=authenticated]", StringComparison.Ordinal));
        _ = actual.Should().Contain(line =>
            line.Contains(" /hubs/printers/negotiate -> hub-negotiate:", StringComparison.Ordinal) &&
            line.EndsWith("[effective=authenticated]", StringComparison.Ordinal));
        _ = actual.Should().Contain(line =>
            line.StartsWith("INVOKE /hubs/printers#", StringComparison.Ordinal),
            "client-invocable hub methods must be part of the authorization snapshot");
    }

    /// <summary>
    /// Microservices (and ARM-default) startup drops the inline slicer module and maps
    /// authenticated stub endpoints instead. The canonical snapshot covers the slicer-enabled
    /// topology, so this pins the other branch: the only lines it may add are the reviewed stubs,
    /// and every line it keeps must be byte-identical to the canonical snapshot.
    /// </summary>
    [Fact]
    public async Task RouteTable_MicroservicesModeReplacesSlicerSurfaceWithAuthenticatedStubs()
    {
        using RouteTopologyWebApplicationFactory factory = RouteTopologyWebApplicationFactory.Microservices();

        string[] actual = await BuildRouteTableAsync(
            factory.Services.GetRequiredService<EndpointDataSource>(),
            factory.Services.GetRequiredService<IAuthorizationPolicyProvider>());
        HashSet<string> canonical = new(File.ReadAllLines(SnapshotPath), StringComparer.Ordinal);

        _ = actual.Where(line => !canonical.Contains(line)).Should().BeEquivalentTo(
            "GET /api/3d-models -> endpoint [auth=policy=;roles=;schemes=] [effective=authenticated]",
            "GET /api/3d-models/folders -> endpoint [auth=policy=;roles=;schemes=] [effective=authenticated]",
            "POST /api/3d-models/query -> endpoint [auth=policy=;roles=;schemes=] [effective=authenticated]");
        _ = actual.Should().NotContain(line =>
            line.Contains("Farm.Slicer.Module", StringComparison.Ordinal) ||
            line.Contains("/hubs/slicer", StringComparison.Ordinal),
            "the slicer module and its hubs run in slicer-host in microservices mode");

        string[] removed = canonical.Except(actual, StringComparer.Ordinal).ToArray();
        _ = removed.Should().NotBeEmpty().And.OnlyContain(
            line => line.Contains("Farm.Slicer.Module", StringComparison.Ordinal),
            "microservices mode must remove only slicer-module routes");
    }

    [Fact]
    public async Task RouteTable_ReportsHubMethodRequirementDataThatSignalRDoesNotEnforce()
    {
        using RouteTopologyWebApplicationFactory factory = RouteTopologyWebApplicationFactory.SlicerEnabled();

        string[] actual = await BuildRouteTableAsync(
            factory.Services.GetRequiredService<EndpointDataSource>(),
            factory.Services.GetRequiredService<IAuthorizationPolicyProvider>());

        // SignalR's hub dispatcher evaluates only the IAuthorizeData on a hub method; a
        // [RequirePermission] there contributes the default policy, not its permission requirement.
        _ = actual.Should().ContainSingle(line =>
                line.StartsWith("INVOKE /hubs/harvest#BroadcastFileProgressAsync ", StringComparison.Ordinal))
            .Which.Should().EndWith(
                "[effective=authenticated] [unenforced-requirement-data=permission:gcode_harvest:admin]");
    }

    [Fact]
    public async Task EffectiveAuthorization_ChangesWhenNamedPolicyIsRedefined()
    {
        EndpointMetadataCollection metadata = new(new AuthorizeAttribute("FarmOperators"));

        IAuthorizationPolicyProvider original = CreatePolicyProvider(options =>
            options.AddPolicy("FarmOperators", policy => policy.RequireRole("operator")));
        IAuthorizationPolicyProvider redefined = CreatePolicyProvider(options =>
            options.AddPolicy("FarmOperators", policy => policy.RequireAssertion(_ => true)));

        string declaredOriginal = await BuildAuthorizationAsync(metadata, original);
        string declaredRedefined = await BuildAuthorizationAsync(metadata, redefined);
        string effectiveOriginal = await BuildEffectiveAuthorizationAsync(metadata, original);
        string effectiveRedefined = await BuildEffectiveAuthorizationAsync(metadata, redefined);

        _ = declaredRedefined.Should().Be(
            declaredOriginal,
            "the declared view records only the policy name, which is the gap issue #2831 reports");
        _ = effectiveOriginal.Should().Be("effective=roles:operator");
        _ = effectiveRedefined.Should().Be("effective=assertion");
    }

    [Fact]
    public async Task EffectiveAuthorization_ChangesWhenDefaultPolicyIsRedefinedOnRealEndpoint()
    {
        using RouteTopologyWebApplicationFactory factory = RouteTopologyWebApplicationFactory.SlicerEnabled();
        EndpointMetadataCollection metadata = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Single(endpoint =>
                endpoint.RoutePattern.RawText == "api/admin/roles/{roleId:guid}" &&
                endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains("DELETE") == true)
            .Metadata;

        IAuthorizationPolicyProvider appProvider = factory.Services.GetRequiredService<IAuthorizationPolicyProvider>();
        AuthorizationPolicy? appFallback = await appProvider.GetFallbackPolicyAsync();
        IAuthorizationPolicyProvider weakenedDefault = CreatePolicyProvider(options =>
        {
            options.DefaultPolicy = new AuthorizationPolicyBuilder().RequireAssertion(_ => true).Build();
            options.FallbackPolicy = appFallback;
        });

        _ = (await BuildAuthorizationAsync(metadata, weakenedDefault)).Should().Be(
            await BuildAuthorizationAsync(metadata, appProvider));
        _ = (await BuildEffectiveAuthorizationAsync(metadata, appProvider)).Should().Be(
            "effective=authenticated+permission:roles:admin");
        _ = (await BuildEffectiveAuthorizationAsync(metadata, weakenedDefault)).Should().Be(
            "effective=assertion+permission:roles:admin",
            "redefining the default policy behind [RequirePermission] must surface in the snapshot");
    }

    [Fact]
    public async Task EffectiveAuthorization_RendersRequirementConfiguration()
    {
        IAuthorizationPolicyProvider provider = CreatePolicyProvider(_ => { });

        _ = (await BuildEffectiveAuthorizationAsync(
                new EndpointMetadataCollection(new TestRequirementData(new DesktopScopeRequirement("ModelRead"))),
                provider))
            .Should().Be("effective=desktop-scope:ModelRead");
        _ = (await BuildEffectiveAuthorizationAsync(
                new EndpointMetadataCollection(new TestRequirementData(new UnknownRequirement { Limit = 3, Name = "x" })),
                provider))
            .Should().Be($"effective=requirement:{typeof(UnknownRequirement).FullName}(Limit=3,Name=x)");
        _ = (await BuildEffectiveAuthorizationAsync(new EndpointMetadataCollection(), provider))
            .Should().Be("effective=none");
    }

    private static DefaultAuthorizationPolicyProvider CreatePolicyProvider(Action<AuthorizationOptions> configure)
    {
        AuthorizationOptions options = new();
        configure(options);
        return new DefaultAuthorizationPolicyProvider(Microsoft.Extensions.Options.Options.Create(options));
    }

    [Fact]
    public void ControllerActionRouteTable_BindsOctoPrintApiKeyFiltersToPublicActions()
    {
        using RouteTopologyWebApplicationFactory factory = RouteTopologyWebApplicationFactory.SlicerEnabled();

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
        using RouteTopologyWebApplicationFactory factory = RouteTopologyWebApplicationFactory.SlicerEnabled();

        string[] actual = await BuildRouteTableAsync(
            factory.Services.GetRequiredService<EndpointDataSource>(),
            factory.Services.GetRequiredService<IAuthorizationPolicyProvider>());

        const string octoPrintSuffix =
            "[auth=anonymous:AllowAnonymousAttribute] [effective=anonymous] [filter-auth=octoprint-api-key:requireValidKeyForAnonymous=false]";
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
        using RouteTopologyWebApplicationFactory factory = RouteTopologyWebApplicationFactory.SlicerEnabled();

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
    /// Builds the sorted, checked-in-snapshot line format: one line per runtime
    /// <see cref="RouteEndpoint"/> (controller actions, minimal APIs, and SignalR hub
    /// connect/negotiate endpoints) plus one <c>INVOKE</c> line per client-invocable hub method.
    /// Each line lists every HTTP verb it accepts, its route template, declared endpoint
    /// authorization metadata, the resolved <c>[effective=...]</c> requirements, its
    /// <c>Assembly::Type.Member</c> identity (or <c>endpoint</c>/<c>hub</c> kind), and (when present) its
    /// filter-based authorization projected by
    /// <see cref="BuildFilterAuthorization"/>. Handler-body authorization is not observed.
    /// The assembly qualifier deliberately makes
    /// two identically-named controllers in different assemblies produce distinct lines (see
    /// class remarks); no <c>Distinct()</c> is applied afterward, so a genuine duplicate route
    /// registration -- which this format could otherwise mask -- instead surfaces as a real
    /// diff against the snapshot rather than being silently deduplicated away.
    /// </summary>
    internal static async Task<string[]> BuildRouteTableAsync(
        EndpointDataSource endpointDataSource,
        IAuthorizationPolicyProvider policyProvider)
    {
        RouteEndpoint[] endpoints = endpointDataSource.Endpoints.OfType<RouteEndpoint>().ToArray();
        List<string> lines = new(endpoints.Length);
        foreach (RouteEndpoint endpoint in endpoints)
        {
            lines.Add(await BuildEndpointLineAsync(endpoint, policyProvider));
            HubMetadata? hub = endpoint.Metadata.GetMetadata<HubMetadata>();
            if (hub is not null && endpoint.Metadata.GetMetadata<NegotiateMetadata>() is null)
            {
                lines.AddRange(await BuildHubMethodLinesAsync(
                    hub.HubType, NormalizeRoute(endpoint.RoutePattern.RawText), policyProvider));
            }
        }

        return lines
            .OrderBy(line => line, StringComparer.Ordinal)
            .ToArray();
    }

    private static async Task<string> BuildEndpointLineAsync(
        RouteEndpoint endpoint,
        IAuthorizationPolicyProvider policyProvider)
    {
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

        ControllerActionDescriptor? action = endpoint.Metadata.GetMetadata<ControllerActionDescriptor>();
        string template;
        string identity;
        string filterAuthorization = string.Empty;
        if (action is not null)
        {
            template = action.AttributeRouteInfo?.Template ?? string.Empty;
            string assemblyName = action.ControllerTypeInfo.Assembly.GetName().Name ?? "?";
            identity = $"{assemblyName}::{action.ControllerTypeInfo.FullName}.{action.MethodInfo.Name}";
            filterAuthorization = BuildFilterAuthorization(action.FilterDescriptors);
        }
        else
        {
            template = NormalizeRoute(endpoint.RoutePattern.RawText);
            identity = DescribeNonControllerEndpoint(endpoint.Metadata);
        }

        string authorization = await BuildAuthorizationAsync(endpoint.Metadata, policyProvider);
        string effective = await BuildEffectiveAuthorizationAsync(endpoint.Metadata, policyProvider);
        return filterAuthorization.Length == 0
            ? $"{methods} /{template} -> {identity} [{authorization}] [{effective}]"
            : $"{methods} /{template} -> {identity} [{authorization}] [{effective}] [{filterAuthorization}]";
    }

    /// <summary>
    /// Stable identity for an endpoint that is not a controller action. Compiler-generated
    /// handler names and <see cref="Endpoint.DisplayName"/> are deliberately not used: they are
    /// framework- and compiler-generated and change across upgrades without any behavior change.
    /// The route pattern and verbs (rendered by the caller) identify the endpoint; this adds its
    /// kind, and for SignalR the assembly-qualified hub type.
    /// </summary>
    private static string DescribeNonControllerEndpoint(EndpointMetadataCollection metadata)
    {
        HubMetadata? hub = metadata.GetMetadata<HubMetadata>();
        if (hub is not null)
        {
            string kind = metadata.GetMetadata<NegotiateMetadata>() is null ? "hub" : "hub-negotiate";
            return $"{kind}:{DescribeType(hub.HubType)}";
        }

        return "endpoint";
    }

    private static string DescribeType(Type type) =>
        $"{type.Assembly.GetName().Name ?? "?"}::{type.FullName}";

    private static string NormalizeRoute(string? rawText) => (rawText ?? string.Empty).TrimStart('/');

    /// <summary>
    /// Projects the SignalR hub methods a client can invoke on a connected hub, mirroring the
    /// framework's dispatcher (<c>DefaultHubDispatcher</c> / <c>HubMethodDescriptor</c>): a hub
    /// method is authorized only by the <see cref="AuthorizeAttribute"/> instances declared on the
    /// method, combined through <see cref="AuthorizationPolicy.CombineAsync(IAuthorizationPolicyProvider, IEnumerable{IAuthorizeData})"/>.
    /// Methods with none are guarded only by the hub connection endpoint. Requirements carried by
    /// <see cref="IAuthorizationRequirementData"/> on a hub method are <b>not</b> evaluated by the
    /// SignalR dispatcher, so they are reported separately as <c>unenforced-requirement-data</c>
    /// rather than folded into the effective policy.
    /// </summary>
    private static async Task<IEnumerable<string>> BuildHubMethodLinesAsync(
        Type hubType,
        string route,
        IAuthorizationPolicyProvider policyProvider)
    {
        List<string> lines = [];
        foreach (MethodInfo method in hubType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!IsHubMethod(method, hubType))
            {
                continue;
            }

            string name = method.GetCustomAttribute<HubMethodNameAttribute>()?.Name ?? method.Name;
            // Mirrors SignalR's DefaultHubDispatcher, which reads exactly AuthorizeAttribute.
            AuthorizeAttribute[] authorizeData = method.GetCustomAttributes<AuthorizeAttribute>(inherit: true).ToArray();
            string effective = authorizeData.Length == 0
                ? "effective=connection-only"
                : $"effective={DescribePolicy((await AuthorizationPolicy.CombineAsync(policyProvider, authorizeData))!)}";
            string[] unenforced = method.GetCustomAttributes(inherit: true)
                .OfType<IAuthorizationRequirementData>()
                .SelectMany(data => data.GetRequirements())
                .Select(DescribeRequirement)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(requirement => requirement, StringComparer.Ordinal)
                .ToArray();
            string line = $"INVOKE /{route}#{name} -> hub-method:{DescribeType(hubType)}.{method.Name} [{effective}]";
            lines.Add(unenforced.Length == 0
                ? line
                : $"{line} [unenforced-requirement-data={string.Join("+", unenforced)}]");
        }

        return lines;
    }

    // Mirrors SignalR's HubReflectionHelper.GetHubMethods: public instance methods, minus
    // IDisposable's, special-name members, and members declared by object or Hub/Hub<T>.
    private static bool IsHubMethod(MethodInfo method, Type hubType)
    {
        if (method.IsSpecialName)
        {
            return false;
        }

        if (typeof(IDisposable).IsAssignableFrom(hubType) &&
            hubType.GetInterfaceMap(typeof(IDisposable)).TargetMethods.Contains(method))
        {
            return false;
        }

        Type baseDefinition = method.GetBaseDefinition().DeclaringType!;
        if (baseDefinition == typeof(object))
        {
            return false;
        }

        Type baseType = baseDefinition.IsGenericType ? baseDefinition.GetGenericTypeDefinition() : baseDefinition;
        return baseType != typeof(Hub) && baseType != typeof(Hub<>);
    }

    /// <summary>
    /// Resolves the authorization policy the endpoint-routing <c>AuthorizationMiddleware</c>
    /// actually evaluates for this endpoint (issue #2831): every <see cref="IAuthorizeData"/> and
    /// <see cref="AuthorizationPolicy"/> metadata item combined through the policy provider --
    /// which resolves named policies to their requirements, folds in the default policy for a
    /// bare <c>[Authorize]</c>, and falls back to the fallback policy when nothing is declared --
    /// then every <see cref="IAuthorizationRequirementData"/> requirement appended. Requirements
    /// are rendered with their configuration, so redefining a named, default, or fallback policy
    /// changes this projection even when the declared metadata is byte-identical.
    /// <c>effective=anonymous</c> means the middleware skips authorization because of
    /// <see cref="IAllowAnonymous"/>; <c>effective=none</c> means no policy applies at all.
    /// </summary>
    internal static async Task<string> BuildEffectiveAuthorizationAsync(
        EndpointMetadataCollection metadata,
        IAuthorizationPolicyProvider policyProvider)
    {
        AuthorizationPolicy? policy = await AuthorizationPolicy.CombineAsync(
            policyProvider,
            metadata.GetOrderedMetadata<IAuthorizeData>(),
            metadata.GetOrderedMetadata<AuthorizationPolicy>());

        IReadOnlyList<IAuthorizationRequirementData> requirementData =
            metadata.GetOrderedMetadata<IAuthorizationRequirementData>();
        if (requirementData.Count > 0)
        {
            AuthorizationPolicyBuilder requirementPolicy = new();
            foreach (IAuthorizationRequirementData data in requirementData)
            {
                foreach (IAuthorizationRequirement requirement in data.GetRequirements())
                {
                    _ = requirementPolicy.AddRequirements(requirement);
                }
            }

            policy = policy is null
                ? requirementPolicy.Build()
                : AuthorizationPolicy.Combine(policy, requirementPolicy.Build());
        }

        if (policy is null)
        {
            return "effective=none";
        }

        return metadata.GetMetadata<IAllowAnonymous>() is not null
            ? "effective=anonymous"
            : $"effective={DescribePolicy(policy)}";
    }

    private static string DescribePolicy(AuthorizationPolicy policy)
    {
        string[] requirements = policy.Requirements
            .Select(DescribeRequirement)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(requirement => requirement, StringComparer.Ordinal)
            .ToArray();
        string[] schemes = policy.AuthenticationSchemes
            .Distinct(StringComparer.Ordinal)
            .OrderBy(scheme => scheme, StringComparer.Ordinal)
            .ToArray();
        string described = requirements.Length == 0 ? "no-requirements" : string.Join("+", requirements);
        return schemes.Length == 0 ? described : $"{described};schemes={string.Join("+", schemes)}";
    }

    private static string DescribeRequirement(IAuthorizationRequirement requirement) => requirement switch
    {
        DenyAnonymousAuthorizationRequirement => "authenticated",
        RequirePermissionAttribute permission => $"permission:{permission.Permission}",
        DesktopScopeRequirement scope => $"desktop-scope:{scope.RequiredScope}",
        RolesAuthorizationRequirement roles =>
            $"roles:{string.Join(",", roles.AllowedRoles.Order(StringComparer.Ordinal))}",
        ClaimsAuthorizationRequirement claim =>
            $"claim:{claim.ClaimType}={string.Join(",", (claim.AllowedValues ?? []).Order(StringComparer.Ordinal))}",
        NameAuthorizationRequirement name => $"name:{name.RequiredName}",
        AssertionRequirement => "assertion",
        _ => DescribeUnknownRequirement(requirement)
    };

    /// <summary>
    /// Renders a requirement type this projection does not know explicitly by its full type name
    /// plus its declared public scalar properties, so a new or reconfigured custom requirement
    /// still produces a snapshot diff rather than collapsing to an opaque name.
    /// </summary>
    private static string DescribeUnknownRequirement(IAuthorizationRequirement requirement)
    {
        Type type = requirement.GetType();
        string[] properties = type
            .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(property => property.GetIndexParameters().Length == 0 &&
                (property.PropertyType.IsPrimitive || property.PropertyType.IsEnum ||
                 property.PropertyType == typeof(string)))
            .OrderBy(property => property.Name, StringComparer.Ordinal)
            .Select(property => $"{property.Name}={Convert.ToString(property.GetValue(requirement), CultureInfo.InvariantCulture)}")
            .ToArray();
        return properties.Length == 0
            ? $"requirement:{type.FullName}"
            : $"requirement:{type.FullName}({string.Join(",", properties)})";
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

    private sealed class TestRequirementData(IAuthorizationRequirement requirement) : IAuthorizationRequirementData
    {
        public IEnumerable<IAuthorizationRequirement> GetRequirements()
        {
            yield return requirement;
        }
    }

    private sealed class UnknownRequirement : IAuthorizationRequirement
    {
        public int Limit { get; init; }

        public string? Name { get; init; }
    }
}
