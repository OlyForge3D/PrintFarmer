using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Farm.Infrastructure.Security;
using FluentAssertions;

namespace Farm.Web.Api.Tests.Security;

public sealed class QueueAuthorizationTests : IAsyncLifetime, IDisposable
{
    private readonly CustomWebApplicationFactory _factory = new(
        new Dictionary<string, string?>
        {
            ["Testing:UseTestAuthentication"] = "true",
            ["Security:DevModeBypassAuth"] = "false",
        });

    public static TheoryData<HttpMethod, string, string> ProtectedRouteCases => new()
    {
        { HttpMethod.Get, "/api/job-queue", PrintFarmerPermissions.Queue.Read },
        { HttpMethod.Get, $"/api/job-queue/{Guid.NewGuid()}", PrintFarmerPermissions.Queue.Read },
        { HttpMethod.Put, $"/api/job-queue/{Guid.NewGuid()}", PrintFarmerPermissions.Queue.Write },
        { HttpMethod.Post, $"/api/job-queue/{Guid.NewGuid()}/dispatch", PrintFarmerPermissions.Queue.Start },
        { HttpMethod.Post, $"/api/job-queue/{Guid.NewGuid()}/cancel", PrintFarmerPermissions.Queue.Cancel },
        { HttpMethod.Post, "/api/job-queue/sync-orphaned", PrintFarmerPermissions.Queue.Reconcile },
        { HttpMethod.Post, $"/api/auto-dispatch/{Guid.NewGuid()}/ready", PrintFarmerPermissions.Queue.AcknowledgeBedClear },
        { HttpMethod.Get, "/api/dispatch-settings", PrintFarmerPermissions.DispatchSettings.Manage },
    };

    public static TheoryData<HttpMethod, string> AnonymousRouteCases => new()
    {
        { HttpMethod.Get, "/api/job-queue" },
        { HttpMethod.Get, $"/api/job-queue/{Guid.NewGuid()}" },
        { HttpMethod.Post, "/api/job-queue/sync-orphaned" },
    };

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    public void Dispose() => _factory.Dispose();

    [Theory]
    [MemberData(nameof(AnonymousRouteCases))]
    public async Task ProtectedQueueRoute_AnonymousCaller_ReturnsAuthenticationRequired(
        HttpMethod method,
        string route)
    {
        using HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Anonymous", "true");
        using HttpRequestMessage request = CreateRequest(method, route);

        HttpResponseMessage response = await client.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();

        _ = response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, body);
        using JsonDocument document = JsonDocument.Parse(body);
        _ = document.RootElement.GetProperty("code").GetString()
            .Should().Be("authentication_required");
    }

    [Theory]
    [MemberData(nameof(ProtectedRouteCases))]
    public async Task ProtectedQueueRoute_WithoutRequiredPermission_ReturnsPermissionDenied(
        HttpMethod method,
        string route,
        string requiredPermission)
    {
        using HttpClient client = CreateOperatorClient();
        using HttpRequestMessage request = CreateRequest(method, route);

        HttpResponseMessage response = await client.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();

        _ = response.StatusCode.Should().Be(HttpStatusCode.Forbidden, body);
        using JsonDocument document = JsonDocument.Parse(body);
        _ = document.RootElement.GetProperty("code").GetString()
            .Should().Be("permission_denied");
        _ = requiredPermission.Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [MemberData(nameof(ProtectedRouteCases))]
    public async Task ProtectedQueueRoute_WithRequiredPermission_PassesAuthorization(
        HttpMethod method,
        string route,
        string requiredPermission)
    {
        string[] permissions = route.Contains(
            "/api/auto-dispatch/",
            StringComparison.Ordinal)
            ? [requiredPermission, PrintFarmerPermissions.Queue.Start]
            : [requiredPermission];
        using HttpClient client = CreateOperatorClient(permissions);
        using HttpRequestMessage request = CreateRequest(method, route);

        HttpResponseMessage response = await client.SendAsync(request);

        _ = response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
        _ = response.StatusCode.Should().NotBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task QueueRead_FarmAdministrator_BypassesPermission()
    {
        using HttpClient client = _factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/api/job-queue");

        _ = response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task PrinterList_OperatorWithoutCalibrationPermissions_PreservesExistingAccess()
    {
        using HttpClient client = CreateOperatorClient();

        HttpResponseMessage response = await client.GetAsync("/api/printers");

        _ = response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
        _ = response.StatusCode.Should().NotBe(HttpStatusCode.Forbidden);
    }

    private HttpClient CreateOperatorClient(params string[] permissions)
    {
        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Id", Guid.NewGuid().ToString());
        client.DefaultRequestHeaders.Add("X-Test-Roles", "operator");
        if (permissions.Length > 0)
        {
            client.DefaultRequestHeaders.Add("X-Test-Permissions", string.Join(',', permissions));
        }

        return client;
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string route)
    {
        HttpRequestMessage request = new(method, route);
        if (method != HttpMethod.Get)
        {
            request.Content = JsonContent.Create(new { });
        }

        return request;
    }
}
