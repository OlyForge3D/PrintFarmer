using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Slicing;
using Farm.Infrastructure.Repositories.Workers;
using Farm.Web.Api.Controllers;
using Farm.Web.Api.Hubs;
using Farm.Web.Api.Services.SlicerServices;
using Farm.Web.Api.Services.Slicing;
using Farm.Web.Shared.Contracts.Slicing;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests
{
    public class SlicersControllerUnitTests
    {
        private static AppDbContext CreateInMemoryDb()
        {
            return TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();
        }

        private static Mock<IHubContext<SlicerHub>> CreateMockHub(out Mock<IClientProxy> clientProxy)
        {
            clientProxy = new Mock<IClientProxy>();
            _ = clientProxy.Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            Mock<IHubClients> clients = new Mock<IHubClients>();
            _ = clients.Setup(c => c.All).Returns(clientProxy.Object);

            Mock<IHubContext<SlicerHub>> hubContext = new Mock<IHubContext<SlicerHub>>();
            _ = hubContext.SetupGet(h => h.Clients).Returns(clients.Object);
            return hubContext;
        }

        private static SlicerServiceMetrics CreateMetrics()
        {
            return new SlicerServiceMetrics();
        }

        private static IOptionsMonitor<Farm.Infrastructure.Settings.SlicerSettings> CreateMockSlicerSettings()
        {
            Farm.Infrastructure.Settings.SlicerSettings settings = new Farm.Infrastructure.Settings.SlicerSettings
            {
                MaxConcurrentJobs = 10, // High enough not to interfere with tests
                MaxMemoryMb = 4096
            };
            Mock<IOptionsMonitor<Farm.Infrastructure.Settings.SlicerSettings>> mock = new Mock<IOptionsMonitor<Farm.Infrastructure.Settings.SlicerSettings>>();
            _ = mock.Setup(m => m.CurrentValue).Returns(settings);
            return mock.Object;
        }

        private static Mock<IProcessProfileRepository> CreateMockProfileRepository()
        {
            return new Mock<IProcessProfileRepository>(MockBehavior.Loose);
        }

        private static HttpClient CreateMockHttpClient()
        {
            return new HttpClient();
        }

        [Fact]
        public async Task RegisterAsync_CreatesService_And_Broadcasts()
        {
            using AppDbContext db = CreateInMemoryDb();
            Mock<IHubContext<SlicerHub>> mockHub = CreateMockHub(out Mock<IClientProxy>? clientProxy);

            EfSlicersRepository repo = new EfSlicersRepository(db);
            EfWorkerRepository workerRepo = new EfWorkerRepository(db);
            Mock<IProcessProfileRepository> profileRepo = CreateMockProfileRepository();
            HttpClient httpClient = CreateMockHttpClient();
            SlicerServiceMetrics metrics = CreateMetrics();
            IOptionsMonitor<Farm.Infrastructure.Settings.SlicerSettings> settings = CreateMockSlicerSettings();
            SlicersService service = new SlicersService(repo, workerRepo, profileRepo.Object, mockHub.Object, metrics, httpClient, settings);
            SlicersController controller = new SlicersController(service);

            RegisterSlicerDto dto = new RegisterSlicerDto
            {
                Name = "unit-orca",
                SlicerType = 1,
                Version = "0.1",
                Host = "http://local",
                MaxConcurrentJobs = 2,
                Tags = "t"
            };

            IActionResult result = await controller.RegisterAsync(dto);

            _ = result.Should().BeOfType<CreatedResult>();

            // Verify DB
            SlicerService? svc = await db.SlicerServices.FirstOrDefaultAsync(s => s.Name == "unit-orca");
            _ = svc.Should().NotBeNull();

            // Verify hub broadcast attempted
            clientProxy.Verify(p => p.SendCoreAsync(
                It.Is<string>(s => s == "SlicerRegistered"),
                It.IsAny<object[]>(),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task ListAsync_ReturnsSeededServices()
        {
            using AppDbContext db = CreateInMemoryDb();
            _ = db.SlicerServices.Add(new SlicerService { Id = System.Guid.NewGuid(), Name = "s1" });
            _ = await db.SaveChangesAsync();

            Mock<IHubContext<SlicerHub>> mockHub = CreateMockHub(out _);
            EfSlicersRepository repo = new EfSlicersRepository(db);
            EfWorkerRepository workerRepo = new EfWorkerRepository(db);
            Mock<IProcessProfileRepository> profileRepo = CreateMockProfileRepository();
            HttpClient httpClient = CreateMockHttpClient();
            SlicerServiceMetrics metrics = CreateMetrics();
            IOptionsMonitor<Farm.Infrastructure.Settings.SlicerSettings> settings = CreateMockSlicerSettings();
            SlicersService service = new SlicersService(repo, workerRepo, profileRepo.Object, mockHub.Object, metrics, httpClient, settings);
            SlicersController controller = new SlicersController(service);

            IActionResult res = await controller.ListAsync();
            _ = res.Should().BeOfType<OkObjectResult>();
            OkObjectResult? ok = res as OkObjectResult;
            List<SlicerService>? list = ok!.Value as List<SlicerService>;
            _ = list.Should().NotBeNull();
            _ = list!.Count.Should().BeGreaterOrEqualTo(1);
        }

        [Fact]
        public async Task HeartbeatAsync_UpdatesAndBroadcasts()
        {
            using AppDbContext db = CreateInMemoryDb();
            Guid id = System.Guid.NewGuid();
            _ = db.SlicerServices.Add(new SlicerService { Id = id, Name = "h1", Tags = "0", Status = "Online" });
            _ = await db.SaveChangesAsync();

            Mock<IHubContext<SlicerHub>> mockHub = CreateMockHub(out Mock<IClientProxy>? clientProxy);
            EfSlicersRepository repo = new EfSlicersRepository(db);
            EfWorkerRepository workerRepo = new EfWorkerRepository(db);
            Mock<IProcessProfileRepository> profileRepo = CreateMockProfileRepository();
            HttpClient httpClient = CreateMockHttpClient();
            SlicerServiceMetrics metrics = CreateMetrics();
            IOptionsMonitor<Farm.Infrastructure.Settings.SlicerSettings> settings = CreateMockSlicerSettings();
            SlicersService service = new SlicersService(repo, workerRepo, profileRepo.Object, mockHub.Object, metrics, httpClient, settings);
            SlicersController controller = new SlicersController(service);

            HeartbeatDto hb = new HeartbeatDto { Status = "Updated", FreeSlots = 3 };
            IActionResult res = await controller.HeartbeatAsync(id, hb);

            _ = res.Should().BeOfType<NoContentResult>();

            SlicerService? svc = await db.SlicerServices.FindAsync(id);
            _ = svc.Should().NotBeNull();
            _ = svc!.Status.Should().Be("Updated");
            _ = svc.Tags.Should().Be("3");

            clientProxy.Verify(p => p.SendCoreAsync(
                It.Is<string>(s => s == "SlicerHeartbeat"),
                It.IsAny<object[]>(),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task DeregisterAsync_RemovesAndBroadcasts()
        {
            using AppDbContext db = CreateInMemoryDb();
            Guid id = System.Guid.NewGuid();
            _ = db.SlicerServices.Add(new SlicerService { Id = id, Name = "d1" });
            _ = await db.SaveChangesAsync();

            Mock<IHubContext<SlicerHub>> mockHub = CreateMockHub(out Mock<IClientProxy>? clientProxy);
            EfSlicersRepository repo = new EfSlicersRepository(db);
            EfWorkerRepository workerRepo = new EfWorkerRepository(db);
            Mock<IProcessProfileRepository> profileRepo = CreateMockProfileRepository();
            HttpClient httpClient = CreateMockHttpClient();
            SlicerServiceMetrics metrics = CreateMetrics();
            IOptionsMonitor<Farm.Infrastructure.Settings.SlicerSettings> settings = CreateMockSlicerSettings();
            SlicersService service = new SlicersService(repo, workerRepo, profileRepo.Object, mockHub.Object, metrics, httpClient, settings);
            SlicersController controller = new SlicersController(service);

            IActionResult res = await controller.DeregisterAsync(id);
            _ = res.Should().BeOfType<NoContentResult>();

            SlicerService? svc = await db.SlicerServices.FindAsync(id);
            _ = svc.Should().BeNull();

            clientProxy.Verify(p => p.SendCoreAsync(
                It.Is<string>(s => s == "SlicerDeregistered"),
                It.IsAny<object[]>(),
                It.IsAny<CancellationToken>()), Times.Once);
        }
    }
}
