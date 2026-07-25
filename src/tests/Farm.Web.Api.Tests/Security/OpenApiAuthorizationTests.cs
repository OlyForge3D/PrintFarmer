using System.Net;
using System.Text.Json;
using Farm.Web.Api.Tests.TestInfrastructure;
using FluentAssertions;

namespace Farm.Web.Api.Tests.Security;

[Collection(IntegrationTestCollection.Name)]
public sealed class OpenApiAuthorizationTests : IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task OpenApiDocument_DescribesBearerRequirementsAndAuthorizationResponses()
    {
        using HttpClient client = _factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/openapi/v1.json");
        string body = await response.Content.ReadAsStringAsync();

        _ = response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement root = document.RootElement;
        JsonElement bearer = root.GetProperty("components")
            .GetProperty("securitySchemes")
            .GetProperty("Bearer");
        _ = bearer.GetProperty("type").GetString().Should().Be("http");
        _ = bearer.GetProperty("scheme").GetString().Should().Be("bearer");
        JsonElement schemes = root.GetProperty("components").GetProperty("securitySchemes");
        _ = schemes.GetProperty("SlicerRegistryKey").GetProperty("name")
            .GetString().Should().Be("X-Slicer-Api-Key");
        _ = schemes.GetProperty("SlicerServiceKey").GetProperty("name")
            .GetString().Should().Be("X-Slicer-Service-Api-Key");
        _ = schemes.GetProperty("WorkerKey").GetProperty("name")
            .GetString().Should().Be("X-Worker-Key");
        _ = schemes.GetProperty("WorkerServiceId").GetProperty("name")
            .GetString().Should().Be("X-Worker-Id");

        JsonElement protectedOperation = root.GetProperty("paths")
            .GetProperty("/api/calibration/capabilities")
            .GetProperty("get");
        _ = protectedOperation.GetProperty("security")[0]
            .TryGetProperty("Bearer", out _).Should().BeTrue();
        _ = protectedOperation.GetProperty("responses")
            .TryGetProperty("401", out _).Should().BeTrue();
        _ = protectedOperation.GetProperty("responses")
            .TryGetProperty("403", out _).Should().BeTrue();

        JsonElement publicOperation = root.GetProperty("paths")
            .GetProperty("/api/system/capabilities")
            .GetProperty("get");
        _ = publicOperation.TryGetProperty("security", out _).Should().BeFalse();

        JsonElement registryOperation = root.GetProperty("paths")
            .GetProperty("/api/slicers/register")
            .GetProperty("post");
        _ = registryOperation.GetProperty("security")[0]
            .TryGetProperty("SlicerRegistryKey", out _).Should().BeTrue();

        JsonElement serviceOperation = root.GetProperty("paths")
            .GetProperty("/api/slicers/{id}/heartbeat")
            .GetProperty("post");
        _ = serviceOperation.GetProperty("security")[0]
            .TryGetProperty("SlicerServiceKey", out _).Should().BeTrue();

        JsonElement workerOperation = root.GetProperty("paths")
            .GetProperty("/api/slice/claim")
            .GetProperty("post");
        JsonElement workerSecurity = workerOperation.GetProperty("security")[0];
        _ = workerSecurity.TryGetProperty("WorkerKey", out _).Should().BeTrue();
        _ = workerSecurity.TryGetProperty("WorkerServiceId", out _).Should().BeTrue();
        _ = workerOperation.GetProperty("responses")
            .TryGetProperty("403", out _).Should().BeTrue();
    }
}
