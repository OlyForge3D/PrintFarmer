using System;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;
using Farm.Web.Api.Hubs;
using Farm.Infrastructure.Repositories.Slicing;
using Farm.Infrastructure.Repositories.Workers;
using Farm.Web.Api.Services.Slicing;
using Farm.Web.Shared.Contracts.Slicing;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.SlicerServices
{
    /// <summary>
    /// Unit-level tests validating Phase 3 synchronization logic between SlicerService and Worker entity.
    /// Avoids full WebApplicationFactory host to bypass OpenTelemetry MeterProvider requirements.
    /// </summary>
    public class SlicersServiceWorkerSyncTests
    {
        private static Farm.Infrastructure.Data.AppDbContext CreateDb() => TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();

        private static Mock<IHubContext<SlicerHub>> CreateMockHub(out Mock<IClientProxy> clientProxy)
        {
            clientProxy = new Mock<IClientProxy>();
            clientProxy.Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            var hubClients = new Mock<IHubClients>();
            hubClients.Setup(c => c.All).Returns(clientProxy.Object);
            var hub = new Mock<IHubContext<SlicerHub>>();
            hub.SetupGet(h => h.Clients).Returns(hubClients.Object);
            return hub;
        }

        private static SlicerServiceMetrics CreateMetrics() => new SlicerServiceMetrics();

        private static Microsoft.Extensions.Options.IOptionsMonitor<Farm.Infrastructure.Settings.SlicerSettings> CreateMockSlicerSettings()
        {
            var settings = new Farm.Infrastructure.Settings.SlicerSettings
            {
                MaxConcurrentJobs = 10, // High enough not to interfere with tests
                MaxMemoryMb = 4096
            };
            var mock = new Moq.Mock<Microsoft.Extensions.Options.IOptionsMonitor<Farm.Infrastructure.Settings.SlicerSettings>>();
            mock.Setup(m => m.CurrentValue).Returns(settings);
            return mock.Object;
        }

        private static HttpClient CreateMockHttpClient()
        {
            var handler = new System.Net.Http.HttpClientHandler();
            return new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5000") };
        }

        private static Mock<Farm.Infrastructure.Repositories.Slicing.IProcessProfileRepository> CreateMockProfileRepository()
        {
            var mock = new Mock<Farm.Infrastructure.Repositories.Slicing.IProcessProfileRepository>(MockBehavior.Loose);
            return mock;
        }

        [Fact(DisplayName = "RegisterAsync creates Worker with matching capabilities and slots")]
        public async Task RegisterAsync_Should_Create_Worker()
        {
            using var db = CreateDb();
            var slicerRepo = new EfSlicersRepository(db);
            var workerRepo = new EfWorkerRepository(db);
            var mockHub = CreateMockHub(out var clientProxy);
            var metrics = CreateMetrics();
            var settings = CreateMockSlicerSettings();
            var httpClient = CreateMockHttpClient();
            var profileRepo = CreateMockProfileRepository();
            var svc = new SlicersService(slicerRepo, workerRepo, profileRepo.Object, mockHub.Object, metrics, httpClient, settings);

            var dto = new RegisterSlicerDto
            {
                Name = "sync-test-worker",
                SlicerType = 0,
                Version = "0.9.0",
                Host = "http://worker-host",
                MaxConcurrentJobs = 3,
                CapabilitiesJson = "[\"orcaslicer\",\"gcode-gen\"]"
            };

            var (id, apiKey) = await svc.RegisterAsync(dto, CancellationToken.None);

            id.Should().NotBe(Guid.Empty);
            apiKey.Should().NotBeNullOrWhiteSpace();

            // Verify slicer service exists
            var slicer = await db.SlicerServices.FindAsync(id);
            slicer.Should().NotBeNull();
            slicer!.Name.Should().Be("sync-test-worker");

            // Verify worker created via synchronization
            var worker = await workerRepo.GetByServiceIdAsync(id.ToString());
            worker.Should().NotBeNull();
            worker!.Name.Should().Be("sync-test-worker");
            worker.CapabilitiesJson.Should().Contain("orcaslicer");
            worker.TotalSlots.Should().Be(3);
            worker.FreeSlots.Should().Be(3);
            worker.Status.Should().Be(WorkerStatus.Online);

            // Hub event broadcast attempted
            clientProxy.Verify(p => p.SendCoreAsync(
                It.Is<string>(s => s == SlicerHubEvents.SlicerRegistered),
                It.IsAny<object[]>(),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact(DisplayName = "HeartbeatAsync updates Worker FreeSlots, ActiveJobs, Status")]
        public async Task HeartbeatAsync_Should_Update_Worker()
        {
            using var db = CreateDb();
            var slicerRepo = new EfSlicersRepository(db);
            var workerRepo = new EfWorkerRepository(db);
            var mockHub = CreateMockHub(out var clientProxy);
            var metrics = CreateMetrics();
            var settings = CreateMockSlicerSettings();
            var httpClient = CreateMockHttpClient();
            var profileRepo = CreateMockProfileRepository();
            var svc = new SlicersService(slicerRepo, workerRepo, profileRepo.Object, mockHub.Object, metrics, httpClient, settings);

            var dto = new RegisterSlicerDto
            {
                Name = "sync-heartbeat-worker",
                SlicerType = 0,
                Version = "1.0.0",
                Host = "http://worker-heartbeat",
                MaxConcurrentJobs = 4,
                CapabilitiesJson = "[\"orcaslicer\"]"
            };
            var (id, _) = await svc.RegisterAsync(dto, CancellationToken.None);

            // Simulate heartbeat with fewer free slots (2 of 4 used)
            var hb = new HeartbeatDto
            {
                Status = "Busy",
                FreeSlots = 2
            };
            var ok = await svc.HeartbeatAsync(id, hb, CancellationToken.None);
            ok.Should().BeTrue();

            var worker = await workerRepo.GetByServiceIdAsync(id.ToString());
            worker.Should().NotBeNull();
            worker!.FreeSlots.Should().Be(2);
            worker.ActiveJobs.Should().Be(2); // TotalSlots(4) - FreeSlots(2)
            worker.Status.Should().Be(WorkerStatus.Busy);

            clientProxy.Verify(p => p.SendCoreAsync(
                It.Is<string>(s => s == SlicerHubEvents.SlicerHeartbeat),
                It.IsAny<object[]>(),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact(DisplayName = "DeregisterAsync marks Worker Offline")]
        public async Task DeregisterAsync_Should_Mark_Worker_Offline()
        {
            using var db = CreateDb();
            var slicerRepo = new EfSlicersRepository(db);
            var workerRepo = new EfWorkerRepository(db);
            var mockHub = CreateMockHub(out var clientProxy);
            var metrics = CreateMetrics();
            var settings = CreateMockSlicerSettings();
            var httpClient = CreateMockHttpClient();
            var profileRepo = CreateMockProfileRepository();
            var svc = new SlicersService(slicerRepo, workerRepo, profileRepo.Object, mockHub.Object, metrics, httpClient, settings);

            var dto = new RegisterSlicerDto
            {
                Name = "sync-deregister-worker",
                SlicerType = 0,
                Version = "1.0.0",
                Host = "http://worker-dereg",
                MaxConcurrentJobs = 1
            };
            var (id, _) = await svc.RegisterAsync(dto, CancellationToken.None);

            var ok = await svc.DeregisterAsync(id, CancellationToken.None);
            ok.Should().BeTrue();

            var worker = await workerRepo.GetByServiceIdAsync(id.ToString());
            worker.Should().NotBeNull();
            worker!.Status.Should().Be(WorkerStatus.Offline);
            worker.OfflineAt.Should().NotBeNull();

            clientProxy.Verify(p => p.SendCoreAsync(
                It.Is<string>(s => s == SlicerHubEvents.SlicerDeregistered),
                It.IsAny<object[]>(),
                It.IsAny<CancellationToken>()), Times.Once);
        }
    }
}
