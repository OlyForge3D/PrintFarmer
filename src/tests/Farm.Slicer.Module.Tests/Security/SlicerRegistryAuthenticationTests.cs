using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Farm.Slicer.Module.Contracts;
using FluentAssertions;

namespace Farm.Slicer.Module.Tests.Security;

public sealed class SlicerRegistryAuthenticationTests : IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory = new();

    public async Task InitializeAsync() => await _factory.ResetDatabaseAsync();

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Theory]
    [InlineData(null)]
    [InlineData("incorrect-shared-key")]
    public async Task RegisterAsync_MissingOrInvalidSharedKey_ReturnsUnauthorized(string? sharedKey)
    {
        using HttpClient client = _factory.CreateClient();
        using HttpRequestMessage request = CreateRegistrationRequest("unauthorized-worker", sharedKey);

        HttpResponseMessage response = await client.SendAsync(request);

        _ = response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _ = response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        using JsonDocument problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        _ = problem.RootElement.GetProperty("code").GetString()
            .Should().Be("authentication_required");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("incorrect-shared-key")]
    public async Task ListAsync_MissingOrInvalidSharedKey_ReturnsUnauthorized(string? sharedKey)
    {
        using HttpClient client = _factory.CreateClient();
        using HttpRequestMessage request = new(HttpMethod.Get, "/api/slicers");
        if (!string.IsNullOrEmpty(sharedKey))
        {
            request.Headers.Add("X-Slicer-Api-Key", sharedKey);
        }

        HttpResponseMessage response = await client.SendAsync(request);

        _ = response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ServiceRoutes_KeyForDifferentService_ReturnUnauthorized()
    {
        using HttpClient client = _factory.CreateClient();
        RegisteredService first = await RegisterAsync(client, "first-worker");
        RegisteredService second = await RegisterAsync(client, "second-worker");

        using HttpRequestMessage request = new(HttpMethod.Get, $"/api/slicers/{first.Id}");
        request.Headers.Add("X-Slicer-Service-Api-Key", second.ApiKey);

        HttpResponseMessage response = await client.SendAsync(request);

        _ = response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _ = response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        using JsonDocument problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        _ = problem.RootElement.GetProperty("code").GetString()
            .Should().Be("authentication_required");
    }

    [Fact]
    public async Task ServiceRoutes_MatchingServiceKey_ReturnRedactedServiceStatus()
    {
        using HttpClient client = _factory.CreateClient();
        RegisteredService registered = await RegisterAsync(client, "authorized-worker");
        using HttpRequestMessage request = new(HttpMethod.Get, $"/api/slicers/{registered.Id}");
        request.Headers.Add("X-Slicer-Service-Api-Key", registered.ApiKey);

        HttpResponseMessage response = await client.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();
        string normalizedBody = body.ToLowerInvariant();

        _ = response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        _ = normalizedBody.Should().NotContain("apikey");
        _ = normalizedBody.Should().NotContain("host");
        _ = normalizedBody.Should().NotContain("capabilities");
    }

    [Fact]
    public async Task ListAsync_ValidSharedKey_ReturnsRedactedServiceStatuses()
    {
        using HttpClient client = _factory.CreateClient();
        _ = await RegisterAsync(client, "listed-worker");
        using HttpRequestMessage request = new(HttpMethod.Get, "/api/slicers");
        request.Headers.Add("X-Slicer-Api-Key", "test-worker-key");

        HttpResponseMessage response = await client.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();
        string normalizedBody = body.ToLowerInvariant();

        _ = response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        _ = normalizedBody.Should().NotContain("apikey");
        _ = normalizedBody.Should().NotContain("host");
        _ = normalizedBody.Should().NotContain("capabilities");
    }

    private static async Task<RegisteredService> RegisterAsync(HttpClient client, string name)
    {
        using HttpRequestMessage request = CreateRegistrationRequest(name, "test-worker-key");
        HttpResponseMessage response = await client.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();
        _ = response.StatusCode.Should().Be(HttpStatusCode.Created, body);

        using JsonDocument document = JsonDocument.Parse(body);
        return new RegisteredService(
            document.RootElement.GetProperty("id").GetGuid(),
            document.RootElement.GetProperty("apiKey").GetString()
                ?? throw new InvalidOperationException("Registration did not return a service API key."));
    }

    private static HttpRequestMessage CreateRegistrationRequest(string name, string? sharedKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/slicers/register")
        {
            Content = JsonContent.Create(new RegisterSlicerDto
            {
                Name = name,
                SlicerType = (int)SlicerType.OrcaSlicer,
                Version = "2.3.1",
                Host = "http://private-worker.internal",
                CapabilitiesJson = """{"capabilities":["orcaslicer","orcaslicer-upstream"]}""",
                MaxConcurrentJobs = 1,
                InstanceId = name,
            }),
        };
        if (!string.IsNullOrEmpty(sharedKey))
        {
            request.Headers.Add("X-Slicer-Api-Key", sharedKey);
        }

        return request;
    }

    private sealed record RegisteredService(Guid Id, string ApiKey);
}
