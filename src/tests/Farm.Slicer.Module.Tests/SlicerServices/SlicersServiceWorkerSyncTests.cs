using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Services.Gcode;
using Farm.Infrastructure.Telemetry;
using Farm.Slicer.Module.Api.Hubs;
using Farm.Slicer.Module.Services.Metrics;
using Farm.Web.Api.Services.Catalog;
using Farm.Web.Api.Services.Slicing;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Farm.Slicer.Module.Tests.SlicerServices
{
    /// <summary>
    /// Unit-level tests validating Phase 3 synchronization logic between SlicerService and Worker entity.
    /// Avoids full WebApplicationFactory host to bypass OpenTelemetry MeterProvider requirements.
    /// </summary>
    public class SlicersServiceWorkerSyncTests
    {
        private static SlicerDbContext CreateDb() => TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();

        private static Mock<IHubContext<SlicerHub>> CreateMockHub(out Mock<IClientProxy> clientProxy)
        {
            clientProxy = new Mock<IClientProxy>();
            _ = clientProxy.Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            Mock<IHubClients> hubClients = new Mock<IHubClients>();
            _ = hubClients.Setup(c => c.All).Returns(clientProxy.Object);
            Mock<IHubContext<SlicerHub>> hub = new Mock<IHubContext<SlicerHub>>();
            _ = hub.SetupGet(h => h.Clients).Returns(hubClients.Object);
            return hub;
        }

        private static SlicerServiceMetrics CreateMetrics() => new SlicerServiceMetrics();

        private static IOptionsMonitor<Farm.Slicer.Module.Settings.SlicerSettings> CreateMockSlicerSettings()
        {
            Farm.Slicer.Module.Settings.SlicerSettings settings = new Farm.Slicer.Module.Settings.SlicerSettings
            {
                MaxConcurrentJobs = 10, // High enough not to interfere with tests
                MaxMemoryMb = 4096
            };
            Mock<IOptionsMonitor<Farm.Slicer.Module.Settings.SlicerSettings>> mock = new Mock<IOptionsMonitor<Farm.Slicer.Module.Settings.SlicerSettings>>();
            _ = mock.Setup(m => m.CurrentValue).Returns(settings);
            return mock.Object;
        }

        private static HttpClient CreateMockHttpClient()
        {
            HttpClientHandler handler = new HttpClientHandler();
            return new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5000") };
        }

        private static Mock<IProcessProfileRepository> CreateMockProfileRepository()
        {
            Mock<IProcessProfileRepository> mock = new Mock<IProcessProfileRepository>(MockBehavior.Loose);
            return mock;
        }

        private static Mock<IUnifiedLoggingService> CreateMockLogger()
        {
            return new Mock<IUnifiedLoggingService>(MockBehavior.Loose);
        }

        private static Mock<IFilamentProfileRepository> CreateMockFilamentProfileRepository()
        {
            return new Mock<IFilamentProfileRepository>(MockBehavior.Loose);
        }

        private static Mock<IMachineProfileRepository> CreateMockMachineProfileRepository()
        {
            return new Mock<IMachineProfileRepository>(MockBehavior.Loose);
        }

        private static Mock<IMachineModelProfileRepository> CreateMockMachineModelProfileRepository()
        {
            return new Mock<IMachineModelProfileRepository>(MockBehavior.Loose);
        }

        private static Mock<ICatalogService> CreateMockCatalogService()
        {
            Mock<ICatalogService> mock = new Mock<ICatalogService>(MockBehavior.Loose);
            // Return empty lists for catalog service; tests don't depend on actual catalog data for profile seeding
            _ = mock.Setup(c => c.GetManufacturersAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(((IReadOnlyList<ManufacturerDto>)new List<ManufacturerDto>(), (string?)null));
            _ = mock.Setup(c => c.GetModelsAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(((IReadOnlyList<PrinterModelDto>)new List<PrinterModelDto>(), (string?)null));
            return mock;
        }

        private static Mock<Farm.Infrastructure.Settings.ISettingsService> CreateMockSettingsService()
        {
            var mock = new Mock<Farm.Infrastructure.Settings.ISettingsService>(MockBehavior.Loose);
            // By default, lock operations return success (TryAcquireLockAsync returns true)
            _ = mock.Setup(s => s.TryAcquireLockAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
            _ = mock.Setup(s => s.CompleteLockAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            _ = mock.Setup(s => s.ClearLockAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            return mock;
        }

        private static Mock<IPrinterModelAliasService> CreateMockAliasService()
        {
            var mock = new Mock<IPrinterModelAliasService>(MockBehavior.Loose);
            _ = mock.Setup(s => s.ResolveModelAliasAsync(It.IsAny<string>(), It.IsAny<string?>()))
                .ReturnsAsync((Guid?)null);
            return mock;
        }

        [Fact(DisplayName = "RegisterAsync creates Worker with matching capabilities and slots")]
        public async Task RegisterAsync_Should_Create_Worker()
        {
            using SlicerDbContext db = CreateDb();
            EfSlicersRepository slicerRepo = new EfSlicersRepository(db);
            EfWorkerRepository workerRepo = new EfWorkerRepository(db);
            Mock<IHubContext<SlicerHub>> mockHub = CreateMockHub(out Mock<IClientProxy>? clientProxy);
            SlicerServiceMetrics metrics = CreateMetrics();
            IOptionsMonitor<Farm.Slicer.Module.Settings.SlicerSettings> settings = CreateMockSlicerSettings();
            HttpClient httpClient = CreateMockHttpClient();
            Mock<IProcessProfileRepository> profileRepo = CreateMockProfileRepository();
            Mock<IFilamentProfileRepository> filamentProfileRepo = CreateMockFilamentProfileRepository();
            Mock<IUnifiedLoggingService> logger = CreateMockLogger();
            Mock<ICatalogService> catalogService = CreateMockCatalogService();
            Mock<IPrinterModelAliasService> aliasService = CreateMockAliasService();
            Mock<Farm.Infrastructure.Settings.ISettingsService> settingsService = CreateMockSettingsService();
            Mock<IMachineProfileRepository> machineProfileRepo = CreateMockMachineProfileRepository();
            Mock<IMachineModelProfileRepository> machineModelProfileRepo = CreateMockMachineModelProfileRepository();
            SlicersService svc = new SlicersService(slicerRepo, workerRepo, profileRepo.Object, filamentProfileRepo.Object, machineProfileRepo.Object, machineModelProfileRepo.Object, catalogService.Object, aliasService.Object, settingsService.Object, mockHub.Object, metrics, httpClient, logger.Object, settings);

            RegisterSlicerDto dto = new RegisterSlicerDto
            {
                Name = "sync-test-worker",
                SlicerType = 0,
                Version = "0.9.0",
                Host = "http://worker-host",
                MaxConcurrentJobs = 3,
                CapabilitiesJson = "[\"orcaslicer\",\"gcode-gen\"]"
            };

            (Guid id, string? apiKey) = await svc.RegisterAsync(dto, CancellationToken.None);

            _ = id.Should().NotBe(Guid.Empty);
            _ = apiKey.Should().NotBeNullOrWhiteSpace();

            // Verify slicer service exists
            SlicerService? slicer = await db.Set<SlicerService>().FindAsync(id);
            _ = slicer.Should().NotBeNull();
            _ = slicer!.Name.Should().Be("sync-test-worker");

            // Verify worker created via synchronization
            Worker? worker = await workerRepo.GetByServiceIdAsync(id.ToString());
            _ = worker.Should().NotBeNull();
            _ = worker!.Name.Should().Be("sync-test-worker");
            _ = worker.CapabilitiesJson.Should().Contain("orcaslicer");
            _ = worker.TotalSlots.Should().Be(3);
            _ = worker.FreeSlots.Should().Be(3);
            _ = worker.Status.Should().Be(WorkerStatus.Online);

            // Hub event broadcast attempted
            clientProxy.Verify(p => p.SendCoreAsync(
                It.Is<string>(s => s == SlicerHubEvents.SlicerRegistered),
                It.IsAny<object[]>(),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact(DisplayName = "HeartbeatAsync updates Worker FreeSlots, ActiveJobs, Status")]
        public async Task HeartbeatAsync_Should_Update_Worker()
        {
            using SlicerDbContext db = CreateDb();
            EfSlicersRepository slicerRepo = new EfSlicersRepository(db);
            EfWorkerRepository workerRepo = new EfWorkerRepository(db);
            Mock<IHubContext<SlicerHub>> mockHub = CreateMockHub(out Mock<IClientProxy>? clientProxy);
            SlicerServiceMetrics metrics = CreateMetrics();
            IOptionsMonitor<Farm.Slicer.Module.Settings.SlicerSettings> settings = CreateMockSlicerSettings();
            HttpClient httpClient = CreateMockHttpClient();
            Mock<IProcessProfileRepository> profileRepo = CreateMockProfileRepository();
            Mock<IFilamentProfileRepository> filamentProfileRepo = CreateMockFilamentProfileRepository();
            Mock<IUnifiedLoggingService> logger = CreateMockLogger();
            Mock<ICatalogService> catalogService = CreateMockCatalogService();
            Mock<IPrinterModelAliasService> aliasService = CreateMockAliasService();
            Mock<Farm.Infrastructure.Settings.ISettingsService> settingsService = CreateMockSettingsService();
            Mock<IMachineProfileRepository> machineProfileRepo = CreateMockMachineProfileRepository();
            Mock<IMachineModelProfileRepository> machineModelProfileRepo = CreateMockMachineModelProfileRepository();
            SlicersService svc = new SlicersService(slicerRepo, workerRepo, profileRepo.Object, filamentProfileRepo.Object, machineProfileRepo.Object, machineModelProfileRepo.Object, catalogService.Object, aliasService.Object, settingsService.Object, mockHub.Object, metrics, httpClient, logger.Object, settings);

            RegisterSlicerDto dto = new RegisterSlicerDto
            {
                Name = "sync-heartbeat-worker",
                SlicerType = 0,
                Version = "1.0.0",
                Host = "http://worker-heartbeat",
                MaxConcurrentJobs = 4,
                CapabilitiesJson = "[\"orcaslicer\"]"
            };
            (Guid id, _) = await svc.RegisterAsync(dto, CancellationToken.None);

            // Simulate heartbeat with fewer free slots (2 of 4 used)
            HeartbeatDto hb = new HeartbeatDto
            {
                Status = "Busy",
                FreeSlots = 2
            };
            bool ok = await svc.HeartbeatAsync(id, hb, CancellationToken.None);
            _ = ok.Should().BeTrue();

            Worker? worker = await workerRepo.GetByServiceIdAsync(id.ToString());
            _ = worker.Should().NotBeNull();
            _ = worker!.FreeSlots.Should().Be(2);
            _ = worker.ActiveJobs.Should().Be(2); // TotalSlots(4) - FreeSlots(2)
            _ = worker.Status.Should().Be(WorkerStatus.Busy);

            clientProxy.Verify(p => p.SendCoreAsync(
                It.Is<string>(s => s == SlicerHubEvents.SlicerHeartbeat),
                It.IsAny<object[]>(),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact(DisplayName = "DeregisterAsync marks Worker Offline")]
        public async Task DeregisterAsync_Should_Mark_Worker_Offline()
        {
            using SlicerDbContext db = CreateDb();
            EfSlicersRepository slicerRepo = new EfSlicersRepository(db);
            EfWorkerRepository workerRepo = new EfWorkerRepository(db);
            Mock<IHubContext<SlicerHub>> mockHub = CreateMockHub(out Mock<IClientProxy>? clientProxy);
            SlicerServiceMetrics metrics = CreateMetrics();
            IOptionsMonitor<Farm.Slicer.Module.Settings.SlicerSettings> settings = CreateMockSlicerSettings();
            HttpClient httpClient = CreateMockHttpClient();
            Mock<IProcessProfileRepository> profileRepo = CreateMockProfileRepository();
            Mock<IFilamentProfileRepository> filamentProfileRepo = CreateMockFilamentProfileRepository();
            Mock<IUnifiedLoggingService> logger = CreateMockLogger();
            Mock<ICatalogService> catalogService = CreateMockCatalogService();
            Mock<IPrinterModelAliasService> aliasService = CreateMockAliasService();
            Mock<Farm.Infrastructure.Settings.ISettingsService> settingsService = CreateMockSettingsService();
            Mock<IMachineProfileRepository> machineProfileRepo = CreateMockMachineProfileRepository();
            Mock<IMachineModelProfileRepository> machineModelProfileRepo = CreateMockMachineModelProfileRepository();
            SlicersService svc = new SlicersService(slicerRepo, workerRepo, profileRepo.Object, filamentProfileRepo.Object, machineProfileRepo.Object, machineModelProfileRepo.Object, catalogService.Object, aliasService.Object, settingsService.Object, mockHub.Object, metrics, httpClient, logger.Object, settings);

            RegisterSlicerDto dto = new RegisterSlicerDto
            {
                Name = "sync-deregister-worker",
                SlicerType = 0,
                Version = "1.0.0",
                Host = "http://worker-dereg",
                MaxConcurrentJobs = 1
            };
            (Guid id, _) = await svc.RegisterAsync(dto, CancellationToken.None);

            bool ok = await svc.DeregisterAsync(id, CancellationToken.None);
            _ = ok.Should().BeTrue();

            Worker? worker = await workerRepo.GetByServiceIdAsync(id.ToString());
            _ = worker.Should().NotBeNull();
            _ = worker!.Status.Should().Be(WorkerStatus.Offline);
            _ = worker.OfflineAt.Should().NotBeNull();

            clientProxy.Verify(p => p.SendCoreAsync(
                It.Is<string>(s => s == SlicerHubEvents.SlicerDeregistered),
                It.IsAny<object[]>(),
                It.IsAny<CancellationToken>()), Times.Once);
        }
    }
}
