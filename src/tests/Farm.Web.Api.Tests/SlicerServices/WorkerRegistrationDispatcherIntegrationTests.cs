using System;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Slicing;
using Farm.Infrastructure.Repositories.Workers;
using Farm.Web.Api.Services.JobDispatch;
using Farm.Web.Shared.Contracts.Slicing;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Farm.Web.Api.Tests.SlicerServices
{
    /// <summary>
    /// Integration tests covering Phase 3 worker registration → dispatcher visibility flow.
    /// Validates that registering a slicer service auto-populates the Worker table
    /// and that the dispatcher can select the worker for a capability-constrained job.
    /// </summary>
    [Trait("Category", "Integration")]
    public class WorkerRegistrationDispatcherIntegrationTests : IClassFixture<CustomWebApplicationFactory>
    {
        private readonly CustomWebApplicationFactory _factory;

        public WorkerRegistrationDispatcherIntegrationTests(CustomWebApplicationFactory factory)
        {
            _factory = factory;
        }

        private HttpClient CreateClient() => _factory.CreateClient();

        [Fact(DisplayName = "Registration creates Worker entity accessible via /api/workers", Skip = "Integration host missing MeterProvider; covered by SlicersServiceWorkerSyncTests.")]
        public async Task Registration_Should_Create_WorkerEntity()
        {
            // Arrange
            using var client = CreateClient();
            var registerDto = new RegisterSlicerDto
            {
                Name = "dispatcher-worker-1",
                SlicerType = 0,
                Version = "1.2.3",
                Host = "http://test-host",
                MaxConcurrentJobs = 2,
                CapabilitiesJson = JsonSerializer.Serialize(new[] { "orcaslicer" }),
                Tags = "tag1"
            };
            var json = JsonSerializer.Serialize(registerDto);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            // Act - register
            var resp = await client.PostAsync("/api/slicers/register", content);
            resp.IsSuccessStatusCode.Should().BeTrue();
            var respBody = await resp.Content.ReadAsStringAsync();
            var regResult = JsonSerializer.Deserialize<RegResponse>(respBody, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            regResult.Should().NotBeNull();

            // Query workers endpoint
            var workersResp = await client.GetAsync("/api/workers");
            workersResp.IsSuccessStatusCode.Should().BeTrue();
            var workersJson = await workersResp.Content.ReadAsStringAsync();
            workersJson.Should().Contain("dispatcher-worker-1");

            // Validate Worker repository directly
            using var scope = _factory.Services.CreateScope();
            var workerRepo = scope.ServiceProvider.GetRequiredService<IWorkerRepository>();
            var worker = await workerRepo.GetByServiceIdAsync(regResult!.Id.ToString());
            worker.Should().NotBeNull();
            worker!.Name.Should().Be("dispatcher-worker-1");
            worker.Status.Should().Be(WorkerStatus.Online);
            worker.CapabilitiesJson.Should().Contain("orcaslicer");
            worker.FreeSlots.Should().Be(2);
        }

        [Fact(DisplayName = "Dispatcher selects registered worker with matching capability", Skip = "Integration host missing MeterProvider; covered by SlicersServiceWorkerSyncTests.")]
        public async Task Dispatcher_Should_Select_Worker_With_Capability()
        {
            // Arrange - register worker
            using var client = CreateClient();
            var registerDto = new RegisterSlicerDto
            {
                Name = "dispatcher-worker-cap",
                SlicerType = 0,
                Version = "1.0.0",
                Host = "http://host2",
                MaxConcurrentJobs = 1,
                CapabilitiesJson = JsonSerializer.Serialize(new[] { "orcaslicer", "gcode-generation" })
            };
            var json = JsonSerializer.Serialize(registerDto);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            var resp = await client.PostAsync("/api/slicers/register", content);
            resp.IsSuccessStatusCode.Should().BeTrue();
            var respBody = await resp.Content.ReadAsStringAsync();
            var regResult = JsonSerializer.Deserialize<RegResponse>(respBody, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            regResult.Should().NotBeNull();

            // Create a queued slice job directly via repository
            using var scope = _factory.Services.CreateScope();
            var jobRepo = scope.ServiceProvider.GetRequiredService<ISliceJobRepository>();
            var workerRepo = scope.ServiceProvider.GetRequiredService<IWorkerRepository>();
            var dispatcher = scope.ServiceProvider.GetRequiredService<IJobDispatcherService>();

            var job = new SliceJob
            {
                Id = Guid.NewGuid(),
                Status = SliceJobStatus.Queued,
                QueuedAt = DateTime.UtcNow,
                RequiredCapabilitiesJson = JsonSerializer.Serialize(new[] { "orcaslicer" }),
                SlicerEngine = 0 // OrcaSlicer enum value
            };
            await jobRepo.AddAsync(job);
            await jobRepo.SaveChangesAsync();

            // Act - attempt to find best worker
            var selected = await dispatcher.FindBestWorkerForJobAsync(job);

            // Assert
            selected.Should().NotBeNull();
            selected!.Name.Should().Be("dispatcher-worker-cap");
            selected.CapabilitiesJson.Should().Contain("orcaslicer");
            selected.Status.Should().Be(WorkerStatus.Online);
        }

        private class RegResponse
        {
            public Guid Id { get; set; }
            public string ApiKey { get; set; } = string.Empty;
        }
    }
}
