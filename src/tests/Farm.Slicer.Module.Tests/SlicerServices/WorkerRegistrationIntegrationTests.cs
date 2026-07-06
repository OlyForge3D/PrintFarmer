using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Farm.Slicer.Module.Tests.SlicerServices;

/// <summary>
/// Integration tests for slicer worker registration with the central registry
/// </summary>
[Trait("Category", "Integration")]
[Collection(IntegrationTestCollection.Name)]
public class WorkerRegistrationIntegrationTests(ITestOutputHelper output) : IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory = new CustomWebApplicationFactory();
    private readonly ITestOutputHelper _output = output ?? throw new ArgumentNullException(nameof(output));

    public async Task InitializeAsync()
    {
        await _factory.ResetDatabaseAsync();
    }

    public async Task DisposeAsync()
    {
        _factory?.Dispose();
    }

    private HttpClient CreateClient() => _factory.CreateClient();

    private static HttpRequestMessage CreateJsonRequest(HttpMethod method, string url, object? body = null, string? apiKey = "test-worker-key")
    {
        HttpRequestMessage request = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Add("X-Slicer-ApiKey", apiKey);
        }

        return request;
    }

    [Fact]
    public async Task WorkerRegistration_ShouldSucceed_AndAppearInList()
    {
        // Arrange
        RegisterSlicerDto registrationDto = new RegisterSlicerDto
        {
            Name = "test-orca-worker",
            SlicerType = (int)SlicerType.OrcaSlicer,
            Version = "1.0.0-test",
            Host = "http://test-worker:8080",
            CapabilitiesJson = JsonSerializer.Serialize(new
            {
                supportedFormats = new[] { "stl", "obj" },
                capabilities = new[] { "orcaslicer", "test" }
            }),
            MaxConcurrentJobs = 2,
            Tags = "test,integration"
        };

        // Act - Register
        _output.WriteLine("Registering test worker...");
        using HttpClient client = CreateClient();
        using HttpRequestMessage registerRequest = CreateJsonRequest(HttpMethod.Post, "/api/slicers/register", registrationDto);
        HttpResponseMessage registerResponse = await client.SendAsync(registerRequest);

        // Assert - Registration succeeded
        _ = registerResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        string registerResult = await registerResponse.Content.ReadAsStringAsync();
        _output.WriteLine($"Registration result: {registerResult}");

        RegistrationResult? registrationResponse = JsonSerializer.Deserialize<RegistrationResult>(registerResult, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        _ = registrationResponse.Should().NotBeNull();
        _ = registrationResponse!.Id.Should().NotBeEmpty();
        _ = registrationResponse.ApiKey.Should().NotBeNullOrEmpty();

        _output.WriteLine($"Worker registered with ID: {registrationResponse.Id}");

        // Act - List workers
        using HttpRequestMessage listRequest = CreateJsonRequest(HttpMethod.Get, "/api/slicers");
        HttpResponseMessage listResponse = await client.SendAsync(listRequest);

        // Assert - Worker appears in list
        _ = listResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        string listJson = await listResponse.Content.ReadAsStringAsync();
        _output.WriteLine($"Workers list: {listJson}");

        SlicerServiceDto[]? workers = JsonSerializer.Deserialize<SlicerServiceDto[]>(listJson, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        _ = workers.Should().NotBeNull();
        _ = workers.Should().Contain(w => w.Id == registrationResponse.Id && w.Name == "test-orca-worker");
        using JsonDocument listDocument = JsonDocument.Parse(listJson);
        foreach (JsonElement element in listDocument.RootElement.EnumerateArray())
        {
            _ = element.TryGetProperty("apiKey", out JsonElement _).Should().BeFalse();
        }
    }

    [Fact]
    public async Task WorkerHeartbeat_ShouldUpdateStatus()
    {
        // Arrange - Register worker first
        RegisterSlicerDto registrationDto = new RegisterSlicerDto
        {
            Name = "test-heartbeat-worker",
            SlicerType = 0,
            Version = "1.0.0",
            Host = "http://test:8080",
            MaxConcurrentJobs = 1,
            Tags = "test"
        };

        using HttpClient client = CreateClient();
        using HttpRequestMessage registerRequest = CreateJsonRequest(HttpMethod.Post, "/api/slicers/register", registrationDto);
        HttpResponseMessage registerResponse = await client.SendAsync(registerRequest);
        string registerResult = await registerResponse.Content.ReadAsStringAsync();
        RegistrationResult? registration = JsonSerializer.Deserialize<RegistrationResult>(registerResult, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        _ = registration.Should().NotBeNull();
        _output.WriteLine($"Registered worker: {registration!.Id}");

        // Act - Send heartbeat
        HeartbeatDto heartbeatDto = new HeartbeatDto
        {
            Status = "Busy",
            FreeSlots = 0
        };

        using HttpRequestMessage heartbeatRequest = CreateJsonRequest(HttpMethod.Post, $"/api/slicers/{registration.Id}/heartbeat", heartbeatDto, registration.ApiKey);
        HttpResponseMessage heartbeatResponse = await client.SendAsync(heartbeatRequest);

        // Assert - Heartbeat succeeded
        _ = heartbeatResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);
        _output.WriteLine("Heartbeat sent successfully");

        // Verify worker status was updated
        using HttpRequestMessage getRequest = CreateJsonRequest(HttpMethod.Get, $"/api/slicers/{registration.Id}", apiKey: registration.ApiKey);
        HttpResponseMessage getResponse = await client.SendAsync(getRequest);
        _ = getResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        string workerJson = await getResponse.Content.ReadAsStringAsync();
        SlicerServiceDto? worker = JsonSerializer.Deserialize<SlicerServiceDto>(workerJson, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        _ = worker.Should().NotBeNull();
        _ = worker!.Status.Should().Be("Busy");
    }

    [Fact]
    public async Task WorkerDeregister_ShouldRemoveFromList()
    {
        // Arrange - Register worker
        RegisterSlicerDto registrationDto = new RegisterSlicerDto
        {
            Name = "test-deregister-worker",
            SlicerType = 0,
            Version = "1.0.0",
            Host = "http://test:8080",
            MaxConcurrentJobs = 1,
            Tags = "test"
        };

        using HttpClient client = CreateClient();
        using HttpRequestMessage registerRequest = CreateJsonRequest(HttpMethod.Post, "/api/slicers/register", registrationDto);
        HttpResponseMessage registerResponse = await client.SendAsync(registerRequest);
        string registerResult = await registerResponse.Content.ReadAsStringAsync();
        RegistrationResult? registration = JsonSerializer.Deserialize<RegistrationResult>(registerResult, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        _ = registration.Should().NotBeNull();
        _output.WriteLine($"Registered worker: {registration!.Id}");

        using HttpRequestMessage deregisterRequest = CreateJsonRequest(HttpMethod.Post, $"/api/slicers/{registration.Id}/deregister", apiKey: registration.ApiKey);
        HttpResponseMessage deregisterResponse = await client.SendAsync(deregisterRequest);

        // Assert - Deregistration succeeded
        _ = deregisterResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);
        _output.WriteLine("Worker deregistered successfully");

        // Verify worker no longer appears in list
        using HttpRequestMessage listRequest = CreateJsonRequest(HttpMethod.Get, "/api/slicers");
        HttpResponseMessage listResponse = await client.SendAsync(listRequest);
        string listJson = await listResponse.Content.ReadAsStringAsync();
        SlicerServiceDto[]? workers = JsonSerializer.Deserialize<SlicerServiceDto[]>(listJson, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        _ = workers.Should().NotBeNull();
        _ = workers.Should().NotContain(w => w.Id == registration.Id);
    }

    [Fact]
    public async Task RegisterAsync_MissingSharedKey_ReturnsUnauthorized()
    {
        using HttpClient client = CreateClient();
        using HttpRequestMessage request = CreateJsonRequest(HttpMethod.Post, "/api/slicers/register", new RegisterSlicerDto
        {
            Name = "missing-key-worker",
            SlicerType = 1,
            Version = "1.0.0",
            Host = "http://test:8080",
            MaxConcurrentJobs = 1
        }, apiKey: null);

        HttpResponseMessage response = await client.SendAsync(request);

        _ = response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task RegisterAsync_InvalidSharedKey_ReturnsUnauthorized()
    {
        using HttpClient client = CreateClient();
        using HttpRequestMessage request = CreateJsonRequest(HttpMethod.Post, "/api/slicers/register", new RegisterSlicerDto
        {
            Name = "invalid-key-worker",
            SlicerType = 1,
            Version = "1.0.0",
            Host = "http://test:8080",
            MaxConcurrentJobs = 1
        }, apiKey: "wrong-key");

        HttpResponseMessage response = await client.SendAsync(request);

        _ = response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task HeartbeatAsync_InvalidServiceKey_ReturnsUnauthorized()
    {
        using HttpClient client = CreateClient();
        using HttpRequestMessage registerRequest = CreateJsonRequest(HttpMethod.Post, "/api/slicers/register", new RegisterSlicerDto
        {
            Name = "invalid-service-key-worker",
            SlicerType = 1,
            Version = "1.0.0",
            Host = "http://test:8080",
            MaxConcurrentJobs = 1
        });
        HttpResponseMessage registerResponse = await client.SendAsync(registerRequest);
        RegistrationResult? registration = await registerResponse.Content.ReadFromJsonAsync<RegistrationResult>();

        using HttpRequestMessage heartbeatRequest = CreateJsonRequest(
            HttpMethod.Post,
            $"/api/slicers/{registration!.Id}/heartbeat",
            new HeartbeatDto { Status = "Online", FreeSlots = 1 },
            apiKey: "wrong-service-key");

        HttpResponseMessage response = await client.SendAsync(heartbeatRequest);

        _ = response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task HeartbeatAsync_MissingServiceKey_ReturnsUnauthorized()
    {
        using HttpClient client = CreateClient();
        using HttpRequestMessage registerRequest = CreateJsonRequest(HttpMethod.Post, "/api/slicers/register", new RegisterSlicerDto
        {
            Name = "missing-service-key-worker",
            SlicerType = 1,
            Version = "1.0.0",
            Host = "http://test:8080",
            MaxConcurrentJobs = 1
        });
        HttpResponseMessage registerResponse = await client.SendAsync(registerRequest);
        RegistrationResult? registration = await registerResponse.Content.ReadFromJsonAsync<RegistrationResult>();

        using HttpRequestMessage heartbeatRequest = CreateJsonRequest(
            HttpMethod.Post,
            $"/api/slicers/{registration!.Id}/heartbeat",
            new HeartbeatDto { Status = "Online", FreeSlots = 1 },
            apiKey: null);

        HttpResponseMessage response = await client.SendAsync(heartbeatRequest);

        _ = response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private class RegistrationResult
    {
        public Guid Id { get; set; }
        public string ApiKey { get; set; } = string.Empty;
    }

    private class SlicerServiceDto
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public int SlicerType { get; set; }
        public string? Version { get; set; }
        public string? Host { get; set; }
        public string? Status { get; set; }
        public int MaxConcurrentJobs { get; set; }
    }
}
