extern alias PrinterDiscoveryRef;

using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Modules.Printers.Controllers;
using Farm.Modules.Printers.Services.Discovery;
using Farm.Web.Api.Controllers;
using Farm.Web.Api.Tests.TestInfrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Xunit;

// The broadcaster type lives in the aliased PrinterDiscoveryService assembly (see csproj
// Aliases="PrinterDiscoveryRef"); alias its constants for readable references below.
using DiscoveryProgressBroadcaster = PrinterDiscoveryRef::PrinterDiscovery.Services.DiscoveryProgressBroadcaster;

namespace Farm.Web.Api.Tests.Controllers;

/// <summary>
/// Regression coverage for the two integration blockers that broke the calibration API's
/// authenticated discovery event relay:
///   B1: DiscoveryProgressBroadcaster POSTed to routes the InternalDiscoveryEventsController
///       did not expose (missing "/events" segment), so requests never reached the controller.
///   B2: The controller relied on the X-Discovery-Service-Key header validated by
///       <see cref="DiscoveryServiceAuthenticator"/>, but lacked [AllowAnonymous], so the
///       global JWT fallback policy returned 401 before the key authenticator could execute.
/// </summary>
public class InternalDiscoveryEventsControllerTests : IClassFixture<InternalDiscoveryEventsControllerTests.Factory>, IAsyncLifetime
{
    public class Factory : CustomWebApplicationFactory
    {
        public Factory() : base(new Dictionary<string, string?>
        {
            ["Security:DevModeBypassAuth"] = "false",
            ["DiscoveryAuth:SharedKey"] = SharedKey,
        })
        {
        }
    }

    private const string SharedKeyHeaderName = "X-Discovery-Service-Key";
    private const string SharedKey = "test-discovery-shared-key";

    private readonly Factory _factory;

    public InternalDiscoveryEventsControllerTests(Factory factory)
    {
        _factory = factory;
    }

    public async Task InitializeAsync()
    {
        // DevModeBypassAuth=false ensures the real JWT fallback policy is active — this is
        // what caused B2 in the original bug (401 before the controller's key check ran).
        await _factory.ResetDataAsync();
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // B1: Broadcaster path constants must equal controller [Route] + [HttpPost] templates.
    //
    // Both sides are pinned to their real source of truth: the broadcaster's public
    // constants and the controller's actual routing attributes (via reflection). If either
    // side changes, this test fails, preventing silent drift.
    // ─────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(nameof(InternalDiscoveryEventsController.ProgressAsync),
                DiscoveryProgressBroadcaster.ProgressPath)]
    [InlineData(nameof(InternalDiscoveryEventsController.PrinterFoundAsync),
                DiscoveryProgressBroadcaster.PrinterFoundPath)]
    [InlineData(nameof(InternalDiscoveryEventsController.CompletedAsync),
                DiscoveryProgressBroadcaster.CompletedPath)]
    public void BroadcasterPath_MatchesControllerRouteTemplate(string actionName, string broadcasterPath)
    {
        string controllerTemplate = GetControllerRouteBase();
        string actionTemplate = GetActionHttpPostTemplate(actionName);
        string composed = $"{controllerTemplate.TrimEnd('/')}/{actionTemplate.TrimStart('/')}";

        composed.Should().Be(
            broadcasterPath,
            "the broadcaster's request path constant must equal the controller's [Route] + [HttpPost] template");
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // B2: With [AllowAnonymous] bypassing the JWT fallback policy, a request bearing a valid
    // X-Discovery-Service-Key header (and no JWT) must reach the controller action rather
    // than being short-circuited at 401 by the global authorization policy.
    //
    // A request that reaches the action but references an unknown session receives the
    // controller-emitted 404 ProblemDetails with code=resource_not_found — which proves both
    // the route resolved AND the shared-key authenticator accepted the request.
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ProgressEndpoint_WithValidSharedKeyAndNoJwt_ReachesActionAndReturnsSessionNotFound()
    {
        using HttpClient client = _factory.CreateClient();
        HttpRequestMessage request = BuildRequest(
            DiscoveryProgressBroadcaster.ProgressPath,
            new InternalDiscoveryProgressDto(
                SessionId: "unknown-session",
                TotalIps: 0,
                ScannedIps: 0,
                PrintersFound: 0,
                PrintersExcluded: 0,
                ProgressPercentage: 0,
                Status: DiscoveryStatus.Scanning,
                Message: null,
                AutoDetectedNetworks: false));

        HttpResponseMessage response = await client.SendAsync(request);

        await AssertControllerReachedWithSessionNotFoundAsync(response);
    }

    [Fact]
    public async Task PrinterFoundEndpoint_WithValidSharedKeyAndNoJwt_ReachesActionAndReturnsSessionNotFound()
    {
        using HttpClient client = _factory.CreateClient();
        HttpRequestMessage request = BuildRequest(
            DiscoveryProgressBroadcaster.PrinterFoundPath,
            new InternalDiscoveryPrinterFoundDto(
                SessionId: "unknown-session",
                Name: "test",
                ServerUrl: "http://localhost",
                OriginalServerUrl: null,
                IpAddress: "127.0.0.1",
                Backend: PrinterBackend.Moonraker,
                BackendPort: null,
                FrontendPort: null,
                CameraStreamUrl: null,
                CameraSnapshotUrl: null,
                Manufacturer: null,
                Model: null,
                Notes: null,
                DiscoveredAt: DateTime.UtcNow,
                IsReachable: true));

        HttpResponseMessage response = await client.SendAsync(request);

        await AssertControllerReachedWithSessionNotFoundAsync(response);
    }

    [Fact]
    public async Task CompletedEndpoint_WithValidSharedKeyAndNoJwt_ReachesActionAndReturnsSessionNotFound()
    {
        using HttpClient client = _factory.CreateClient();
        HttpRequestMessage request = BuildRequest(
            DiscoveryProgressBroadcaster.CompletedPath,
            new InternalDiscoveryCompletedDto(
                SessionId: "unknown-session",
                TotalPrintersFound: 0,
                TotalPrintersExcluded: 0,
                Duration: TimeSpan.FromSeconds(1),
                WasCancelled: false,
                AutoDetectedNetworks: false));

        HttpResponseMessage response = await client.SendAsync(request);

        await AssertControllerReachedWithSessionNotFoundAsync(response);
    }

    [Fact]
    public async Task ProgressEndpoint_WithoutSharedKey_ReturnsControllerAuthenticationRequired()
    {
        using HttpClient client = _factory.CreateClient();
        HttpRequestMessage request = new(HttpMethod.Post, DiscoveryProgressBroadcaster.ProgressPath)
        {
            Content = JsonContent.Create(new InternalDiscoveryProgressDto(
                SessionId: "unknown-session",
                TotalIps: 0,
                ScannedIps: 0,
                PrintersFound: 0,
                PrintersExcluded: 0,
                ProgressPercentage: 0,
                Status: DiscoveryStatus.Scanning,
                Message: null,
                AutoDetectedNetworks: false)),
        };
        // Intentionally no X-Discovery-Service-Key header, no JWT.

        HttpResponseMessage response = await client.SendAsync(request);

        // Fail-closed: still denied, but by the controller's own key authenticator with the
        // documented ProblemDetails shape (code=authentication_required), NOT by the global
        // fallback policy short-circuiting to a bare 401.
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        string body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"code\":\"authentication_required\"");
    }

    [Fact]
    public async Task ProgressEndpoint_WithInvalidSharedKey_ReturnsControllerAuthenticationRequired()
    {
        using HttpClient client = _factory.CreateClient();
        HttpRequestMessage request = BuildRequest(
            DiscoveryProgressBroadcaster.ProgressPath,
            new InternalDiscoveryProgressDto(
                SessionId: "unknown-session",
                TotalIps: 0,
                ScannedIps: 0,
                PrintersFound: 0,
                PrintersExcluded: 0,
                ProgressPercentage: 0,
                Status: DiscoveryStatus.Scanning,
                Message: null,
                AutoDetectedNetworks: false),
            sharedKey: "not-the-real-key");

        HttpResponseMessage response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        string body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"code\":\"authentication_required\"");
    }

    [Fact]
    public void SharedKeyActionsAdvertiseAllowAnonymous_SoJwtFallbackPolicyDoesNotShortCircuit()
    {
        // Action-level [AllowAnonymous] lets each reviewed shared-key route run its own
        // X-Discovery-Service-Key authentication without making future controller actions public.
        Type controllerType = typeof(InternalDiscoveryEventsController);
        string[] actionNames =
        [
            nameof(InternalDiscoveryEventsController.ProgressAsync),
            nameof(InternalDiscoveryEventsController.PrinterFoundAsync),
            nameof(InternalDiscoveryEventsController.CompletedAsync),
        ];

        foreach (string actionName in actionNames)
        {
            controllerType.GetMethod(actionName)!
                .GetCustomAttribute<AllowAnonymousAttribute>(inherit: true)
                .Should().NotBeNull(
                    $"{actionName} must opt out of JWT fallback so its shared-key authenticator can run");
        }

        controllerType.GetCustomAttribute<AllowAnonymousAttribute>(inherit: true)
            .Should().BeNull("future actions must remain protected by the global fallback policy");
    }

    private static HttpRequestMessage BuildRequest<T>(string path, T payload, string sharedKey = SharedKey)
    {
        HttpRequestMessage request = new(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(payload),
        };
        _ = request.Headers.TryAddWithoutValidation(SharedKeyHeaderName, sharedKey);
        return request;
    }

    private static async Task AssertControllerReachedWithSessionNotFoundAsync(HttpResponseMessage response)
    {
        // 404 with ProblemDetails code=resource_not_found is emitted only by the controller
        // action itself (see InternalDiscoveryEventsController.DiscoveryProblem). Any other
        // 404 — from routing, or a bare framework response — would prove the request did not
        // reach the action, which is exactly what B1 and B2 caused before the fix.
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        string body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"code\":\"resource_not_found\"");
    }

    private static string GetControllerRouteBase()
    {
        // Read the actual RouteAttribute placed on the controller so the test tracks the real
        // routing configuration rather than a copy of the string.
        RouteAttribute? routeAttribute = typeof(InternalDiscoveryEventsController)
            .GetCustomAttribute<RouteAttribute>(inherit: true);
        routeAttribute.Should().NotBeNull(
            "InternalDiscoveryEventsController must declare a [Route] template");
        return routeAttribute!.Template;
    }

    private static string GetActionHttpPostTemplate(string actionName)
    {
        MethodInfo? method = typeof(InternalDiscoveryEventsController)
            .GetMethod(actionName, BindingFlags.Public | BindingFlags.Instance);
        method.Should().NotBeNull($"controller must expose action '{actionName}'");
        HttpPostAttribute? attribute = method!.GetCustomAttribute<HttpPostAttribute>(inherit: true);
        attribute.Should().NotBeNull($"action '{actionName}' must declare a [HttpPost] template");
        (attribute!.Template ?? string.Empty).Should().NotBeNullOrEmpty(
            $"action '{actionName}' must supply an [HttpPost] route template segment");
        return attribute.Template!;
    }
}
