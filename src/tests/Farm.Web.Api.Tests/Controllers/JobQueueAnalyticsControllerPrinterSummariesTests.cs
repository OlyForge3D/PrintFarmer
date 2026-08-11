using Farm.Api.Controllers;
using Farm.Infrastructure.Authorization;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos.PrintQueue;
using Farm.Infrastructure.Security;
using Farm.Infrastructure.Services.Cost;
using Farm.Infrastructure.Services.Interfaces;
using Farm.Infrastructure.Services.Queue;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Controllers;

/// <summary>
/// Tests for <see cref="JobQueueAnalyticsController.GetPrinterQueueSummariesAsync"/> (PR 1146,
/// item 9): the fleet-scoped read that replaces N per-printer
/// <c>GET /api/job-queue-analytics/printer/{id}</c> round trips used to derive the compact-card
/// "X of Y" label.
/// </summary>
public class JobQueueAnalyticsControllerPrinterSummariesTests
{
    private readonly Mock<IPrintJobManagementService> _printJobManagementServiceMock = new();
    private readonly Mock<IJobCostCalculationService> _jobCostCalculationServiceMock = new();

    private JobQueueAnalyticsController CreateController(IQueueResourceAuthorizationService? resourceAuthorization = null)
    {
        var controller = new JobQueueAnalyticsController(
            _printJobManagementServiceMock.Object,
            _jobCostCalculationServiceMock.Object,
            Mock.Of<ILogger<JobQueueAnalyticsController>>(),
            db: null,
            resourceAuthorization: resourceAuthorization);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };
        return controller;
    }

    [Fact]
    public void GetPrinterQueueSummariesAsync_RequiresQueueReadPermission()
    {
        var method = typeof(JobQueueAnalyticsController).GetMethod(nameof(JobQueueAnalyticsController.GetPrinterQueueSummariesAsync));

        Assert.NotNull(method);
        RequirePermissionAttribute attribute = Assert.Single(
            method!.GetCustomAttributes(typeof(RequirePermissionAttribute), inherit: true).Cast<RequirePermissionAttribute>());
        Assert.Equal(PrintFarmerPermissions.Queue.Read, attribute.Permission);
    }

    [Fact]
    public async Task GetPrinterQueueSummariesAsync_WithoutResourceAuthorization_ReturnsAllSummariesFromService()
    {
        var summaries = new List<PrinterQueueSummaryDto>
        {
            new(Guid.NewGuid(), 2, 1, 1),
            new(Guid.NewGuid(), 3, 0, null),
        };
        _printJobManagementServiceMock
            .Setup(s => s.GetPrinterQueueSummariesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(summaries);
        JobQueueAnalyticsController controller = CreateController(resourceAuthorization: null);

        IActionResult result = await controller.GetPrinterQueueSummariesAsync(CancellationToken.None);

        OkObjectResult okResult = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(summaries, okResult.Value);
    }

    [Fact]
    public async Task GetPrinterQueueSummariesAsync_FiltersOutPrintersCallerCannotAccess()
    {
        Guid visiblePrinterId = Guid.NewGuid();
        Guid hiddenPrinterId = Guid.NewGuid();
        var summaries = new List<PrinterQueueSummaryDto>
        {
            new(visiblePrinterId, 1, 1, 1),
            new(hiddenPrinterId, 2, 0, null),
        };
        _printJobManagementServiceMock
            .Setup(s => s.GetPrinterQueueSummariesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(summaries);

        var resourceAuthorization = new Mock<IQueueResourceAuthorizationService>();
        resourceAuthorization
            .Setup(r => r.CanAccessPrinterAsync(It.IsAny<System.Security.Claims.ClaimsPrincipal>(), visiblePrinterId, PrinterGroupAccessLevel.View, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        resourceAuthorization
            .Setup(r => r.CanAccessPrinterAsync(It.IsAny<System.Security.Claims.ClaimsPrincipal>(), hiddenPrinterId, PrinterGroupAccessLevel.View, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        JobQueueAnalyticsController controller = CreateController(resourceAuthorization.Object);

        IActionResult result = await controller.GetPrinterQueueSummariesAsync(CancellationToken.None);

        OkObjectResult okResult = Assert.IsType<OkObjectResult>(result);
        var returned = Assert.IsAssignableFrom<IEnumerable<PrinterQueueSummaryDto>>(okResult.Value).ToList();
        PrinterQueueSummaryDto onlyEntry = Assert.Single(returned);
        Assert.Equal(visiblePrinterId, onlyEntry.PrinterId);
    }

    [Fact]
    public async Task GetPrinterQueueSummariesAsync_NoActiveQueues_ReturnsOkWithEmptyList()
    {
        _printJobManagementServiceMock
            .Setup(s => s.GetPrinterQueueSummariesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        JobQueueAnalyticsController controller = CreateController();

        IActionResult result = await controller.GetPrinterQueueSummariesAsync(CancellationToken.None);

        OkObjectResult okResult = Assert.IsType<OkObjectResult>(result);
        Assert.Empty(Assert.IsAssignableFrom<IEnumerable<PrinterQueueSummaryDto>>(okResult.Value));
    }

    [Fact]
    public async Task GetPrinterQueueSummariesAsync_WhenServiceThrows_ReturnsInternalServerError()
    {
        _printJobManagementServiceMock
            .Setup(s => s.GetPrinterQueueSummariesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        JobQueueAnalyticsController controller = CreateController();

        IActionResult result = await controller.GetPrinterQueueSummariesAsync(CancellationToken.None);

        ObjectResult statusResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status500InternalServerError, statusResult.StatusCode);
    }
}
