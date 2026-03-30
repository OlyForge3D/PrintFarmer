using System.Reflection;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.FailureDetection;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.SignalR;
using Farm.Infrastructure.Settings;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace Farm.Web.Api.Tests.Services.FailureDetection;

public sealed class PrintFailureMonitorPersistenceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _dbContextOptions;

    public PrintFailureMonitorPersistenceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _dbContextOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        using AppDbContext dbContext = CreateDbContext();
        _ = dbContext.Database.EnsureCreated();
    }

    [Fact]
    public async Task HandleFailureDetectedAsync_PersistsIncidentAndBroadcastsEnrichedEvent()
    {
        using AppDbContext dbContext = CreateDbContext();
        Printer printer = await SeedPrinterAsync(dbContext, "Monitor History Printer");
        IFailureDetectionIncidentHistoryService historyService = new FailureDetectionIncidentHistoryService(dbContext);

        var cachedStatus = new PrinterStatusDto(
            printer.Id,
            true,
            "Printing",
            JobName: "jobs/monitoring-job.gcode",
            FileName: "monitoring-job.gcode");

        Mock<IPrinterStatusCacheReader> statusCache = new();
        statusCache.Setup(cache => cache.GetStatus(printer.Id)).Returns(cachedStatus);

        Mock<IClientProxy> clientProxy = new();
        clientProxy
            .Setup(proxy => proxy.SendCoreAsync(
                "FailureDetected",
                It.IsAny<object[]>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        Mock<IHubClients> hubClients = new();
        hubClients.Setup(clients => clients.All).Returns(clientProxy.Object);

        Mock<IHubContext<PrinterHub>> hub = new();
        hub.SetupGet(context => context.Clients).Returns(hubClients.Object);

        Mock<ISettingsService> settingsService = new();
        settingsService
            .Setup(service => service.Get<ObicoSettings>())
            .Returns(new ObicoSettings
            {
                Enabled = true,
                ScanIntervalSeconds = 30,
                ConfidenceThreshold = 0.8m,
                AutoPauseOnFailure = false,
            });

        Mock<IServiceProvider> serviceProvider = new();
        serviceProvider.Setup(provider => provider.GetService(typeof(ISettingsService))).Returns(settingsService.Object);

        Mock<IServiceScope> scope = new();
        scope.SetupGet(createdScope => createdScope.ServiceProvider).Returns(serviceProvider.Object);

        Mock<IServiceScopeFactory> scopeFactory = new();
        scopeFactory.Setup(factory => factory.CreateScope()).Returns(scope.Object);

        Mock<IPrintersService> printersService = new(MockBehavior.Strict);
        FailureDetectionMonitorStatusStore monitorStatus = new();
        ILogger<PrintFailureMonitorService> logger = Mock.Of<ILogger<PrintFailureMonitorService>>();
        FailureDetectionMetrics metrics = new();

        PrintFailureMonitorService service = new(
            scopeFactory.Object,
            monitorStatus,
            statusCache.Object,
            hub.Object,
            metrics,
            logger);

        FailureDetectionResult result = FailureDetectionResult.Success(0.9421m, isFailureDetected: true);

        MethodInfo method = typeof(PrintFailureMonitorService).GetMethod(
            "HandleFailureDetectedAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Expected HandleFailureDetectedAsync private method.");

        var task = (Task<bool>)(method.Invoke(service, [
            printer,
            "http://camera.local/snapshot.jpg",
            result,
            new ObicoSettings
            {
                Enabled = true,
                ScanIntervalSeconds = 30,
                ConfidenceThreshold = 0.8m,
                AutoPauseOnFailure = false,
            },
            dbContext,
            historyService,
            printersService.Object,
            CancellationToken.None,
        ]) ?? throw new InvalidOperationException("Expected HandleFailureDetectedAsync invocation to return a task."));

        bool autoPaused = await task;

        autoPaused.Should().BeFalse();

        FailureDetectionIncident incident = await dbContext.FailureDetectionIncidents.SingleAsync();
        incident.PrinterId.Should().Be(printer.Id);
        incident.JobId.Should().BeNull();
        incident.JobName.Should().Be("jobs/monitoring-job.gcode");
        incident.FileName.Should().Be("monitoring-job.gcode");
        incident.Confidence.Should().Be(0.9421m);
        incident.DetectedAt.Should().Be(result.AnalyzedAt);
        incident.SnapshotUrl.Should().Be("http://camera.local/snapshot.jpg");
        incident.AutoPaused.Should().BeFalse();

        clientProxy.Verify(
            proxy => proxy.SendCoreAsync(
                "FailureDetected",
                It.Is<object[]>(args => HasExpectedFailureEvent(args, printer.Id)),
                It.IsAny<CancellationToken>()),
            Times.Once);

        printersService.Verify(
            service => service.PauseAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    private AppDbContext CreateDbContext() => new(_dbContextOptions);

    private static async Task<Printer> SeedPrinterAsync(AppDbContext dbContext, string printerName)
    {
        Manufacturer manufacturer = new()
        {
            Id = Guid.NewGuid(),
            Name = $"{printerName} Manufacturer",
        };
        PrinterModel model = new()
        {
            Id = Guid.NewGuid(),
            ManufacturerId = manufacturer.Id,
            Name = $"{printerName} Model",
        };
        Printer printer = new()
        {
            Id = Guid.NewGuid(),
            Name = printerName,
            ServerUrl = $"http://{Guid.NewGuid():N}.local",
            BackendPort = 7125,
            ManufacturerId = manufacturer.Id,
            ModelId = model.Id,
        };

        _ = dbContext.Manufacturers.Add(manufacturer);
        _ = dbContext.PrinterModels.Add(model);
        _ = dbContext.Printers.Add(printer);
        await dbContext.SaveChangesAsync();

        return printer;
    }

    private static bool HasExpectedFailureEvent(object[] args, Guid printerId)
    {
        if (args.Length != 1 || args[0] is not FailureDetectionDto dto)
        {
            return false;
        }

        return dto.PrinterId == printerId
            && dto.Id.HasValue
            && dto.JobName == "jobs/monitoring-job.gcode"
            && dto.FileName == "monitoring-job.gcode"
            && dto.AutoPaused == false;
    }
}
