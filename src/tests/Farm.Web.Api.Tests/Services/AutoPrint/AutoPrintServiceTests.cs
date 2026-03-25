using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.AutoPrint;
using Farm.Infrastructure.Services.Queue.Dispatch;
using Farm.Infrastructure.Services.SignalR;
using Farm.Web.Api.Tests.TestInfrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services.AutoPrint;

public sealed class AutoPrintServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;

    public AutoPrintServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        TestSqlitePragmaEnforcer.EnsureForeignKeysEnabled(_connection);

        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new AppDbContext(options);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task MarkPreClearAsync_WhenQueuedJobsAlreadyExist_NotifiesDispatchTrigger()
    {
        Printer printer = await CreatePrinterAsync();
        _db.PrintJobs.Add(new PrintJob
        {
            Id = Guid.NewGuid(),
            Name = "queued-job",
            AssignedPrinterId = printer.Id,
            Status = PrintJobStatus.Queued,
            Priority = 0,
            QueuePosition = 1,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            QueuedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();

        Mock<IAutoDispatchTrigger> dispatchTrigger = new();
        AutoPrintService service = new(
            _db,
            CreateHubContextMock().Object,
            NullLogger<AutoPrintService>.Instance,
            dispatchTrigger: dispatchTrigger.Object);

        AutoPrintStatusDto status = await service.MarkPreClearAsync(printer.Id);

        status.BedPreConfirmed.Should().BeTrue();
        dispatchTrigger.Verify(trigger => trigger.NotifyJobQueued(printer.Id), Times.Once);
    }

    private async Task<Printer> CreatePrinterAsync()
    {
        Manufacturer manufacturer = new()
        {
            Id = Guid.NewGuid(),
            Name = "Test Manufacturer",
        };
        PrinterModel model = new()
        {
            Id = Guid.NewGuid(),
            Name = "Test Model",
            ManufacturerId = manufacturer.Id,
        };
        Printer printer = new()
        {
            Id = Guid.NewGuid(),
            Name = "AutoPrint Service Test Printer",
            ServerUrl = "http://autoprint-service-test.local",
            BackendPort = 7125,
            Backend = (int)PrinterBackend.Moonraker,
            ManufacturerId = manufacturer.Id,
            ModelId = model.Id,
            AutoPrintEnabled = true,
            IsEnabled = true,
        };

        _db.Manufacturers.Add(manufacturer);
        _db.PrinterModels.Add(model);
        _db.Printers.Add(printer);
        await _db.SaveChangesAsync();

        return printer;
    }

    private static Mock<IHubContext<PrinterHub>> CreateHubContextMock()
    {
        Mock<IClientProxy> proxy = new();
        proxy.Setup(x => x.SendCoreAsync(
                It.IsAny<string>(),
                It.IsAny<object?[]>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        Mock<IHubClients> clients = new();
        clients.Setup(x => x.All).Returns(proxy.Object);

        Mock<IHubContext<PrinterHub>> hub = new();
        hub.Setup(x => x.Clients).Returns(clients.Object);
        return hub;
    }
}
