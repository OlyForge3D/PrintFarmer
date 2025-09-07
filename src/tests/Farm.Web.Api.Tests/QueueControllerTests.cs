using System.Net;
using System.Net.Http.Json;
using Farm.Web.Shared;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Farm.Web.Api.Data;
using Microsoft.EntityFrameworkCore;
using Farm.Web.Api.Domain;

namespace Farm.Web.Api.Tests;

/// <summary>
/// Integration tests for QueueController (job queue management)
/// </summary>
public class QueueControllerTests : IClassFixture<CustomWebApplicationFactory>, IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public QueueControllerTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = _factory.CreateClient();
    }

    public async Task InitializeAsync()
    {
        await CleanDatabaseAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task GetQueueOverview_ShouldReturnEmptyOverview_WhenNoPrintersExist()
    {
    // Arrange
        
        // Act
        var response = await _client.GetAsync("/api/queue/overview");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var overview = await response.Content.ReadFromJsonAsync<QueueOverviewDto[]>();
        overview.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public async Task GetQueueOverview_ShouldReturnPrinterQueues_WhenPrintersExist()
    {
    // Arrange - Create a printer with capabilities
        var printer = await CreateTestPrinterWithCapabilitiesAsync("Test Printer");

        // Act
        var response = await _client.GetAsync("/api/queue/overview");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var overview = await response.Content.ReadFromJsonAsync<QueueOverviewDto[]>();
        
        overview.Should().NotBeNull().And.HaveCount(1);
        overview![0].PrinterId.Should().Be(printer.Id);
        overview[0].PrinterName.Should().Be("Test Printer");
        overview[0].IsAvailable.Should().BeTrue();
        overview[0].QueuedJobsCount.Should().Be(0);
        overview[0].CurrentJobId.Should().BeNull();
    }

    [Fact]
    public async Task GetPrinterQueue_ShouldReturnEmptyQueue_WhenNoPrintersExist()
    {
        // Arrange
        var printerId = Guid.NewGuid();

        // Act
        var response = await _client.GetAsync($"/api/queue/printer/{printerId}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var jobs = await response.Content.ReadFromJsonAsync<JobQueuePrintJobDto[]>();
        jobs.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public async Task GetPrinterQueue_ShouldReturnJobs_WhenJobsExistForPrinter()
    {
        // Arrange - Create printer, gcode file, and job
        var printer = await CreateTestPrinterWithCapabilitiesAsync("Queue Test Printer");
        var gcodeFile = await CreateTestGcodeFileAsync("test.gcode");
        var job = await CreateTestPrintJobAsync(gcodeFile.Id, printer.Id);

        // Act
        var response = await _client.GetAsync($"/api/queue/printer/{printer.Id}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var jobs = await response.Content.ReadFromJsonAsync<JobQueuePrintJobDto[]>();
        
        jobs.Should().NotBeNull().And.HaveCount(1);
        jobs![0].Id.Should().Be(job.Id);
        jobs[0].AssignedPrinterId.Should().Be(printer.Id);
        jobs[0].AssignedPrinterName.Should().Be("Queue Test Printer");
        jobs[0].Status.Should().Be(PrintJobStatusDto.Queued);
    }

    [Fact]
    public async Task AddJobToQueue_ShouldCreateJob_WhenValidDataProvided()
    {
        // Arrange - Create prerequisites
        var printer = await CreateTestPrinterWithCapabilitiesAsync("Job Test Printer");
        var gcodeFile = await CreateTestGcodeFileAsync("job-test.gcode");
        
        var request = new QueuePrintJobDto
        {
            GcodeFileId = gcodeFile.Id,
            AssignedPrinterId = printer.Id,
            Priority = PrintJobPriority.Normal,
            RequiredNozzleDiameter = 0.4m,
            RequiredMaterialType = "PLA"
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/queue/jobs", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var job = await response.Content.ReadFromJsonAsync<JobQueuePrintJobDto>();
        
        job.Should().NotBeNull();
        job!.Id.Should().NotBeEmpty();
        job.GcodeFileId.Should().Be(gcodeFile.Id);
        job.AssignedPrinterId.Should().Be(printer.Id);
        job.Status.Should().Be(PrintJobStatusDto.Queued);
        job.Priority.Should().Be((int)PrintJobPriority.Normal);
        job.RequiredNozzleDiameter.Should().Be(0.4m);
        job.RequiredMaterialType.Should().Be("PLA");
    }

    [Fact]
    public async Task AddJobToQueue_ShouldAutoAssignPrinter_WhenNoAssignedPrinter()
    {
        // Arrange - Create prerequisites
        var printer = await CreateTestPrinterWithCapabilitiesAsync("Auto Assign Printer");
        var gcodeFile = await CreateTestGcodeFileAsync("auto-assign.gcode");
        
        var request = new QueuePrintJobDto
        {
            GcodeFileId = gcodeFile.Id,
            AssignedPrinterId = null, // Let system auto-assign
            Priority = PrintJobPriority.Normal
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/queue/jobs", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var job = await response.Content.ReadFromJsonAsync<JobQueuePrintJobDto>();
        
        job.Should().NotBeNull();
        job!.AssignedPrinterId.Should().Be(printer.Id); // Should auto-assign to available printer
        job.AssignedPrinterName.Should().Be("Auto Assign Printer");
    }

    [Fact]
    public async Task AddJobToQueue_ShouldReturnBadRequest_WhenGcodeFileNotFound()
    {
        // Arrange
        var nonExistentGcodeId = Guid.NewGuid();
        var request = new QueuePrintJobDto
        {
            GcodeFileId = nonExistentGcodeId,
            Priority = PrintJobPriority.Normal
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/queue/jobs", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("G-code file not found");
    }

    [Fact]
    public async Task AddJobToQueue_ShouldReturnBadRequest_WhenNoCompatiblePrinter()
    {
        // Arrange - Create gcode file but no compatible printer
        var gcodeFile = await CreateTestGcodeFileAsync("incompatible.gcode");
        
        var request = new QueuePrintJobDto
        {
            GcodeFileId = gcodeFile.Id,
            AssignedPrinterId = null,
            Priority = PrintJobPriority.Normal,
            RequiredNozzleDiameter = 2.0m // Unusual size to ensure no match
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/queue/jobs", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("No compatible printer available");
    }

    [Fact]
    public async Task GetJob_ShouldReturnJob_WhenJobExists()
    {
        // Arrange - Create job
        var printer = await CreateTestPrinterWithCapabilitiesAsync("Get Job Printer");
        var gcodeFile = await CreateTestGcodeFileAsync("get-job.gcode");
        var job = await CreateTestPrintJobAsync(gcodeFile.Id, printer.Id);

        // Act
        var response = await _client.GetAsync($"/api/queue/jobs/{job.Id}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var retrievedJob = await response.Content.ReadFromJsonAsync<JobQueuePrintJobDto>();
        
        retrievedJob.Should().NotBeNull();
        retrievedJob!.Id.Should().Be(job.Id);
        retrievedJob.GcodeFileId.Should().Be(gcodeFile.Id);
        retrievedJob.AssignedPrinterId.Should().Be(printer.Id);
    }

    [Fact]
    public async Task GetJob_ShouldReturnNotFound_WhenJobDoesNotExist()
    {
        // Arrange
        var nonExistentJobId = Guid.NewGuid();

        // Act
        var response = await _client.GetAsync($"/api/queue/jobs/{nonExistentJobId}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task RemoveJobFromQueue_ShouldDeleteJob_WhenJobExists()
    {
        // Arrange - Create job
        var printer = await CreateTestPrinterWithCapabilitiesAsync("Remove Job Printer");
        var gcodeFile = await CreateTestGcodeFileAsync("remove-job.gcode");
        var job = await CreateTestPrintJobAsync(gcodeFile.Id, printer.Id);

        // Act
        var response = await _client.DeleteAsync($"/api/queue/jobs/{job.Id}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Verify job is deleted
        var getResponse = await _client.GetAsync($"/api/queue/jobs/{job.Id}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task RemoveJobFromQueue_ShouldReturnNotFound_WhenJobDoesNotExist()
    {
        // Arrange
        var nonExistentJobId = Guid.NewGuid();

        // Act
        var response = await _client.DeleteAsync($"/api/queue/jobs/{nonExistentJobId}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task RemoveJobFromQueue_ShouldReturnBadRequest_WhenJobAlreadyStarted()
    {
        // Arrange - Create a job that's already printing
        var printer = await CreateTestPrinterWithCapabilitiesAsync("Started Job Printer");
        var gcodeFile = await CreateTestGcodeFileAsync("started-job.gcode");
        var job = await CreateTestPrintJobAsync(gcodeFile.Id, printer.Id, PrintJobStatus.Printing);

        // Act
        var response = await _client.DeleteAsync($"/api/queue/jobs/{job.Id}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("Cannot remove job that is already started");
    }

    [Fact]
    public async Task UpdateJobPriority_ShouldUpdatePriority_WhenJobExists()
    {
        // Arrange - Create job
        var printer = await CreateTestPrinterWithCapabilitiesAsync("Priority Job Printer");
        var gcodeFile = await CreateTestGcodeFileAsync("priority-job.gcode");
        var job = await CreateTestPrintJobAsync(gcodeFile.Id, printer.Id);

        var updateRequest = new UpdateJobPriorityDto
        {
            Priority = (int)PrintJobPriority.High
        };

        // Act
        var response = await _client.PatchAsJsonAsync($"/api/queue/jobs/{job.Id}/priority", updateRequest);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var updatedJob = await response.Content.ReadFromJsonAsync<JobQueuePrintJobDto>();
        
        updatedJob.Should().NotBeNull();
        updatedJob!.Id.Should().Be(job.Id);
        updatedJob.Priority.Should().Be((int)PrintJobPriority.High);
    }

    [Fact]
    public async Task UpdateJobPriority_ShouldReturnNotFound_WhenJobDoesNotExist()
    {
        // Arrange
        var nonExistentJobId = Guid.NewGuid();
        var updateRequest = new UpdateJobPriorityDto
        {
            Priority = (int)PrintJobPriority.High
        };

        // Act
        var response = await _client.PatchAsJsonAsync($"/api/queue/jobs/{nonExistentJobId}/priority", updateRequest);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData(PrintJobPriority.Low, 0)]
    [InlineData(PrintJobPriority.Normal, 1)]
    [InlineData(PrintJobPriority.High, 2)]
    public async Task QueuePriority_ShouldOrderJobsCorrectly(PrintJobPriority priority, int expectedPriorityValue)
    {
    // Arrange - Create printer and gcode file
        var printer = await CreateTestPrinterWithCapabilitiesAsync("Priority Order Printer");
        var gcodeFile = await CreateTestGcodeFileAsync("priority-order.gcode");
        
        var request = new QueuePrintJobDto
        {
            GcodeFileId = gcodeFile.Id,
            AssignedPrinterId = printer.Id,
            Priority = priority
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/queue/jobs", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var job = await response.Content.ReadFromJsonAsync<JobQueuePrintJobDto>();
        
        job.Should().NotBeNull();
        job!.Priority.Should().Be(expectedPriorityValue);
    }

    [Fact]
    public async Task GetQueueOverview_ShouldShowCorrectCounts_WhenJobsExist()
    {
    // Arrange - Create printer and multiple jobs
        var printer = await CreateTestPrinterWithCapabilitiesAsync("Count Test Printer");
        var gcodeFile = await CreateTestGcodeFileAsync("count-test.gcode");
        
        // Create 3 queued jobs
        await CreateTestPrintJobAsync(gcodeFile.Id, printer.Id, PrintJobStatus.Queued);
        await CreateTestPrintJobAsync(gcodeFile.Id, printer.Id, PrintJobStatus.Queued);
        await CreateTestPrintJobAsync(gcodeFile.Id, printer.Id, PrintJobStatus.Queued);
        
        // Create 1 printing job
        var currentJob = await CreateTestPrintJobAsync(gcodeFile.Id, printer.Id, PrintJobStatus.Printing);

        // Act
        var response = await _client.GetAsync("/api/queue/overview");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var overview = await response.Content.ReadFromJsonAsync<QueueOverviewDto[]>();
        
        overview.Should().NotBeNull().And.HaveCount(1);
        overview![0].QueuedJobsCount.Should().Be(3);
        overview[0].CurrentJobId.Should().Be(currentJob.Id);
        overview[0].CurrentJobName.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task PrinterCompatibility_ShouldMatchByNozzleDiameter()
    {
        // Arrange - Create printer with specific nozzle diameter
        var printer = await CreateTestPrinterWithCapabilitiesAsync("Nozzle Match Printer", nozzleDiameter: 0.6);
        var gcodeFile = await CreateTestGcodeFileAsync("nozzle-test.gcode");
        
        var request = new QueuePrintJobDto
        {
            GcodeFileId = gcodeFile.Id,
            AssignedPrinterId = null, // Auto-assign
            Priority = PrintJobPriority.Normal,
            RequiredNozzleDiameter = 0.6m // Should match printer
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/queue/jobs", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var job = await response.Content.ReadFromJsonAsync<JobQueuePrintJobDto>();
        
        job.Should().NotBeNull();
        job!.AssignedPrinterId.Should().Be(printer.Id); // Should auto-assign to matching printer
    }

    [Fact]
    public async Task PrinterCompatibility_ShouldMatchByMaterialType()
    {
    // Arrange - Create printer with specific material support
        var printer = await CreateTestPrinterWithCapabilitiesAsync("Material Match Printer", supportedMaterials: ["PLA", "PETG", "ABS"]);
        var gcodeFile = await CreateTestGcodeFileAsync("material-test.gcode");
        
        var request = new QueuePrintJobDto
        {
            GcodeFileId = gcodeFile.Id,
            AssignedPrinterId = null, // Auto-assign
            Priority = PrintJobPriority.Normal,
            RequiredMaterialType = "PETG" // Should match printer
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/queue/jobs", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var job = await response.Content.ReadFromJsonAsync<JobQueuePrintJobDto>();
        
        job.Should().NotBeNull();
        job!.AssignedPrinterId.Should().Be(printer.Id); // Should auto-assign to matching printer
    }

    // Helper methods

    private async Task<Printer> CreateTestPrinterWithCapabilitiesAsync(
        string name, 
        double? nozzleDiameter = null,
        string[]? supportedMaterials = null)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var printer = new Printer
        {
            Id = Guid.NewGuid(),
            Name = name,
            ServerUrl = "http://test-printer:7125",
            Backend = 0 // Moonraker
        };

        var capabilities = new PrinterCapabilities
        {
            Id = Guid.NewGuid(),
            PrinterId = printer.Id,
            NozzleDiameter = nozzleDiameter ?? 0.4,
            SupportedMaterials = supportedMaterials ?? ["PLA", "PETG"],
            IsAvailable = true,
            LastUpdated = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        dbContext.Printers.Add(printer);
        dbContext.PrinterCapabilities.Add(capabilities);
        await dbContext.SaveChangesAsync();

        printer.Capabilities = capabilities;
        return printer;
    }

    private async Task<GcodeFile> CreateTestGcodeFileAsync(string filename)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var gcodeFile = new GcodeFile
        {
            Id = Guid.NewGuid(),
            OriginalFileName = filename,
            DisplayName = Path.GetFileNameWithoutExtension(filename),
            FilePath = Path.Combine(Path.GetTempPath(), filename),
            FileSizeBytes = 1024,
            FileHash = Guid.NewGuid().ToString("N"),
            Source = GcodeSource.Upload,
            UploadedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        dbContext.GcodeFiles.Add(gcodeFile);
        await dbContext.SaveChangesAsync();

        return gcodeFile;
    }

    private async Task<PrintJob> CreateTestPrintJobAsync(
        Guid gcodeFileId, 
        Guid printerId, 
        PrintJobStatus status = PrintJobStatus.Queued)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var printJob = new PrintJob
        {
            Id = Guid.NewGuid(),
            Name = $"Test Job {Guid.NewGuid():N}",
            GcodeFileId = gcodeFileId,
            AssignedPrinterId = printerId,
            Status = status,
            Priority = (int)PrintJobPriority.Normal,
            QueuePosition = 1,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            QueuedAt = DateTime.UtcNow
        };

        if (status == PrintJobStatus.Printing)
        {
            printJob.ActualStartTime = DateTime.UtcNow;
        }

        dbContext.PrintJobs.Add(printJob);
        await dbContext.SaveChangesAsync();

        return printJob;
    }
    
    /// <summary>
    /// Clean the database by removing all test data
    /// </summary>
    private async Task CleanDatabaseAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Remove data in dependency order
        dbContext.PrintJobs.RemoveRange(dbContext.PrintJobs);
        dbContext.PrinterCapabilities.RemoveRange(dbContext.PrinterCapabilities);
        dbContext.Printers.RemoveRange(dbContext.Printers);
        dbContext.GcodeFiles.RemoveRange(dbContext.GcodeFiles);
        dbContext.Models3D.RemoveRange(dbContext.Models3D);
        dbContext.SlicerProfiles.RemoveRange(dbContext.SlicerProfiles);
        
        await dbContext.SaveChangesAsync();
    }
}