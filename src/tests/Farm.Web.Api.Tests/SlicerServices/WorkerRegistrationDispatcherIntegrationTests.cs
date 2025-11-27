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
            using HttpClient client = CreateClient();
            RegisterSlicerDto registerDto = new RegisterSlicerDto
            {
                Name = "dispatcher-worker-1",
                SlicerType = 0,
                Version = "1.2.3",
                Host = "http://test-host",
                MaxConcurrentJobs = 2,
                CapabilitiesJson = JsonSerializer.Serialize(new[] { "orcaslicer" }),
                Tags = "tag1"
            };
            string json = JsonSerializer.Serialize(registerDto);
            using StringContent content = new StringContent(json, Encoding.UTF8, "application/json");

            // Act - register
            HttpResponseMessage resp = await client.PostAsync("/api/slicers/register", content);
            _ = resp.IsSuccessStatusCode.Should().BeTrue();
            string respBody = await resp.Content.ReadAsStringAsync();
            RegResponse? regResult = JsonSerializer.Deserialize<RegResponse>(respBody, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            _ = regResult.Should().NotBeNull();

            // Query workers endpoint
            HttpResponseMessage workersResp = await client.GetAsync("/api/workers");
            _ = workersResp.IsSuccessStatusCode.Should().BeTrue();
            string workersJson = await workersResp.Content.ReadAsStringAsync();
            _ = workersJson.Should().Contain("dispatcher-worker-1");

            // Validate Worker repository directly
            using IServiceScope scope = _factory.Services.CreateScope();
            IWorkerRepository workerRepo = scope.ServiceProvider.GetRequiredService<IWorkerRepository>();
            Worker? worker = await workerRepo.GetByServiceIdAsync(regResult!.Id.ToString());
            _ = worker.Should().NotBeNull();
            _ = worker!.Name.Should().Be("dispatcher-worker-1");
            _ = worker.Status.Should().Be(WorkerStatus.Online);
            _ = worker.CapabilitiesJson.Should().Contain("orcaslicer");
            _ = worker.FreeSlots.Should().Be(2);
        }

        [Fact(DisplayName = "Dispatcher selects registered worker with matching capability", Skip = "Integration host missing MeterProvider; covered by SlicersServiceWorkerSyncTests.")]
        public async Task Dispatcher_Should_Select_Worker_With_Capability()
        {
            // Arrange - register worker
            using HttpClient client = CreateClient();
            RegisterSlicerDto registerDto = new RegisterSlicerDto
            {
                Name = "dispatcher-worker-cap",
                SlicerType = 0,
                Version = "1.0.0",
                Host = "http://host2",
                MaxConcurrentJobs = 1,
                CapabilitiesJson = JsonSerializer.Serialize(new[] { "orcaslicer", "gcode-generation" })
            };
            string json = JsonSerializer.Serialize(registerDto);
            using StringContent content = new StringContent(json, Encoding.UTF8, "application/json");
            HttpResponseMessage resp = await client.PostAsync("/api/slicers/register", content);
            _ = resp.IsSuccessStatusCode.Should().BeTrue();
            string respBody = await resp.Content.ReadAsStringAsync();
            RegResponse? regResult = JsonSerializer.Deserialize<RegResponse>(respBody, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            _ = regResult.Should().NotBeNull();

            // Create a queued slice job directly via repository
            using IServiceScope scope = _factory.Services.CreateScope();
            ISliceJobRepository jobRepo = scope.ServiceProvider.GetRequiredService<ISliceJobRepository>();
            IWorkerRepository workerRepo = scope.ServiceProvider.GetRequiredService<IWorkerRepository>();
            IJobDispatcherService dispatcher = scope.ServiceProvider.GetRequiredService<IJobDispatcherService>();

            SliceJob job = new SliceJob
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
            Worker? selected = await dispatcher.FindBestWorkerForJobAsync(job);

            // Assert
            _ = selected.Should().NotBeNull();
            _ = selected!.Name.Should().Be("dispatcher-worker-cap");
            _ = selected.CapabilitiesJson.Should().Contain("orcaslicer");
            _ = selected.Status.Should().Be(WorkerStatus.Online);
        }

        private class RegResponse
        {
            public Guid Id { get; set; }
            public string ApiKey { get; set; } = string.Empty;
        }
    }
}
