using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Farm.Infrastructure.Contracts.Slicing;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Farm.Web.Api.Tests.SlicerServices;

/// <summary>
/// Integration tests for slicer worker registration with the central registry
/// </summary>
[Trait("Category", "Integration")]
public class WorkerRegistrationIntegrationTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly ITestOutputHelper _output;

    public WorkerRegistrationIntegrationTests(CustomWebApplicationFactory factory, ITestOutputHelper output)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _output = output ?? throw new ArgumentNullException(nameof(output));
    }

    private HttpClient CreateClient() => _factory.CreateClient();

    [Fact]
    public async Task WorkerRegistration_ShouldSucceed_AndAppearInList()
    {
        // Arrange
        RegisterSlicerDto registrationDto = new RegisterSlicerDto
        {
            Name = "test-orca-worker",
            SlicerType = 0, // OrcaSlicer
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

        string json = JsonSerializer.Serialize(registrationDto);
        StringContent content = new StringContent(json, Encoding.UTF8, "application/json");

        // Act - Register
        _output.WriteLine("Registering test worker...");
        using HttpClient client = CreateClient();
        HttpResponseMessage registerResponse = await client.PostAsync("/api/slicers/register", content);

        // Assert - Registration succeeded
        _ = registerResponse.Should().BeSuccessful();
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
        HttpResponseMessage listResponse = await client.GetAsync("/api/slicers");

        // Assert - Worker appears in list
        _ = listResponse.Should().BeSuccessful();
        string listJson = await listResponse.Content.ReadAsStringAsync();
        _output.WriteLine($"Workers list: {listJson}");

        SlicerServiceDto[]? workers = JsonSerializer.Deserialize<SlicerServiceDto[]>(listJson, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        _ = workers.Should().NotBeNull();
        _ = workers.Should().Contain(w => w.Id == registrationResponse.Id && w.Name == "test-orca-worker");
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

        string registerJson = JsonSerializer.Serialize(registrationDto);
        StringContent registerContent = new StringContent(registerJson, Encoding.UTF8, "application/json");
        using HttpClient client = CreateClient();
        HttpResponseMessage registerResponse = await client.PostAsync("/api/slicers/register", registerContent);
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

        string heartbeatJson = JsonSerializer.Serialize(heartbeatDto);
        StringContent heartbeatContent = new StringContent(heartbeatJson, Encoding.UTF8, "application/json");

        // Add API key header
        client.DefaultRequestHeaders.Clear();
        client.DefaultRequestHeaders.Add("X-Slicer-ApiKey", registration.ApiKey);

        HttpResponseMessage heartbeatResponse = await client.PostAsync($"/api/slicers/{registration.Id}/heartbeat", heartbeatContent);

        // Assert - Heartbeat succeeded
        _ = heartbeatResponse.Should().BeSuccessful();
        _output.WriteLine("Heartbeat sent successfully");

        // Verify worker status was updated
        HttpResponseMessage getResponse = await client.GetAsync($"/api/slicers/{registration.Id}");
        _ = getResponse.Should().BeSuccessful();

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

        string registerJson = JsonSerializer.Serialize(registrationDto);
        StringContent registerContent = new StringContent(registerJson, Encoding.UTF8, "application/json");
        using HttpClient client = CreateClient();
        HttpResponseMessage registerResponse = await client.PostAsync("/api/slicers/register", registerContent);
        string registerResult = await registerResponse.Content.ReadAsStringAsync();
        RegistrationResult? registration = JsonSerializer.Deserialize<RegistrationResult>(registerResult, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        _ = registration.Should().NotBeNull();
        _output.WriteLine($"Registered worker: {registration!.Id}");

        // Act - Deregister
        client.DefaultRequestHeaders.Clear();
        client.DefaultRequestHeaders.Add("X-Slicer-ApiKey", registration.ApiKey);

        HttpResponseMessage deregisterResponse = await client.PostAsync($"/api/slicers/{registration.Id}/deregister", null);

        // Assert - Deregistration succeeded
        _ = deregisterResponse.Should().BeSuccessful();
        _output.WriteLine("Worker deregistered successfully");

        // Verify worker no longer appears in list
        HttpResponseMessage listResponse = await client.GetAsync("/api/slicers");
        string listJson = await listResponse.Content.ReadAsStringAsync();
        SlicerServiceDto[]? workers = JsonSerializer.Deserialize<SlicerServiceDto[]>(listJson, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        _ = workers.Should().NotBeNull();
        _ = workers.Should().NotContain(w => w.Id == registration.Id);
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
