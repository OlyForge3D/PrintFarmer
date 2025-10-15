using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Web.Api.Controllers;
using Farm.Web.Api.Services.SlicerServices;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests
{
    public class SlicersControllerUnitTests
    {
        private static AppDbContext CreateInMemoryDb()
        {
            var opts = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(databaseName: System.Guid.NewGuid().ToString())
                .Options;
            var db = new AppDbContext(opts);
            db.Database.EnsureCreated();
            return db;
        }

        private static Mock<IHubContext<SlicerProgressHub>> CreateMockHub(out Mock<IClientProxy> clientProxy)
        {
            clientProxy = new Mock<IClientProxy>();
            clientProxy.Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var clients = new Mock<IHubClients>();
            clients.Setup(c => c.All).Returns(clientProxy.Object);

            var hubContext = new Mock<IHubContext<SlicerProgressHub>>();
            hubContext.SetupGet(h => h.Clients).Returns(clients.Object);
            return hubContext;
        }

        [Fact]
        public async Task RegisterAsync_CreatesService_And_Broadcasts()
        {
            using var db = CreateInMemoryDb();
            var mockHub = CreateMockHub(out var clientProxy);

            var controller = new SlicersController(db, mockHub.Object);

            var dto = new RegisterSlicerDto
            {
                Name = "unit-orca",
                SlicerType = 1,
                Version = "0.1",
                Host = "http://local",
                MaxConcurrentJobs = 2,
                Tags = "t"
            };

            var result = await controller.RegisterAsync(dto);

            result.Should().BeOfType<CreatedResult>();

            // Verify DB
            var svc = await db.SlicerServices.FirstOrDefaultAsync(s => s.Name == "unit-orca");
            svc.Should().NotBeNull();

            // Verify hub broadcast attempted
            clientProxy.Verify(p => p.SendCoreAsync(
                It.Is<string>(s => s == "SlicerRegistered"),
                It.IsAny<object[]>(),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task ListAsync_ReturnsSeededServices()
        {
            using var db = CreateInMemoryDb();
            db.SlicerServices.Add(new SlicerService { Id = System.Guid.NewGuid(), Name = "s1" });
            await db.SaveChangesAsync();

            var mockHub = CreateMockHub(out _);
            var controller = new SlicersController(db, mockHub.Object);

            var res = await controller.ListAsync();
            res.Should().BeOfType<OkObjectResult>();
            var ok = res as OkObjectResult;
            var list = ok!.Value as System.Collections.Generic.List<SlicerService>;
            list.Should().NotBeNull();
            list!.Count.Should().BeGreaterOrEqualTo(1);
        }

        [Fact]
        public async Task HeartbeatAsync_UpdatesAndBroadcasts()
        {
            using var db = CreateInMemoryDb();
            var id = System.Guid.NewGuid();
            db.SlicerServices.Add(new SlicerService { Id = id, Name = "h1", Tags = "0", Status = "Online" });
            await db.SaveChangesAsync();

            var mockHub = CreateMockHub(out var clientProxy);
            var controller = new SlicersController(db, mockHub.Object);

            var hb = new HeartbeatDto { Status = "Updated", FreeSlots = 3 };
            var res = await controller.HeartbeatAsync(id, hb);

            res.Should().BeOfType<NoContentResult>();

            var svc = await db.SlicerServices.FindAsync(id);
            svc.Should().NotBeNull();
            svc!.Status.Should().Be("Updated");
            svc.Tags.Should().Be("3");

            clientProxy.Verify(p => p.SendCoreAsync(
                It.Is<string>(s => s == "SlicerHeartbeat"),
                It.IsAny<object[]>(),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task DeregisterAsync_RemovesAndBroadcasts()
        {
            using var db = CreateInMemoryDb();
            var id = System.Guid.NewGuid();
            db.SlicerServices.Add(new SlicerService { Id = id, Name = "d1" });
            await db.SaveChangesAsync();

            var mockHub = CreateMockHub(out var clientProxy);
            var controller = new SlicersController(db, mockHub.Object);

            var res = await controller.DeregisterAsync(id);
            res.Should().BeOfType<NoContentResult>();

            var svc = await db.SlicerServices.FindAsync(id);
            svc.Should().BeNull();

            clientProxy.Verify(p => p.SendCoreAsync(
                It.Is<string>(s => s == "SlicerDeregistered"),
                It.IsAny<object[]>(),
                It.IsAny<CancellationToken>()), Times.Once);
        }
    }
}
