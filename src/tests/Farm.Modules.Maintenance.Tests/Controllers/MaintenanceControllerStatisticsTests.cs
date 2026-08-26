using System.Net.Http;
using System.Threading;
using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Maintenance;
using Farm.Infrastructure.Services.Maintenance;
using Farm.Infrastructure.Services.OperatorFeatures;
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
            toolheadStatisticsRepository: Mock.Of<IToolheadStatisticsRepository>(),
            alertService: Mock.Of<IMaintenanceAlertService>(),
            printersService: _printersService.Object,
            operatorFeatureGate: Mock.Of<IOperatorFeatureGate>(),
            maintenanceHub: Mock.Of<IHubContext<MaintenanceHub>>(),
            webhookService: Mock.Of<IWebhookService>(),
            alertResolutionService: Mock.Of<IMaintenanceAlertResolutionService>());
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
        _printersService
            .Setup(s => s.GetHistoryTotalsAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HistoryTotals { JobTotals = new JobTotals() });

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
    public async Task GetPrinterStatisticsAsync_ExistingPrinterWithoutStats_LiveHistoryHasTotals_ReturnsLiveTotals()
    {
        // Reproduces issue #1994: a seeded Moonraker printer with no accrued maintenance-statistics
        // row yet, but with real history on the backend (Moonraker's /server/history/totals reports
        // total_time: 3600, total_print_time: 3550 for one completed job).
        Guid printerId = Guid.NewGuid();
        var printer = new Printer
        {
            Id = printerId,
            Name = "Moonraker Ready",
            ServerUrl = "http://printer.local",
            Backend = (int)PrinterBackend.Moonraker
        };

        _statisticsRepository
            .Setup(r => r.GetByPrinterIdAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((PrinterStatistics?)null);
        _printersService
            .Setup(s => s.FindByIdAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(printer);
        _printersService
            .Setup(s => s.GetHistoryTotalsAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HistoryTotals
            {
                JobTotals = new JobTotals
                {
                    TotalJobs = 1,
                    TotalTime = 3600,
                    TotalPrintTime = 3550,
                    TotalFilamentUsed = 1000
                }
            });

        MaintenanceController controller = CreateController();

        ActionResult<PrinterStatistics> result = await controller.GetPrinterStatisticsAsync(printerId, CancellationToken.None);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result.Result);
        PrinterStatistics stats = Assert.IsType<PrinterStatistics>(ok.Value);
        Assert.Equal(printerId, stats.PrinterId);
        Assert.Equal(3550.0 / 3600.0, stats.TotalPrintHours, precision: 10);
        Assert.Equal(1, stats.TotalJobsCompleted);
        Assert.Equal(0, stats.TotalJobsFailed);
        Assert.Equal(1.0, stats.TotalFilamentUsedMeters, precision: 10);
        Assert.Equal(1000 * 0.00237, stats.TotalFilamentUsedGrams, precision: 10);
        _statisticsRepository.Verify(r => r.UpsertAsync(It.IsAny<PrinterStatistics>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetPrinterStatisticsAsync_ExistingPrinterWithoutStats_OctoPrintLiveTotals_FilamentAlreadyInMeters()
    {
        // OctoPrintClient normalizes FilamentUsed to meters before aggregating into JobTotals
        // (unlike Moonraker, which reports millimeters). Applying the millimeter conversion to an
        // OctoPrint printer's totals would underreport filament usage by a factor of 1000.
        Guid printerId = Guid.NewGuid();
        var printer = new Printer
        {
            Id = printerId,
            Name = "OctoPrint Ready",
            ServerUrl = "http://printer.local",
            Backend = (int)PrinterBackend.OctoPrint
        };

        _statisticsRepository
            .Setup(r => r.GetByPrinterIdAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((PrinterStatistics?)null);
        _printersService
            .Setup(s => s.FindByIdAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(printer);
        _printersService
            .Setup(s => s.GetHistoryTotalsAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HistoryTotals
            {
                JobTotals = new JobTotals
                {
                    TotalJobs = 2,
                    TotalTime = 7200,
                    TotalPrintTime = 7100,
                    TotalFilamentUsed = 2.5 // already meters, per OctoPrintClient
                }
            });

        MaintenanceController controller = CreateController();

        ActionResult<PrinterStatistics> result = await controller.GetPrinterStatisticsAsync(printerId, CancellationToken.None);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result.Result);
        PrinterStatistics stats = Assert.IsType<PrinterStatistics>(ok.Value);
        Assert.Equal(2, stats.TotalJobsCompleted);
        Assert.Equal(2.5, stats.TotalFilamentUsedMeters, precision: 10);
        Assert.Equal(2.5 * 1000.0 * 0.00237, stats.TotalFilamentUsedGrams, precision: 10);
    }

    [Fact]
    public async Task GetPrinterStatisticsAsync_ExistingPrinterWithoutStats_PrusaLinkLiveTotals_FilamentInMillimeters()
    {
        // PrusaLinkApiClient sums the raw PrusaLink history "filament.tool0.length" field without any
        // unit conversion, so PrusaLink's TotalFilamentUsed is in millimeters just like Moonraker's -
        // it must NOT be treated as "unknown backend" and silently zeroed out.
        Guid printerId = Guid.NewGuid();
        var printer = new Printer
        {
            Id = printerId,
            Name = "PrusaLink Ready",
            ServerUrl = "http://printer.local",
            Backend = (int)PrinterBackend.PrusaLink
        };

        _statisticsRepository
            .Setup(r => r.GetByPrinterIdAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((PrinterStatistics?)null);
        _printersService
            .Setup(s => s.FindByIdAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(printer);
        _printersService
            .Setup(s => s.GetHistoryTotalsAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HistoryTotals
            {
                JobTotals = new JobTotals
                {
                    TotalJobs = 3,
                    TotalTime = 300,
                    TotalPrintTime = 300,
                    TotalFilamentUsed = 1000 // millimeters, per PrusaLinkApiClient
                }
            });

        MaintenanceController controller = CreateController();

        ActionResult<PrinterStatistics> result = await controller.GetPrinterStatisticsAsync(printerId, CancellationToken.None);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result.Result);
        PrinterStatistics stats = Assert.IsType<PrinterStatistics>(ok.Value);
        Assert.Equal(3, stats.TotalJobsCompleted);
        Assert.Equal(1.0, stats.TotalFilamentUsedMeters, precision: 10);
        Assert.Equal(1000 * 0.00237, stats.TotalFilamentUsedGrams, precision: 10);
    }

    [Fact]
    public async Task GetPrinterStatisticsAsync_ExistingPrinterWithoutStats_LiveHistoryThrowsUnexpectedException_Returns500()
    {
        // An unexpected exception (not one of the recognized transient/upstream failure types) is a
        // real bug, not a "backend is unreachable" condition, so it must not be silently swallowed
        // into a misleading 200-with-zeros response.
        Guid printerId = Guid.NewGuid();
        var printer = new Printer
        {
            Id = printerId,
            Name = "Buggy printer",
            ServerUrl = "http://printer.local"
        };

        _statisticsRepository
            .Setup(r => r.GetByPrinterIdAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((PrinterStatistics?)null);
        _printersService
            .Setup(s => s.FindByIdAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(printer);
        _printersService
            .Setup(s => s.GetHistoryTotalsAsync(printerId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("unexpected bug"));

        MaintenanceController controller = CreateController();

        ActionResult<PrinterStatistics> result = await controller.GetPrinterStatisticsAsync(printerId, CancellationToken.None);

        ObjectResult errorResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(500, errorResult.StatusCode);
    }

    [Fact]
    public async Task GetPrinterStatisticsAsync_ExistingPrinterWithoutStats_LiveHistoryFails_ReturnsZeroFallback()
    {
        Guid printerId = Guid.NewGuid();
        var printer = new Printer
        {
            Id = printerId,
            Name = "Offline printer",
            ServerUrl = "http://printer.local"
        };

        _statisticsRepository
            .Setup(r => r.GetByPrinterIdAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((PrinterStatistics?)null);
        _printersService
            .Setup(s => s.FindByIdAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(printer);
        _printersService
            .Setup(s => s.GetHistoryTotalsAsync(printerId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("backend unreachable"));

        MaintenanceController controller = CreateController();

        ActionResult<PrinterStatistics> result = await controller.GetPrinterStatisticsAsync(printerId, CancellationToken.None);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result.Result);
        PrinterStatistics stats = Assert.IsType<PrinterStatistics>(ok.Value);
        Assert.Equal(printerId, stats.PrinterId);
        Assert.Equal(0, stats.TotalPrintHours);
        Assert.Equal(0, stats.TotalJobsCompleted);
        Assert.Equal(0, stats.TotalFilamentUsedGrams);
        Assert.Equal(0, stats.TotalFilamentUsedMeters);
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
