using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Farm.Infrastructure.Security;
using FluentAssertions;

namespace Farm.Web.Api.Tests.Security;

public sealed class CalibrationPersistenceAuthorizationTests : IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory = new(
        new Dictionary<string, string?>
        {
            ["Testing:UseTestAuthentication"] = "true",
            ["Security:DevModeBypassAuth"] = "false",
        });

    public static TheoryData<HttpMethod, string, string> ProtectedRouteCases => new()
    {
        { HttpMethod.Get, "/api/calibration-projects", PrintFarmerPermissions.Calibration.Read },
        { HttpMethod.Post, "/api/calibration-projects", PrintFarmerPermissions.Calibration.Create },
        { HttpMethod.Get, $"/api/calibration-attempts/{Guid.NewGuid()}", PrintFarmerPermissions.Calibration.Read },
        { HttpMethod.Post, $"/api/calibration-attempts/{Guid.NewGuid()}/events", PrintFarmerPermissions.Calibration.Update },
        { HttpMethod.Get, "/api/calibration-sync/changes", PrintFarmerPermissions.Calibration.Read },
        { HttpMethod.Post, "/api/calibration-sync/apply", PrintFarmerPermissions.Calibration.Update },
        { HttpMethod.Post, "/api/calibration-imports/legacy-v4", PrintFarmerPermissions.Calibration.Create },
        { HttpMethod.Post, $"/api/calibration-generated-profiles/{Guid.NewGuid()}/publish", PrintFarmerPermissions.Calibration.Publish },
    };

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task CalibrationPersistence_AnonymousCaller_ReturnsAuthenticationRequired()
    {
        using HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Anonymous", "true");

        HttpResponseMessage response = await client.GetAsync("/api/calibration-projects");
        string body = await response.Content.ReadAsStringAsync();

        _ = response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, body);
        using JsonDocument document = JsonDocument.Parse(body);
        _ = document.RootElement.GetProperty("code").GetString()
            .Should().Be("authentication_required");
    }

    [Theory]
    [MemberData(nameof(ProtectedRouteCases))]
    public async Task CalibrationPersistence_WithoutRequiredPermission_ReturnsPermissionDenied(
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
    public async Task CalibrationPersistence_WithRequiredPermission_PassesAuthorization(
        HttpMethod method,
        string route,
        string requiredPermission)
    {
        using HttpClient client = CreateOperatorClient(requiredPermission);
        using HttpRequestMessage request = CreateRequest(method, route);

        HttpResponseMessage response = await client.SendAsync(request);

        _ = response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
        _ = response.StatusCode.Should().NotBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task CalibrationPersistence_UnsupportedExplicitContract_ReturnsUpgradeRequired()
    {
        using HttpClient client = CreateOperatorClient(PrintFarmerPermissions.Calibration.Read);
        client.DefaultRequestHeaders.Add("X-PrintFarmer-Api-Contract-Version", "0.9");

        HttpResponseMessage response = await client.GetAsync("/api/calibration-projects");
        string body = await response.Content.ReadAsStringAsync();

        _ = response.StatusCode.Should().Be(HttpStatusCode.UpgradeRequired, body);
        using JsonDocument document = JsonDocument.Parse(body);
        _ = document.RootElement.GetProperty("code").GetString()
            .Should().Be("client_upgrade_required");
    }

    [Fact]
    public async Task CalibrationPersistence_UnsupportedContractAndMalformedBody_ReturnsUpgradeRequiredWithHeaders()
    {
        using HttpClient client = CreateOperatorClient(PrintFarmerPermissions.Calibration.Create);
        client.DefaultRequestHeaders.Add("X-PrintFarmer-Api-Contract-Version", "0.9");
        using StringContent malformedBody = new("{", System.Text.Encoding.UTF8, "application/json");

        HttpResponseMessage response = await client.PostAsync("/api/calibration-projects", malformedBody);
        string body = await response.Content.ReadAsStringAsync();

        _ = response.StatusCode.Should().Be(HttpStatusCode.UpgradeRequired, body);
        _ = response.Headers.Should().ContainKey("X-PrintFarmer-Api-Contract-Version");
        _ = response.Headers.Should().ContainKey("X-PrintFarmer-Minimum-Api-Contract-Version");
        using JsonDocument document = JsonDocument.Parse(body);
        _ = document.RootElement.GetProperty("code").GetString()
            .Should().Be("client_upgrade_required");
    }

    [Fact]
    public async Task CalibrationSync_DeleteMutationWithoutDeletePermission_ReturnsPermissionDenied()
    {
        using HttpClient client = CreateOperatorClient(PrintFarmerPermissions.Calibration.Update);
        using HttpRequestMessage request = new(HttpMethod.Post, "/api/calibration-sync/apply")
        {
            Content = JsonContent.Create(new
            {
                mutations = new[]
                {
                    new
                    {
                        clientId = "desktop",
                        operationId = "delete-1",
                        operationType = " project.delete ",
                        projectId = Guid.NewGuid(),
                        baseRevision = 1,
                        payload = new { },
                        dependencies = Array.Empty<string>(),
                    },
                },
            }),
        };

        HttpResponseMessage response = await client.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();

        _ = response.StatusCode.Should().Be(HttpStatusCode.Forbidden, body);
        using JsonDocument document = JsonDocument.Parse(body);
        _ = document.RootElement.GetProperty("code").GetString()
            .Should().Be("permission_denied");
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

    private static HttpRequestMessage CreateRequest(HttpMethod method, string route) =>
        new(method, route)
        {
            Content = method == HttpMethod.Get ? null : JsonContent.Create(new { }),
        };
}
