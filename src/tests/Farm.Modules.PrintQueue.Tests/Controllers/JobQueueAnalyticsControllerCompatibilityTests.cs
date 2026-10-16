using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos.PrintQueue;
using Farm.Infrastructure.Services.Cost;
using Farm.Infrastructure.Services.Interfaces;
using Farm.Infrastructure.Services.Queue;
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
    public async Task GetAllQueueAsync_SummarizerThrows_FailsOpenWithNullSummary()
    {
        List<QueuedPrintJobWithFileMetaDto> jobs =
        [
            new() { Job = new QueuedPrintJobDto { Id = Guid.NewGuid().ToString(), Status = "Queued" } },
        ];
        Mock<IPrintJobManagementService> service = new();
        service.Setup(s => s.GetAllQueuedJobsAsync(
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(jobs);
        Mock<IJobCompatibilitySummarizer> summarizer = new();
        summarizer.Setup(s => s.SummarizeCompatibilityAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db timeout"));

        IActionResult result = await CreateController(service.Object, summarizer.Object).GetAllQueueAsync(null, null, null);

        List<QueuedPrintJobWithFileMetaDto> body = Assert.IsType<List<QueuedPrintJobWithFileMetaDto>>(
            Assert.IsType<OkObjectResult>(result).Value);
        Assert.Null(body[0].Job.Compatibility);
    }

    [Fact]
    public async Task GetJobDetailsAsync_UnassignedQueuedJob_AttachesSummary()
    {
        Guid id = Guid.NewGuid();
        Mock<IPrintJobManagementService> service = new();
        service.Setup(s => s.GetJobByIdAsync(id.ToString(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueuedPrintJobDto { Id = id.ToString(), Status = "Queued" });
        Mock<IJobCompatibilitySummarizer> summarizer = new();
        summarizer.Setup(s => s.SummarizeCompatibilityAsync(
                It.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == 1 && ids.Single() == id),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<Guid, PrinterCompatibilitySummaryDto> { [id] = Summary });

        IActionResult result = await CreateController(service.Object, summarizer.Object)
            .GetJobDetailsAsync(id.ToString());

        QueuedPrintJobDto body = Assert.IsType<QueuedPrintJobDto>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Same(Summary, body.Compatibility);
        summarizer.VerifyAll();
    }

    [Theory]
    [InlineData("Queued", "printer-1")]
    [InlineData("Printing", null)]
    public async Task GetJobDetailsAsync_AssignedOrNonQueuedJob_HasNoSummary(string status, string? assignedPrinterId)
    {
        Guid id = Guid.NewGuid();
        Mock<IPrintJobManagementService> service = new();
        service.Setup(s => s.GetJobByIdAsync(id.ToString(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueuedPrintJobDto { Id = id.ToString(), Status = status, AssignedPrinterId = assignedPrinterId });
        Mock<IJobCompatibilitySummarizer> summarizer = new(MockBehavior.Strict);

        IActionResult result = await CreateController(service.Object, summarizer.Object)
            .GetJobDetailsAsync(id.ToString());

        QueuedPrintJobDto body = Assert.IsType<QueuedPrintJobDto>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Null(body.Compatibility);
    }

    [Fact]
    public async Task GetJobDetailsAsync_Unauthorized_DoesNotSummarize()
    {
        Guid id = Guid.NewGuid();
        Mock<IQueueResourceAuthorizationService> authorization = new();
        authorization.Setup(a => a.CanAccessJobAsync(
                It.IsAny<ClaimsPrincipal>(), id, It.IsAny<PrinterGroupAccessLevel>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        Mock<IJobCompatibilitySummarizer> summarizer = new(MockBehavior.Strict);

        IActionResult result = await CreateController(
                Mock.Of<IPrintJobManagementService>(), summarizer.Object, authorization.Object)
            .GetJobDetailsAsync(id.ToString());

        Assert.IsType<NotFoundObjectResult>(result);
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
        IJobCompatibilitySummarizer summarizer,
        IQueueResourceAuthorizationService? resourceAuthorization = null)
    {
        var controller = new JobQueueAnalyticsController(
            service,
            Mock.Of<IJobCostCalculationService>(),
            Mock.Of<ILogger<JobQueueAnalyticsController>>(),
            db: null,
            resourceAuthorization: resourceAuthorization,
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
