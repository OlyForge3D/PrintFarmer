using Farm.Api.Controllers;
using Farm.Infrastructure.Dtos.PrintQueue;
using Farm.Infrastructure.Services.Cost;
using Farm.Infrastructure.Services.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Controllers;

public sealed class JobQueueAnalyticsControllerReorderTests
{
    [Fact]
    public void BulkReorderJobs_AlwaysReturnsConflictWithoutCallingService()
    {
        Mock<IPrintJobManagementService> service = new(MockBehavior.Strict);
        var controller = new JobQueueAnalyticsController(
            service.Object,
            Mock.Of<IJobCostCalculationService>(),
            NullLogger<JobQueueAnalyticsController>.Instance);

        IActionResult action = controller.BulkReorderJobs(new BulkReorderQueueJobsRequest
        {
            Moves = [],
        });

        ConflictObjectResult conflict = Assert.IsType<ConflictObjectResult>(action);
        Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);
        ProblemDetails problem = Assert.IsType<ProblemDetails>(conflict.Value);
        Assert.Equal("Manual queue reordering is disabled", problem.Title);
        Assert.Contains("priority", problem.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("queued time", problem.Detail, StringComparison.OrdinalIgnoreCase);
        service.VerifyNoOtherCalls();
    }
}
