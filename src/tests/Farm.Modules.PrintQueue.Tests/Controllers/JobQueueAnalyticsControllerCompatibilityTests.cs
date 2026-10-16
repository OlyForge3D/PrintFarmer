using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos.PrintQueue;
using Farm.Infrastructure.Services.Cost;
using Farm.Infrastructure.Services.Interfaces;
using Farm.Infrastructure.Services.Queue.Dispatch;
using Farm.Modules.PrintQueue.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Farm.Modules.PrintQueue.Tests.Controllers;

/// <summary>
/// Issue #3280: queue list and job details attach the dispatcher-derived compatibility summary
/// only to unassigned queued jobs.
/// </summary>
public class JobQueueAnalyticsControllerCompatibilityTests
{
    private static readonly PrinterCompatibilitySummaryDto Summary = new()
    {
        EvaluatedPrinterCount = 3,
        CompatiblePrinterCount = 1,
        ReadyPrinterCount = 1,
        Basis = new PrinterCompatibilityBasisDto { RequiredMaterial = "PLA" },
    };

    [Fact]
    public async Task GetAllQueueAsync_AttachesSummaryOnlyToUnassignedQueuedJobs()
    {
        string unassignedId = Guid.NewGuid().ToString();
        string assignedId = Guid.NewGuid().ToString();
        List<QueuedPrintJobWithFileMetaDto> jobs =
        [
            new() { Job = new QueuedPrintJobDto { Id = unassignedId, Status = "Queued" } },
            new() { Job = new QueuedPrintJobDto { Id = assignedId, Status = "Queued", AssignedPrinterId = "p1" } },
        ];

        Mock<IPrintJobManagementService> service = new();
        service.Setup(s => s.GetAllQueuedJobsAsync(
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(jobs);

        Mock<IJobCompatibilitySummarizer> summarizer = new();
        summarizer.Setup(s => s.SummarizeCompatibilityAsync(
                It.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == 1 && ids.Single() == Guid.Parse(unassignedId)),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<Guid, PrinterCompatibilitySummaryDto> { [Guid.Parse(unassignedId)] = Summary });

        JobQueueAnalyticsController controller = CreateController(service.Object, summarizer.Object);

        IActionResult result = await controller.GetAllQueueAsync(null, null, null);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result);
        List<QueuedPrintJobWithFileMetaDto> body = Assert.IsType<List<QueuedPrintJobWithFileMetaDto>>(ok.Value);
        Assert.Same(Summary, body[0].Job.Compatibility);
        Assert.Null(body[1].Job.Compatibility);
        summarizer.VerifyAll();
    }

    [Fact]
    public void Compatibility_SerializesCamelCase()
    {
        JsonSerializerOptions options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() },
        };

        string withSummary = JsonSerializer.Serialize(new QueuedPrintJobDto { Compatibility = Summary }, options);
        Assert.Contains("\"compatibility\":{\"evaluatedPrinterCount\":3,\"compatiblePrinterCount\":1,\"readyPrinterCount\":1", withSummary, StringComparison.Ordinal);
        Assert.Contains("\"requiredMaterial\":\"PLA\"", withSummary, StringComparison.Ordinal);
        Assert.Contains("\"excludedBy\":[]", withSummary, StringComparison.Ordinal);
    }

    private static JobQueueAnalyticsController CreateController(
        IPrintJobManagementService service,
        IJobCompatibilitySummarizer summarizer)
    {
        var controller = new JobQueueAnalyticsController(
            service,
            Mock.Of<IJobCostCalculationService>(),
            Mock.Of<ILogger<JobQueueAnalyticsController>>(),
            db: null,
            resourceAuthorization: null,
            compatibilitySummarizer: summarizer);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())], "Test")),
            },
        };
        return controller;
    }
}
