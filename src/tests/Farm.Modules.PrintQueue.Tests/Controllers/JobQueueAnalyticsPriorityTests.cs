using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Farm.Api.Controllers;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos.PrintQueue;
using Farm.Infrastructure.Services.Cost;
using Farm.Infrastructure.Services.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Controllers;

public class JobQueueAnalyticsPriorityTests
{
    [Theory]
    [InlineData(PrintJobPriority.Low, "Low")]
    [InlineData(PrintJobPriority.Normal, "Normal")]
    [InlineData(PrintJobPriority.High, "High")]
    [InlineData(PrintJobPriority.Urgent, "Urgent")]
    public void UpdatePriorityRequest_SerializesCanonicalEnumName(
        PrintJobPriority priority,
        string expectedName)
    {
        JsonSerializerOptions options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() },
        };

        string json = JsonSerializer.Serialize(
            new UpdateQueueJobPriorityRequest { NewPriority = priority },
            options);

        Assert.Equal($"{{\"newPriority\":\"{expectedName}\"}}", json);
    }

    [Theory]
    [InlineData(PrintJobPriority.Low, "Low")]
    [InlineData(PrintJobPriority.Normal, "Normal")]
    [InlineData(PrintJobPriority.High, "High")]
    [InlineData(PrintJobPriority.Urgent, "Urgent")]
    public void PrimaryQueueResponse_SerializesCanonicalEnumName(
        PrintJobPriority priority,
        string expectedName)
    {
        JsonSerializerOptions options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() },
        };

        string json = JsonSerializer.Serialize(
            new JobQueuePrintJobDto { Priority = priority },
            options);

        Assert.Contains($"\"priority\":\"{expectedName}\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void UpdatePriorityRequest_UnknownEnumName_IsRejected()
    {
        JsonSerializerOptions options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() },
        };

        _ = Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<UpdateQueueJobPriorityRequest>(
                """{"newPriority":"Immediate"}""",
                options));
    }

    [Fact]
    public async Task UpdateJobPriorityAsync_UndefinedPriority_ReturnsBadRequest()
    {
        await using AppDbContext db = CreateContext();
        PrintJob job = CreateJob();
        db.PrintJobs.Add(job);
        await db.SaveChangesAsync();

        Mock<IPrintJobManagementService> service = new();
        service.Setup(value => value.UpdateJobPriorityAsync(
                job.Id.ToString(),
                (PrintJobPriority)99,
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ValidationException("Priority value 99 is not a valid PrintJobPriority."));
        JobQueueAnalyticsController controller = CreateController(service.Object, db, job);

        IActionResult result = await controller.UpdateJobPriorityAsync(
            job.Id.ToString(),
            new UpdateQueueJobPriorityRequest { NewPriority = (PrintJobPriority)99 });

        _ = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal((int)PrintJobPriority.Normal, job.Priority);
        service.VerifyAll();
    }

    [Theory]
    [InlineData(PrintJobPriority.Low)]
    [InlineData(PrintJobPriority.Normal)]
    [InlineData(PrintJobPriority.High)]
    [InlineData(PrintJobPriority.Urgent)]
    public async Task UpdateJobPriorityAsync_DefinedPriority_ForwardsCanonicalEnum(
        PrintJobPriority priority)
    {
        await using AppDbContext db = CreateContext();
        PrintJob job = CreateJob();
        db.PrintJobs.Add(job);
        await db.SaveChangesAsync();
        QueuedPrintJobDto expected = new()
        {
            Id = job.Id.ToString(),
            Name = job.Name,
            Priority = priority,
        };

        Mock<IPrintJobManagementService> service = new();
        service.Setup(value => value.UpdateJobPriorityAsync(
                job.Id.ToString(),
                priority,
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);
        JobQueueAnalyticsController controller = CreateController(service.Object, db, job);

        IActionResult result = await controller.UpdateJobPriorityAsync(
            job.Id.ToString(),
            new UpdateQueueJobPriorityRequest { NewPriority = priority });

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result);
        Assert.Same(expected, ok.Value);
        service.VerifyAll();
    }

    private static JobQueueAnalyticsController CreateController(
        IPrintJobManagementService service,
        AppDbContext db,
        PrintJob job)
    {
        var controller = new JobQueueAnalyticsController(
            service,
            Mock.Of<IJobCostCalculationService>(),
            Mock.Of<ILogger<JobQueueAnalyticsController>>(),
            db);
        DefaultHttpContext httpContext = new();
        httpContext.User = new ClaimsPrincipal(
            new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())],
                "Test"));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext,
        };
        controller.Request.Headers.IfMatch = $"\"{Convert.ToBase64String(job.RowVersion!)}\"";
        return controller;
    }

    private static AppDbContext CreateContext()
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private static PrintJob CreateJob() =>
        new()
        {
            Id = Guid.NewGuid(),
            Revision = 1,
            Name = "priority.gcode",
            Priority = (int)PrintJobPriority.Normal,
            Status = PrintJobStatus.Queued,
            QueuedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
}
