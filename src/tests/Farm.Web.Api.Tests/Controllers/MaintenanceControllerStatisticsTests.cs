using System.Threading;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Maintenance;
using Farm.Infrastructure.Services.Maintenance;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.Webhooks;
using Farm.Web.Api.Controllers;
using Farm.Web.Api.Hubs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Moq;

namespace Farm.Web.Api.Tests.Controllers;

public class MaintenanceControllerStatisticsTests
{
    private readonly Mock<IPrinterStatisticsRepository> _statisticsRepository = new(MockBehavior.Strict);
    private readonly Mock<IPrintersService> _printersService = new(MockBehavior.Strict);

    private MaintenanceController CreateController()
    {
        return new MaintenanceController(
            logger: Mock.Of<ILogger<MaintenanceController>>(),
            alertRepository: Mock.Of<IMaintenanceAlertRepository>(),
            logRepository: Mock.Of<IMaintenanceLogRepository>(),
            deploymentRepository: Mock.Of<IPrinterMaintenanceScheduleRepository>(),
            statisticsRepository: _statisticsRepository.Object,
            alertService: Mock.Of<IMaintenanceAlertService>(),
            printersService: _printersService.Object,
            maintenanceHub: Mock.Of<IHubContext<MaintenanceHub>>(),
            webhookService: Mock.Of<IWebhookService>());
    }

    [Fact]
    public async Task GetPrinterStatisticsAsync_ExistingPrinterWithoutStats_ReturnsEmptyStatistics()
    {
        Guid printerId = Guid.NewGuid();
        var printer = new Printer
        {
            Id = printerId,
            Name = "Printer without activity",
            ServerUrl = "http://printer.local"
        };

        _statisticsRepository
            .Setup(r => r.GetByPrinterIdAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((PrinterStatistics?)null);
        _printersService
            .Setup(s => s.FindByIdAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(printer);

        MaintenanceController controller = CreateController();

        ActionResult<PrinterStatistics> result = await controller.GetPrinterStatisticsAsync(printerId, CancellationToken.None);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result.Result);
        PrinterStatistics stats = Assert.IsType<PrinterStatistics>(ok.Value);
        Assert.Equal(printerId, stats.PrinterId);
        Assert.Equal(0, stats.TotalPrintHours);
        Assert.Equal(0, stats.TotalJobsCompleted);
        Assert.Equal(0, stats.TotalJobsFailed);
        Assert.Equal(0, stats.TotalFilamentUsedGrams);
        Assert.Equal(0, stats.TotalFilamentUsedMeters);
        Assert.Equal(default, stats.LastSyncTime);
        Assert.Equal(default, stats.CreatedAt);
        Assert.Equal(default, stats.UpdatedAt);
        _statisticsRepository.Verify(r => r.UpsertAsync(It.IsAny<PrinterStatistics>(), It.IsAny<CancellationToken>()), Times.Never);
        _statisticsRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetPrinterStatisticsAsync_MissingPrinter_ReturnsNotFound()
    {
        Guid printerId = Guid.NewGuid();

        _statisticsRepository
            .Setup(r => r.GetByPrinterIdAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((PrinterStatistics?)null);
        _printersService
            .Setup(s => s.FindByIdAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Printer?)null);

        MaintenanceController controller = CreateController();

        ActionResult<PrinterStatistics> result = await controller.GetPrinterStatisticsAsync(printerId, CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetPrinterStatisticsAsync_ExistingStats_ReturnsRealStatistics()
    {
        Guid printerId = Guid.NewGuid();
        var expected = new PrinterStatistics
        {
            Id = Guid.NewGuid(),
            PrinterId = printerId,
            TotalPrintHours = 12.5,
            TotalJobsCompleted = 7,
            TotalJobsFailed = 1,
            TotalFilamentUsedGrams = 235.4,
            TotalFilamentUsedMeters = 78.2,
            LastSyncTime = new DateTime(2026, 7, 3, 12, 0, 0, DateTimeKind.Utc),
            CreatedAt = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc),
            UpdatedAt = new DateTime(2026, 7, 3, 12, 0, 0, DateTimeKind.Utc)
        };

        _statisticsRepository
            .Setup(r => r.GetByPrinterIdAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        MaintenanceController controller = CreateController();

        ActionResult<PrinterStatistics> result = await controller.GetPrinterStatisticsAsync(printerId, CancellationToken.None);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Same(expected, ok.Value);
        _printersService.Verify(s => s.FindByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
