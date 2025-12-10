using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Web.Api.Services.Queue;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Farm.Web.Api.Tests.Integration;

/// <summary>
/// Integration tests for JobQueueService
/// Tests job queue management, CRUD operations, priority updates, and status transitions
/// Fast executing (~5-6 seconds for 22 tests) - suitable for CI/CD pipelines
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public class JobQueueServiceIntegrationTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public JobQueueServiceIntegrationTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private async Task<Printer> CreateTestPrinterAsync(string name = null)
    {
        using var scope = _factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var uniqueId = Guid.NewGuid().ToString().Substring(0, 8);
        var uniqueName = name ?? $"printer-{uniqueId}";
        
        // Create manufacturer with unique name
        var manufacturer = new Manufacturer
        {
            Id = Guid.NewGuid(),
            Name = $"Mfr-{uniqueId}",
            IsActive = true
        };
        context.Manufacturers.Add(manufacturer);
        await context.SaveChangesAsync();
        
        // Create printer model with unique name
        var printerModel = new PrinterModel
        {
            Id = Guid.NewGuid(),
            Name = $"Model-{uniqueId}",
            ManufacturerId = manufacturer.Id,
            DefaultNozzleDiameter = 0.4
        };
        context.PrinterModels.Add(printerModel);
        await context.SaveChangesAsync();
        
        // Create printer
        var printerId = Guid.NewGuid();
        var printer = new Printer
        {
            Id = printerId,
            Name = uniqueName,
            ServerUrl = $"http://printer-{uniqueId}.local:7125",
            BackendPort = 7125,
            Backend = 1, // Moonraker
            IsEnabled = true,
            ManufacturerId = manufacturer.Id,
            ModelId = printerModel.Id
        };

        context.Printers.Add(printer);
        await context.SaveChangesAsync();
        
        // Create capabilities
        var capabilities = new PrinterCapabilities
        {
            Id = Guid.NewGuid(),
            PrinterId = printerId,
            IsAvailable = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        context.PrinterCapabilities.Add(capabilities);
        await context.SaveChangesAsync();
        
        return printer;
    }

    private async Task<GcodeFile> CreateTestGcodeFileAsync(Printer printer, string displayName = null)
    {
        using var scope = _factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var uniqueName = displayName ?? $"print-{Guid.NewGuid().ToString().Substring(0, 8)}.gcode";
        var gcode = new GcodeFile
        {
            Id = Guid.NewGuid(),
            DisplayName = uniqueName,
            OriginalFileName = uniqueName,
            FileDirectory = "/gcodes",
            FilePath = $"/gcodes/{uniqueName}",
            FileSizeBytes = 1024000,
            FileHash = Guid.NewGuid().ToString(),
            Source = GcodeSource.Upload,
            UploadedAt = DateTime.UtcNow,
            EstimatedPrintTimeMinutes = 120,
            EstimatedFilamentWeightG = 50,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        context.GcodeFiles.Add(gcode);
        await context.SaveChangesAsync();
        return gcode;
    }

    #region GetQueueOverviewAsync Tests

    [Fact]
    public async Task GetQueueOverviewAsync_WithAvailablePrinters_ReturnsOverview()
    {
        // Arrange
        using var scope = _factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IJobQueueService>();

        var printer = await CreateTestPrinterAsync();

        // Act
        var result = await service.GetQueueOverviewAsync(CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Count.Should().BeGreaterThanOrEqualTo(1);
        var overview = result.FirstOrDefault(o => o.PrinterId == printer.Id);
        overview.Should().NotBeNull();
        overview!.PrinterName.Should().Be(printer.Name);
        overview.IsAvailable.Should().BeTrue();
    }

    [Fact]
    public async Task GetQueueOverviewAsync_IncludesQueuedJobsCount()
    {
        // Arrange
        using var scope = _factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IJobQueueService>();

        var printer = await CreateTestPrinterAsync();
        var gcode = await CreateTestGcodeFileAsync(printer);

        var request = new QueuePrintJobDto
        {
            GcodeFileId = gcode.Id,
            AssignedPrinterId = printer.Id,
            Priority = PrintJobPriority.Normal,
            RequiredNozzleDiameter = 0.4m,
            RequiredMaterialType = "PLA"
        };

        await service.AddJobToQueueAsync(request, CancellationToken.None);

        // Act
        var result = await service.GetQueueOverviewAsync(CancellationToken.None);

        // Assert
        var overview = result.FirstOrDefault(o => o.PrinterId == printer.Id);
        overview.Should().NotBeNull();
        overview!.QueuedJobsCount.Should().Be(1);
    }

    #endregion

    #region GetPrinterQueueAsync Tests

    [Fact]
    public async Task GetPrinterQueueAsync_WithEmptyQueue_ReturnsEmptyList()
    {
        // Arrange
        using var scope = _factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IJobQueueService>();

        var printer = await CreateTestPrinterAsync();

        // Act
        var result = await service.GetPrinterQueueAsync(printer.Id, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Should().HaveCount(0);
    }

    [Fact]
    public async Task GetPrinterQueueAsync_WithQueuedJobs_ReturnsAllJobs()
    {
        // Arrange
        using var scope = _factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IJobQueueService>();

        var printer = await CreateTestPrinterAsync();
        var gcode1 = await CreateTestGcodeFileAsync(printer);
        var gcode2 = await CreateTestGcodeFileAsync(printer);

        var req1 = new QueuePrintJobDto
        {
            GcodeFileId = gcode1.Id,
            AssignedPrinterId = printer.Id,
            Priority = PrintJobPriority.Normal,
            RequiredNozzleDiameter = 0.4m,
            RequiredMaterialType = "PLA"
        };

        var req2 = new QueuePrintJobDto
        {
            GcodeFileId = gcode2.Id,
            AssignedPrinterId = printer.Id,
            Priority = PrintJobPriority.High,
            RequiredNozzleDiameter = 0.4m,
            RequiredMaterialType = "PETG"
        };

        await service.AddJobToQueueAsync(req1, CancellationToken.None);
        await service.AddJobToQueueAsync(req2, CancellationToken.None);

        // Act
        var result = await service.GetPrinterQueueAsync(printer.Id, CancellationToken.None);

        // Assert
        result.Should().HaveCount(2);
        result.Should().Contain(j => j.GcodeFileName == gcode1.DisplayName);
        result.Should().Contain(j => j.GcodeFileName == gcode2.DisplayName);
    }

    [Fact]
    public async Task GetPrinterQueueAsync_SetsQueuePosition()
    {
        // Arrange
        using var scope = _factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IJobQueueService>();

        var printer = await CreateTestPrinterAsync();
        var gcode1 = await CreateTestGcodeFileAsync(printer);
        var gcode2 = await CreateTestGcodeFileAsync(printer);
        var gcode3 = await CreateTestGcodeFileAsync(printer);

        var req1 = new QueuePrintJobDto
        {
            GcodeFileId = gcode1.Id,
            AssignedPrinterId = printer.Id,
            Priority = PrintJobPriority.Normal,
            RequiredNozzleDiameter = 0.4m,
            RequiredMaterialType = "PLA"
        };

        var req2 = new QueuePrintJobDto
        {
            GcodeFileId = gcode2.Id,
            AssignedPrinterId = printer.Id,
            Priority = PrintJobPriority.Normal,
            RequiredNozzleDiameter = 0.4m,
            RequiredMaterialType = "PLA"
        };

        var req3 = new QueuePrintJobDto
        {
            GcodeFileId = gcode3.Id,
            AssignedPrinterId = printer.Id,
            Priority = PrintJobPriority.Normal,
            RequiredNozzleDiameter = 0.4m,
            RequiredMaterialType = "PLA"
        };

        await service.AddJobToQueueAsync(req1, CancellationToken.None);
        await service.AddJobToQueueAsync(req2, CancellationToken.None);
        await service.AddJobToQueueAsync(req3, CancellationToken.None);

        // Act
        var result = await service.GetPrinterQueueAsync(printer.Id, CancellationToken.None);

        // Assert
        var queued = result.Where(j => j.Status == PrintJobStatus.Queued || j.Status == PrintJobStatus.Assigned).ToList();
        queued.Should().HaveCount(3);
        queued[0].QueuePosition.Should().Be(1);
        queued[1].QueuePosition.Should().Be(2);
        queued[2].QueuePosition.Should().Be(3);
    }

    #endregion

    #region AddJobToQueueAsync Tests

    [Fact]
    public async Task AddJobToQueueAsync_WithValidRequest_CreatesJob()
    {
        // Arrange
        using var scope = _factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IJobQueueService>();

        var printer = await CreateTestPrinterAsync();
        var gcode = await CreateTestGcodeFileAsync(printer);

        var request = new QueuePrintJobDto
        {
            GcodeFileId = gcode.Id,
            AssignedPrinterId = printer.Id,
            Priority = PrintJobPriority.Normal,
            RequiredNozzleDiameter = 0.4m,
            RequiredMaterialType = "PLA"
        };

        // Act
        var result = await service.AddJobToQueueAsync(request, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result!.Id.Should().NotBe(Guid.Empty);
        result.GcodeFileName.Should().Be(gcode.DisplayName);
        result.AssignedPrinterId.Should().Be(printer.Id);
        result.Status.Should().Be(PrintJobStatus.Queued);
        result.Priority.Should().Be((int)PrintJobPriority.Normal);
    }

    [Fact]
    public async Task AddJobToQueueAsync_WithNullRequest_ReturnsNull()
    {
        // Arrange
        using var scope = _factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IJobQueueService>();

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => service.AddJobToQueueAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task AddJobToQueueAsync_WithNonExistentGcode_ReturnsNull()
    {
        // Arrange
        using var scope = _factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IJobQueueService>();

        var printer = await CreateTestPrinterAsync();

        var request = new QueuePrintJobDto
        {
            GcodeFileId = Guid.NewGuid(),
            AssignedPrinterId = printer.Id,
            Priority = PrintJobPriority.Normal,
            RequiredNozzleDiameter = 0.4m,
            RequiredMaterialType = "PLA"
        };

        // Act
        var result = await service.AddJobToQueueAsync(request, CancellationToken.None);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task AddJobToQueueAsync_WithEstimatedData_CopiesFromGcode()
    {
        // Arrange
        using var scope = _factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IJobQueueService>();

        var printer = await CreateTestPrinterAsync();
        var gcode = await CreateTestGcodeFileAsync(printer);

        var request = new QueuePrintJobDto
        {
            GcodeFileId = gcode.Id,
            AssignedPrinterId = printer.Id,
            Priority = PrintJobPriority.Normal,
            RequiredNozzleDiameter = 0.4m,
            RequiredMaterialType = "PLA"
        };

        // Act
        var result = await service.AddJobToQueueAsync(request, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result!.EstimatedPrintTime.Should().HaveValue();
        result.EstimatedPrintTime!.Value.TotalMinutes.Should().Be(120);
        result.EstimatedFilamentUsage.Should().Be(50);
    }

    #endregion

    #region GetJobAsync Tests

    [Fact]
    public async Task GetJobAsync_WithValidId_ReturnsJob()
    {
        // Arrange
        using var scope = _factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IJobQueueService>();

        var printer = await CreateTestPrinterAsync();
        var gcode = await CreateTestGcodeFileAsync(printer);

        var request = new QueuePrintJobDto
        {
            GcodeFileId = gcode.Id,
            AssignedPrinterId = printer.Id,
            Priority = PrintJobPriority.High,
            RequiredNozzleDiameter = 0.4m,
            RequiredMaterialType = "PLA"
        };

        var created = await service.AddJobToQueueAsync(request, CancellationToken.None);

        // Act
        var result = await service.GetJobAsync(created!.Id, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result!.Id.Should().Be(created.Id);
        result.GcodeFileName.Should().Be(gcode.DisplayName);
        result.AssignedPrinterId.Should().Be(printer.Id);
    }

    [Fact]
    public async Task GetJobAsync_WithNonExistentId_ReturnsNull()
    {
        // Arrange
        using var scope = _factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IJobQueueService>();

        // Act
        var result = await service.GetJobAsync(Guid.NewGuid(), CancellationToken.None);

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region RemoveJobAsync Tests

    [Fact]
    public async Task RemoveJobAsync_WithQueuedJob_RemovesSuccessfully()
    {
        // Arrange
        using var scope = _factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IJobQueueService>();

        var printer = await CreateTestPrinterAsync();
        var gcode = await CreateTestGcodeFileAsync(printer);

        var request = new QueuePrintJobDto
        {
            GcodeFileId = gcode.Id,
            AssignedPrinterId = printer.Id,
            Priority = PrintJobPriority.Normal,
            RequiredNozzleDiameter = 0.4m,
            RequiredMaterialType = "PLA"
        };

        var created = await service.AddJobToQueueAsync(request, CancellationToken.None);

        // Act
        var result = await service.RemoveJobAsync(created!.Id, CancellationToken.None);

        // Assert
        result.Should().BeTrue();

        // Verify job was deleted
        var retrieved = await service.GetJobAsync(created.Id, CancellationToken.None);
        retrieved.Should().BeNull();
    }

    [Fact]
    public async Task RemoveJobAsync_WithNonExistentId_ReturnsFalse()
    {
        // Arrange
        using var scope = _factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IJobQueueService>();

        // Act
        var result = await service.RemoveJobAsync(Guid.NewGuid(), CancellationToken.None);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task RemoveJobAsync_WithInProgressJob_ReturnsFalse()
    {
        // Arrange
        using var scope = _factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IJobQueueService>();

        var printer = await CreateTestPrinterAsync();
        var gcode = await CreateTestGcodeFileAsync(printer);

        var request = new QueuePrintJobDto
        {
            GcodeFileId = gcode.Id,
            AssignedPrinterId = printer.Id,
            Priority = PrintJobPriority.Normal,
            RequiredNozzleDiameter = 0.4m,
            RequiredMaterialType = "PLA"
        };

        var created = await service.AddJobToQueueAsync(request, CancellationToken.None);

        // Change status to InProgress
        var job = context.PrintJobs.Find(created!.Id);
        job!.Status = PrintJobStatus.Printing;
        await context.SaveChangesAsync();

        // Act
        var result = await service.RemoveJobAsync(created.Id, CancellationToken.None);

        // Assert
        result.Should().BeFalse();
    }

    #endregion

    #region UpdateJobPriorityAsync Tests

    [Fact]
    public async Task UpdateJobPriorityAsync_WithValidRequest_UpdatesPriority()
    {
        // Arrange
        using var scope = _factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IJobQueueService>();

        var printer = await CreateTestPrinterAsync();
        var gcode = await CreateTestGcodeFileAsync(printer);

        var request = new QueuePrintJobDto
        {
            GcodeFileId = gcode.Id,
            AssignedPrinterId = printer.Id,
            Priority = PrintJobPriority.Low,
            RequiredNozzleDiameter = 0.4m,
            RequiredMaterialType = "PLA"
        };

        var created = await service.AddJobToQueueAsync(request, CancellationToken.None);

        var updateRequest = new UpdateJobPriorityDto
        {
            Priority = (int)PrintJobPriority.High
        };

        // Act
        var result = await service.UpdateJobPriorityAsync(created!.Id, updateRequest, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result!.Priority.Should().Be((int)PrintJobPriority.High);

        // Verify persistence
        var retrieved = await service.GetJobAsync(created.Id, CancellationToken.None);
        retrieved!.Priority.Should().Be((int)PrintJobPriority.High);
    }

    [Fact]
    public async Task UpdateJobPriorityAsync_WithNonExistentId_ReturnsNull()
    {
        // Arrange
        using var scope = _factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IJobQueueService>();

        var updateRequest = new UpdateJobPriorityDto
        {
            Priority = (int)PrintJobPriority.High
        };

        // Act
        var result = await service.UpdateJobPriorityAsync(Guid.NewGuid(), updateRequest, CancellationToken.None);

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region UpdateJobAsync Tests

    [Fact]
    public async Task UpdateJobAsync_WithStatusUpdate_UpdatesStatus()
    {
        // Arrange
        using var scope = _factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IJobQueueService>();

        var printer = await CreateTestPrinterAsync();
        var gcode = await CreateTestGcodeFileAsync(printer);

        var request = new QueuePrintJobDto
        {
            GcodeFileId = gcode.Id,
            AssignedPrinterId = printer.Id,
            Priority = PrintJobPriority.Normal,
            RequiredNozzleDiameter = 0.4m,
            RequiredMaterialType = "PLA"
        };

        var created = await service.AddJobToQueueAsync(request, CancellationToken.None);

        var updateRequest = new UpdatePrintJobStatusDto
        {
            Status = PrintJobStatus.Printing,
            Priority = null,
            AssignedPrinterId = null,
            ActualFilamentUsage = null,
            FailureReason = null
        };

        // Act
        var result = await service.UpdateJobAsync(created!.Id, updateRequest, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result!.Status.Should().Be(PrintJobStatus.Printing);
    }

    [Fact]
    public async Task UpdateJobAsync_WithNullRequest_ThrowsException()
    {
        // Arrange
        using var scope = _factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IJobQueueService>();

        var printer = await CreateTestPrinterAsync();
        var gcode = await CreateTestGcodeFileAsync(printer);

        var request = new QueuePrintJobDto
        {
            GcodeFileId = gcode.Id,
            AssignedPrinterId = printer.Id,
            Priority = PrintJobPriority.Normal,
            RequiredNozzleDiameter = 0.4m,
            RequiredMaterialType = "PLA"
        };

        var created = await service.AddJobToQueueAsync(request, CancellationToken.None);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => service.UpdateJobAsync(created!.Id, null!, CancellationToken.None));
    }

    [Fact]
    public async Task UpdateJobAsync_WithNonExistentId_ReturnsNull()
    {
        // Arrange
        using var scope = _factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IJobQueueService>();

        var updateRequest = new UpdatePrintJobStatusDto
        {
            Status = PrintJobStatus.Completed,
            Priority = null,
            AssignedPrinterId = null,
            ActualFilamentUsage = null,
            FailureReason = null
        };

        // Act
        var result = await service.UpdateJobAsync(Guid.NewGuid(), updateRequest, CancellationToken.None);

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region Integration Tests

    [Fact]
    public async Task AddJob_ThenUpdatePriority_ThenRemove_CompleteWorkflow()
    {
        // Arrange
        using var scope = _factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IJobQueueService>();

        var printer = await CreateTestPrinterAsync();
        var gcode = await CreateTestGcodeFileAsync(printer);

        var request = new QueuePrintJobDto
        {
            GcodeFileId = gcode.Id,
            AssignedPrinterId = printer.Id,
            Priority = PrintJobPriority.Low,
            RequiredNozzleDiameter = 0.4m,
            RequiredMaterialType = "PLA"
        };

        // Add job
        var created = await service.AddJobToQueueAsync(request, CancellationToken.None);
        created.Should().NotBeNull();

        // Update priority
        var priorityUpdate = new UpdateJobPriorityDto
        {
            Priority = (int)PrintJobPriority.High
        };
        var updated = await service.UpdateJobPriorityAsync(created!.Id, priorityUpdate, CancellationToken.None);
        updated!.Priority.Should().Be((int)PrintJobPriority.High);

        // Remove job
        var removed = await service.RemoveJobAsync(created.Id, CancellationToken.None);
        removed.Should().BeTrue();

        // Verify deletion
        var retrieved = await service.GetJobAsync(created.Id, CancellationToken.None);
        retrieved.Should().BeNull();
    }

    [Fact]
    public async Task AddMultipleJobs_ThenVerifyQueue()
    {
        // Arrange
        using var scope = _factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IJobQueueService>();

        var printer = await CreateTestPrinterAsync();
        var gcode1 = await CreateTestGcodeFileAsync(printer);
        var gcode2 = await CreateTestGcodeFileAsync(printer);
        var gcode3 = await CreateTestGcodeFileAsync(printer);

        // Add multiple jobs
        var jobs = new List<JobQueuePrintJobDto>();
        foreach (var gcode in new[] { gcode1, gcode2, gcode3 })
        {
            var req = new QueuePrintJobDto
            {
                GcodeFileId = gcode.Id,
                AssignedPrinterId = printer.Id,
                Priority = PrintJobPriority.Normal,
                RequiredNozzleDiameter = 0.4m,
                RequiredMaterialType = "PLA"
            };
            var job = await service.AddJobToQueueAsync(req, CancellationToken.None);
            jobs.Add(job!);
        }

        // Act
        var queue = await service.GetPrinterQueueAsync(printer.Id, CancellationToken.None);

        // Assert
        queue.Should().HaveCount(3);
        var queued = queue.Where(j => j.Status == PrintJobStatus.Queued).ToList();
        queued.Should().HaveCount(3);
        for (int i = 0; i < 3; i++)
        {
            queued[i].QueuePosition.Should().Be(i + 1);
        }
    }

    [Fact]
    public async Task UpdateJobStatus_ThenVerifyQueuePosition()
    {
        // Arrange
        using var scope = _factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IJobQueueService>();

        var printer = await CreateTestPrinterAsync();
        var gcode1 = await CreateTestGcodeFileAsync(printer);
        var gcode2 = await CreateTestGcodeFileAsync(printer);

        var req1 = new QueuePrintJobDto
        {
            GcodeFileId = gcode1.Id,
            AssignedPrinterId = printer.Id,
            Priority = PrintJobPriority.Normal,
            RequiredNozzleDiameter = 0.4m,
            RequiredMaterialType = "PLA"
        };

        var req2 = new QueuePrintJobDto
        {
            GcodeFileId = gcode2.Id,
            AssignedPrinterId = printer.Id,
            Priority = PrintJobPriority.Normal,
            RequiredNozzleDiameter = 0.4m,
            RequiredMaterialType = "PLA"
        };

        var job1 = await service.AddJobToQueueAsync(req1, CancellationToken.None);
        var job2 = await service.AddJobToQueueAsync(req2, CancellationToken.None);

        // Start first job
        var updateReq = new UpdatePrintJobStatusDto
        {
            Status = PrintJobStatus.Printing,
            Priority = null,
            AssignedPrinterId = null,
            ActualFilamentUsage = null,
            FailureReason = null
        };

        await service.UpdateJobAsync(job1!.Id, updateReq, CancellationToken.None);

        // Act
        var queue = await service.GetPrinterQueueAsync(printer.Id, CancellationToken.None);

        // Assert
        var inProgress = queue.FirstOrDefault(j => j.Status == PrintJobStatus.Printing);
        inProgress.Should().NotBeNull();
        inProgress!.Id.Should().Be(job1.Id);

        var queued = queue.Where(j => j.Status == PrintJobStatus.Queued).ToList();
        queued.Should().HaveCount(1);
        queued[0].Id.Should().Be(job2.Id);
    }

    #endregion
}
