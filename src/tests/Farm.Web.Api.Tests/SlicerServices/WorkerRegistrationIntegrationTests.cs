using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Farm.Web.Shared.Contracts.Slicing;
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
        var registrationDto = new RegisterSlicerDto
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

        var json = JsonSerializer.Serialize(registrationDto);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        // Act - Register
        _output.WriteLine("Registering test worker...");
        using var client = CreateClient();
        var registerResponse = await client.PostAsync("/api/slicers/register", content);

        // Assert - Registration succeeded
        registerResponse.Should().BeSuccessful();
        var registerResult = await registerResponse.Content.ReadAsStringAsync();
        _output.WriteLine($"Registration result: {registerResult}");

        var registrationResponse = JsonSerializer.Deserialize<RegistrationResult>(registerResult, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        registrationResponse.Should().NotBeNull();
        registrationResponse!.Id.Should().NotBeEmpty();
        registrationResponse.ApiKey.Should().NotBeNullOrEmpty();

        _output.WriteLine($"Worker registered with ID: {registrationResponse.Id}");

        // Act - List workers
        var listResponse = await client.GetAsync("/api/slicers");

        // Assert - Worker appears in list
        listResponse.Should().BeSuccessful();
        var listJson = await listResponse.Content.ReadAsStringAsync();
        _output.WriteLine($"Workers list: {listJson}");

        var workers = JsonSerializer.Deserialize<SlicerServiceDto[]>(listJson, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        workers.Should().NotBeNull();
        workers.Should().Contain(w => w.Id == registrationResponse.Id && w.Name == "test-orca-worker");
    }

    [Fact]
    public async Task WorkerHeartbeat_ShouldUpdateStatus()
    {
        // Arrange - Register worker first
        var registrationDto = new RegisterSlicerDto
        {
            Name = "test-heartbeat-worker",
            SlicerType = 0,
            Version = "1.0.0",
            Host = "http://test:8080",
            MaxConcurrentJobs = 1,
            Tags = "test"
        };

        var registerJson = JsonSerializer.Serialize(registrationDto);
        var registerContent = new StringContent(registerJson, Encoding.UTF8, "application/json");
        using var client = CreateClient();
        var registerResponse = await client.PostAsync("/api/slicers/register", registerContent);
        var registerResult = await registerResponse.Content.ReadAsStringAsync();
        var registration = JsonSerializer.Deserialize<RegistrationResult>(registerResult, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        registration.Should().NotBeNull();
        _output.WriteLine($"Registered worker: {registration!.Id}");

        // Act - Send heartbeat
        var heartbeatDto = new HeartbeatDto
        {
            Status = "Busy",
            FreeSlots = 0
        };

        var heartbeatJson = JsonSerializer.Serialize(heartbeatDto);
        var heartbeatContent = new StringContent(heartbeatJson, Encoding.UTF8, "application/json");

        // Add API key header
        client.DefaultRequestHeaders.Clear();
        client.DefaultRequestHeaders.Add("X-Slicer-ApiKey", registration.ApiKey);

        var heartbeatResponse = await client.PostAsync($"/api/slicers/{registration.Id}/heartbeat", heartbeatContent);

        // Assert - Heartbeat succeeded
        heartbeatResponse.Should().BeSuccessful();
        _output.WriteLine("Heartbeat sent successfully");

        // Verify worker status was updated
        var getResponse = await client.GetAsync($"/api/slicers/{registration.Id}");
        getResponse.Should().BeSuccessful();

        var workerJson = await getResponse.Content.ReadAsStringAsync();
        var worker = JsonSerializer.Deserialize<SlicerServiceDto>(workerJson, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        worker.Should().NotBeNull();
        worker!.Status.Should().Be("Busy");
    }

    [Fact]
    public async Task WorkerDeregister_ShouldRemoveFromList()
    {
        // Arrange - Register worker
        var registrationDto = new RegisterSlicerDto
        {
            Name = "test-deregister-worker",
            SlicerType = 0,
            Version = "1.0.0",
            Host = "http://test:8080",
            MaxConcurrentJobs = 1,
            Tags = "test"
        };

        var registerJson = JsonSerializer.Serialize(registrationDto);
        var registerContent = new StringContent(registerJson, Encoding.UTF8, "application/json");
        using var client = CreateClient();
        var registerResponse = await client.PostAsync("/api/slicers/register", registerContent);
        var registerResult = await registerResponse.Content.ReadAsStringAsync();
        var registration = JsonSerializer.Deserialize<RegistrationResult>(registerResult, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        registration.Should().NotBeNull();
        _output.WriteLine($"Registered worker: {registration!.Id}");

        // Act - Deregister
        client.DefaultRequestHeaders.Clear();
        client.DefaultRequestHeaders.Add("X-Slicer-ApiKey", registration.ApiKey);

        var deregisterResponse = await client.PostAsync($"/api/slicers/{registration.Id}/deregister", null);

        // Assert - Deregistration succeeded
        deregisterResponse.Should().BeSuccessful();
        _output.WriteLine("Worker deregistered successfully");

        // Verify worker no longer appears in list
        var listResponse = await client.GetAsync("/api/slicers");
        var listJson = await listResponse.Content.ReadAsStringAsync();
        var workers = JsonSerializer.Deserialize<SlicerServiceDto[]>(listJson, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        workers.Should().NotBeNull();
        workers.Should().NotContain(w => w.Id == registration.Id);
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
