using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Queue;
using Farm.Infrastructure.Services.PrinterGroups;
using Farm.Infrastructure.Services.Queue;
using Farm.Web.Api.Tests.Builders;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services.Queue;

/// <summary>
/// Comprehensive tests for JobQueueService covering critical business logic paths
/// Target: 85%+ code coverage for job queue operations
/// </summary>
public class JobQueueServiceTests
{
    private readonly Mock<IQueueRepository> _mockRepo;
    private readonly Mock<IQueueDataService> _mockDataService;
    private readonly Mock<ILogger<JobQueueService>> _mockLogger;
    private readonly JobQueueService _sut; // System Under Test

    public JobQueueServiceTests()
    {
        _mockRepo = new Mock<IQueueRepository>();
        _mockDataService = new Mock<IQueueDataService>();
        _mockLogger = new Mock<ILogger<JobQueueService>>(MockBehavior.Loose);
        _sut = new JobQueueService(_mockRepo.Object, _mockDataService.Object, _mockLogger.Object);
    }

    #region Constructor Tests

    [Fact]
    public void Constructor_WithNullRepository_ThrowsArgumentNullException()
    {
        // Act & Assert
        System.Action act = () => new JobQueueService(null!, _mockDataService.Object, _mockLogger.Object);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_WithNullDataService_ThrowsArgumentNullException()
    {
        // Act & Assert
        System.Action act = () => new JobQueueService(_mockRepo.Object, null!, _mockLogger.Object);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_WithNullLogger_ThrowsArgumentNullException()
    {
        // Act & Assert
        System.Action act = () => new JobQueueService(_mockRepo.Object, _mockDataService.Object, null!);
        act.Should().Throw<ArgumentNullException>();
    }

    #endregion

    #region GetQueueOverviewAsync Tests

    [Fact]
    public async Task GetQueueOverviewAsync_WithNoPrinters_ReturnsEmptyList()
    {
        // Arrange
        _mockDataService.Setup(x => x.GetAvailablePrintersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Printer>());
        _mockDataService.Setup(x => x.GetPrintJobsForPrintersAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PrintJob>());

        // Act
        IReadOnlyList<QueueOverviewDto> result = await _sut.GetQueueOverviewAsync(null, null, null, CancellationToken.None);

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetQueueOverviewAsync_WithAvailablePrinter_ReturnsOverview()
    {
        // Arrange
        Printer printer = new PrinterBuilder()
            .WithName("Test Printer")
            .AsOnlineAndReady()
            .Build();

        _mockDataService.Setup(x => x.GetAvailablePrintersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Printer> { printer });
        _mockDataService.Setup(x => x.GetPrintJobsForPrintersAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PrintJob>());

        // Act
        IReadOnlyList<QueueOverviewDto> result = await _sut.GetQueueOverviewAsync(null, null, null, CancellationToken.None);

        // Assert
        result.Should().HaveCount(1);
        result.First().PrinterId.Should().Be(printer.Id);
        result.First().PrinterName.Should().Be("Test Printer");
        result.First().IsAvailable.Should().BeTrue();
        result.First().QueuedJobsCount.Should().Be(0);
    }

    [Fact]
    public async Task GetQueueOverviewAsync_WithQueuedJobs_CalculatesEstimatedCompletion()
    {
        // Arrange
        Printer printer = new PrinterBuilder().AsOnlineAndReady().Build();
        PrintJob queuedJob = new PrintJobBuilder()
            .WithAssignedPrinterId(printer.Id)
            .WithEstimatedPrintTime(TimeSpan.FromHours(2))
            .AsQueued()
            .Build();

        _mockDataService.Setup(x => x.GetAvailablePrintersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Printer> { printer });
        _mockDataService.Setup(x => x.GetPrintJobsForPrintersAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PrintJob> { queuedJob });

        // Act
        IReadOnlyList<QueueOverviewDto> result = await _sut.GetQueueOverviewAsync(null, null, null, CancellationToken.None);

        // Assert
        result.Should().HaveCount(1);
        result.First().QueuedJobsCount.Should().Be(1);
        result.First().EstimatedCompletionTime.Should().NotBeNull();
        result.First().EstimatedCompletionTime.Should().BeCloseTo(DateTime.UtcNow.AddHours(2), TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task GetQueueOverviewAsync_WithCurrentJobAndQueuedJobs_UsesElapsedTimeForEstimate()
    {
        // Arrange
        Printer printer = new PrinterBuilder().AsOnlineAndReady().Build();
        var currentJob = new PrintJob
        {
            Id = Guid.NewGuid(),
            AssignedPrinterId = printer.Id,
            EstimatedPrintTime = TimeSpan.FromHours(2),
            ActualStartTime = DateTime.UtcNow.AddMinutes(-60),
            Status = PrintJobStatus.Printing
        };
        var queuedJob = new PrintJob
        {
            Id = Guid.NewGuid(),
            AssignedPrinterId = printer.Id,
            EstimatedPrintTime = TimeSpan.FromMinutes(30),
            Status = PrintJobStatus.Queued
        };

        _mockDataService.Setup(x => x.GetAvailablePrintersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Printer> { printer });
        _mockDataService.Setup(x => x.GetPrintJobsForPrintersAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PrintJob> { currentJob, queuedJob });

        // Act
        IReadOnlyList<QueueOverviewDto> result = await _sut.GetQueueOverviewAsync(null, null, null, CancellationToken.None);

        // Assert
        result.Should().HaveCount(1);
        DateTime? estimate = result.First().EstimatedCompletionTime;
        estimate.Should().NotBeNull();
        estimate.Should().BeCloseTo(DateTime.UtcNow.AddMinutes(90), TimeSpan.FromMinutes(2));
    }

    #endregion

    #region GetQueueOverviewAsync Filtering Tests

    [Fact]
    public async Task GetQueueOverviewAsync_WithNozzleFilter_ReturnsOnlyMatchingPrinters()
    {
        // Arrange - Create printer with 0.4mm nozzle
        var nozzleModel = new NozzleModelDefinition { Id = Guid.NewGuid(), Name = "Brass 0.4", Diameter = 0.4 };
        var toolhead = new Toolhead
        {
            Id = Guid.NewGuid(),
            Name = "Primary",
            IsPrimary = true,
            NozzleModelId = nozzleModel.Id,
            NozzleModel = nozzleModel,
            SupportedMaterials = new[] { "PLA", "PETG" }
        };
        Printer printerWith04 = new PrinterBuilder()
            .WithName("Printer 0.4mm")
            .AsOnlineAndReady()
            .Build();
        printerWith04.Toolheads = new List<Toolhead> { toolhead };

        // Create printer with 0.6mm nozzle
        var nozzle06 = new NozzleModelDefinition { Id = Guid.NewGuid(), Name = "Brass 0.6", Diameter = 0.6 };
        var toolhead06 = new Toolhead
        {
            Id = Guid.NewGuid(),
            Name = "Primary",
            IsPrimary = true,
            NozzleModelId = nozzle06.Id,
            NozzleModel = nozzle06,
            SupportedMaterials = new[] { "PLA", "PETG" }
        };
        Printer printerWith06 = new PrinterBuilder()
            .WithName("Printer 0.6mm")
            .AsOnlineAndReady()
            .Build();
        printerWith06.Toolheads = new List<Toolhead> { toolhead06 };

        _mockDataService.Setup(x => x.GetAvailablePrintersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Printer> { printerWith04, printerWith06 });
        _mockDataService.Setup(x => x.GetPrintJobsForPrintersAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PrintJob>());

        // Act - Filter by 0.4mm nozzle
        IReadOnlyList<QueueOverviewDto> result = await _sut.GetQueueOverviewAsync(null, 0.4m, null, CancellationToken.None);

        // Assert - Only 0.4mm printer returned
        result.Should().HaveCount(1);
        result.First().PrinterName.Should().Be("Printer 0.4mm");
        result.First().NozzleDiameter.Should().BeApproximately(0.4, 0.01);
    }

    [Fact]
    public async Task GetQueueOverviewAsync_WithNozzleFilter_ExcludesPrintersWithoutNozzleConfigured()
    {
        // Arrange - Printer with no nozzle configured (NozzleModel is null)
        var toolheadNoNozzle = new Toolhead
        {
            Id = Guid.NewGuid(),
            Name = "Primary",
            IsPrimary = true,
            NozzleModel = null, // No nozzle configured
            SupportedMaterials = new[] { "PLA" }
        };
        Printer printerNoNozzle = new PrinterBuilder()
            .WithName("Printer No Nozzle")
            .AsOnlineAndReady()
            .Build();
        printerNoNozzle.Toolheads = new List<Toolhead> { toolheadNoNozzle };

        _mockDataService.Setup(x => x.GetAvailablePrintersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Printer> { printerNoNozzle });
        _mockDataService.Setup(x => x.GetPrintJobsForPrintersAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PrintJob>());

        // Act - Filter by 0.4mm nozzle
        IReadOnlyList<QueueOverviewDto> result = await _sut.GetQueueOverviewAsync(null, 0.4m, null, CancellationToken.None);

        // Assert - Printer without nozzle configured is excluded
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetQueueOverviewAsync_WithNozzleFilter_UsesToleranceForMatching()
    {
        // Arrange - Printer with 0.401mm nozzle (within 0.01mm tolerance)
        var nozzleModel = new NozzleModelDefinition { Id = Guid.NewGuid(), Name = "Brass 0.4", Diameter = 0.401 };
        var toolhead = new Toolhead
        {
            Id = Guid.NewGuid(),
            Name = "Primary",
            IsPrimary = true,
            NozzleModel = nozzleModel,
            SupportedMaterials = new[] { "PLA" }
        };
        Printer printer = new PrinterBuilder()
            .WithName("Printer Within Tolerance")
            .AsOnlineAndReady()
            .Build();
        printer.Toolheads = new List<Toolhead> { toolhead };

        _mockDataService.Setup(x => x.GetAvailablePrintersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Printer> { printer });
        _mockDataService.Setup(x => x.GetPrintJobsForPrintersAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PrintJob>());

        // Act - Filter by exactly 0.4mm
        IReadOnlyList<QueueOverviewDto> result = await _sut.GetQueueOverviewAsync(null, 0.4m, null, CancellationToken.None);

        // Assert - Printer matches due to tolerance
        result.Should().HaveCount(1);
        result.First().PrinterName.Should().Be("Printer Within Tolerance");
    }

    [Fact]
    public async Task GetQueueOverviewAsync_WithMaterialFilter_ReturnsOnlyMatchingPrinters()
    {
        // Arrange - Printer that supports PCTG
        var toolheadPCTG = new Toolhead
        {
            Id = Guid.NewGuid(),
            Name = "Primary",
            IsPrimary = true,
            SupportedMaterials = new[] { "PLA", "PETG", "PCTG", "ABS" }
        };
        Printer printerWithPCTG = new PrinterBuilder()
            .WithName("PCTG Printer")
            .AsOnlineAndReady()
            .Build();
        printerWithPCTG.Toolheads = new List<Toolhead> { toolheadPCTG };

        // Printer that does NOT support PCTG
        var toolheadNoPCTG = new Toolhead
        {
            Id = Guid.NewGuid(),
            Name = "Primary",
            IsPrimary = true,
            SupportedMaterials = new[] { "PLA", "PETG" }
        };
        Printer printerWithoutPCTG = new PrinterBuilder()
            .WithName("Basic Printer")
            .AsOnlineAndReady()
            .Build();
        printerWithoutPCTG.Toolheads = new List<Toolhead> { toolheadNoPCTG };

        _mockDataService.Setup(x => x.GetAvailablePrintersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Printer> { printerWithPCTG, printerWithoutPCTG });
        _mockDataService.Setup(x => x.GetPrintJobsForPrintersAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PrintJob>());

        // Act - Filter by PCTG material
        IReadOnlyList<QueueOverviewDto> result = await _sut.GetQueueOverviewAsync(null, null, "PCTG", CancellationToken.None);

        // Assert - Only PCTG-capable printer returned
        result.Should().HaveCount(1);
        result.First().PrinterName.Should().Be("PCTG Printer");
        result.First().SupportedMaterials.Should().Contain("PCTG");
    }

    [Fact]
    public async Task GetQueueOverviewAsync_WithMaterialFilter_IsCaseInsensitive()
    {
        // Arrange - Printer with "PCTG" in supported materials
        var toolhead = new Toolhead
        {
            Id = Guid.NewGuid(),
            Name = "Primary",
            IsPrimary = true,
            SupportedMaterials = new[] { "PLA", "PCTG" }
        };
        Printer printer = new PrinterBuilder()
            .WithName("Test Printer")
            .AsOnlineAndReady()
            .Build();
        printer.Toolheads = new List<Toolhead> { toolhead };

        _mockDataService.Setup(x => x.GetAvailablePrintersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Printer> { printer });
        _mockDataService.Setup(x => x.GetPrintJobsForPrintersAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PrintJob>());

        // Act - Filter with lowercase "pctg"
        IReadOnlyList<QueueOverviewDto> result = await _sut.GetQueueOverviewAsync(null, null, "pctg", CancellationToken.None);

        // Assert - Matches despite case difference
        result.Should().HaveCount(1);
        result.First().PrinterName.Should().Be("Test Printer");
    }

    [Fact]
    public async Task GetQueueOverviewAsync_WithModelFilter_CallsGetCompatiblePrinters()
    {
        // Arrange
        var model = new PrinterModel { Id = Guid.NewGuid(), Name = "Qidi X-Plus 4" };
        Printer printer = new PrinterBuilder()
            .WithName("qp4-1")
            .WithModel(model)
            .AsOnlineAndReady()
            .Build();

        _mockDataService.Setup(x => x.GetCompatiblePrintersAsync("Qidi X-Plus 4", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Printer> { printer });
        _mockDataService.Setup(x => x.GetPrintJobsForPrintersAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PrintJob>());

        // Act - Filter by model name
        IReadOnlyList<QueueOverviewDto> result = await _sut.GetQueueOverviewAsync("Qidi X-Plus 4", null, null, CancellationToken.None);

        // Assert
        _mockDataService.Verify(x => x.GetCompatiblePrintersAsync("Qidi X-Plus 4", It.IsAny<CancellationToken>()), Times.Once);
        _mockDataService.Verify(x => x.GetAvailablePrintersAsync(It.IsAny<CancellationToken>()), Times.Never);
        result.Should().HaveCount(1);
        result.First().PrinterModel.Should().Be("Qidi X-Plus 4");
    }

    [Fact]
    public async Task GetQueueOverviewAsync_WithAllFilters_AppliesAllFiltersTogether()
    {
        // Arrange - Printer that matches all criteria: Qidi model, 0.4mm nozzle, PCTG material
        var model = new PrinterModel { Id = Guid.NewGuid(), Name = "QIDI X-Plus 4" };
        var nozzleModel = new NozzleModelDefinition { Id = Guid.NewGuid(), Name = "Brass 0.4", Diameter = 0.4 };
        var matchingToolhead = new Toolhead
        {
            Id = Guid.NewGuid(),
            Name = "Primary",
            IsPrimary = true,
            NozzleModel = nozzleModel,
            SupportedMaterials = new[] { "PLA", "PETG", "PCTG" }
        };
        Printer matchingPrinter = new PrinterBuilder()
            .WithName("qp4-1")
            .WithModel(model)
            .AsOnlineAndReady()
            .Build();
        matchingPrinter.Toolheads = new List<Toolhead> { matchingToolhead };

        // Printer with wrong nozzle
        var wrongNozzle = new NozzleModelDefinition { Id = Guid.NewGuid(), Name = "Brass 0.6", Diameter = 0.6 };
        var wrongNozzleToolhead = new Toolhead
        {
            Id = Guid.NewGuid(),
            Name = "Primary",
            IsPrimary = true,
            NozzleModel = wrongNozzle,
            SupportedMaterials = new[] { "PLA", "PETG", "PCTG" }
        };
        Printer wrongNozzlePrinter = new PrinterBuilder()
            .WithName("qp4-2")
            .WithModel(model)
            .AsOnlineAndReady()
            .Build();
        wrongNozzlePrinter.Toolheads = new List<Toolhead> { wrongNozzleToolhead };

        // Printer with wrong material
        var wrongMaterialToolhead = new Toolhead
        {
            Id = Guid.NewGuid(),
            Name = "Primary",
            IsPrimary = true,
            NozzleModel = nozzleModel,
            SupportedMaterials = new[] { "PLA", "PETG" } // No PCTG
        };
        Printer wrongMaterialPrinter = new PrinterBuilder()
            .WithName("qp4-3")
            .WithModel(model)
            .AsOnlineAndReady()
            .Build();
        wrongMaterialPrinter.Toolheads = new List<Toolhead> { wrongMaterialToolhead };

        // GetCompatiblePrintersAsync returns all Qidi printers (model filter)
        _mockDataService.Setup(x => x.GetCompatiblePrintersAsync("Qidi X-Plus 4", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Printer> { matchingPrinter, wrongNozzlePrinter, wrongMaterialPrinter });
        _mockDataService.Setup(x => x.GetPrintJobsForPrintersAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PrintJob>());

        // Act - Filter by all three: model, nozzle, material
        IReadOnlyList<QueueOverviewDto> result = await _sut.GetQueueOverviewAsync("Qidi X-Plus 4", 0.4m, "PCTG", CancellationToken.None);

        // Assert - Only the printer matching ALL criteria is returned
        result.Should().HaveCount(1);
        result.First().PrinterName.Should().Be("qp4-1");
        result.First().NozzleDiameter.Should().BeApproximately(0.4, 0.01);
        result.First().SupportedMaterials.Should().Contain("PCTG");
    }

    [Fact]
    public async Task GetQueueOverviewAsync_WithNoMatchingPrinters_ReturnsEmptyList()
    {
        // Arrange - Printer that doesn't match the nozzle filter
        var nozzleModel = new NozzleModelDefinition { Id = Guid.NewGuid(), Name = "Brass 0.8", Diameter = 0.8 };
        var toolhead = new Toolhead
        {
            Id = Guid.NewGuid(),
            Name = "Primary",
            IsPrimary = true,
            NozzleModel = nozzleModel,
            SupportedMaterials = new[] { "PLA" }
        };
        Printer printer = new PrinterBuilder()
            .WithName("Test Printer")
            .AsOnlineAndReady()
            .Build();
        printer.Toolheads = new List<Toolhead> { toolhead };

        _mockDataService.Setup(x => x.GetAvailablePrintersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Printer> { printer });
        _mockDataService.Setup(x => x.GetPrintJobsForPrintersAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PrintJob>());

        // Act - Filter by 0.4mm nozzle (printer has 0.8mm)
        IReadOnlyList<QueueOverviewDto> result = await _sut.GetQueueOverviewAsync(null, 0.4m, null, CancellationToken.None);

        // Assert - No matching printers
        result.Should().BeEmpty();
    }

    #endregion

    #region GetPrinterQueueAsync Tests

    [Fact]
    public async Task GetPrinterQueueAsync_WithNoJobs_ReturnsEmptyList()
    {
        // Arrange
        var printerId = Guid.NewGuid();
        _mockDataService.Setup(x => x.GetPrintJobsForPrinterAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PrintJob>());

        // Act
        IReadOnlyList<JobQueuePrintJobDto> result = await _sut.GetPrinterQueueAsync(printerId, CancellationToken.None);

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetPrinterQueueAsync_WithQueuedJobs_ReturnsOrderedByQueuePosition()
    {
        // Arrange
        var printerId = Guid.NewGuid();
        PrintJob job1 = new PrintJobBuilder().WithQueuePosition(1).AsQueued().Build();
        PrintJob job2 = new PrintJobBuilder().WithQueuePosition(2).AsQueued().Build();

        _mockDataService.Setup(x => x.GetPrintJobsForPrinterAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PrintJob> { job2, job1 }); // Intentionally out of order

        // Act
        IReadOnlyList<JobQueuePrintJobDto> result = await _sut.GetPrinterQueueAsync(printerId, CancellationToken.None);

        // Assert
        result.Should().HaveCount(2);
        result.First().QueuePosition.Should().Be(1);
        result.Last().QueuePosition.Should().Be(2);
    }

    [Fact]
    public async Task GetPrinterQueueAsync_SetsQueuePositionForQueuedAndAssignedJobs()
    {
        // Arrange
        var printerId = Guid.NewGuid();
        PrintJob queuedJob = new PrintJobBuilder().AsQueued().Build();
        PrintJob assignedJob = new PrintJobBuilder().AsAssigned().Build();
        PrintJob printingJob = new PrintJobBuilder().AsPrinting().Build();

        _mockDataService.Setup(x => x.GetPrintJobsForPrinterAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PrintJob> { queuedJob, assignedJob, printingJob });

        // Act
        IReadOnlyList<JobQueuePrintJobDto> result = await _sut.GetPrinterQueueAsync(printerId, CancellationToken.None);

        // Assert
        var queuedResults = result.Where(r => r.Status == PrintJobStatus.Queued || r.Status == PrintJobStatus.Assigned).ToList();
        queuedResults.Should().HaveCount(2);
        queuedResults[0].QueuePosition.Should().Be(1);
        queuedResults[1].QueuePosition.Should().Be(2);
    }

    #endregion

    #region AddJobToQueueAsync Tests

    [Fact]
    public async Task AddJobToQueueAsync_WithNullRequest_ThrowsArgumentNullException()
    {
        // Act & Assert
        Func<Task> act = async () => await _sut.AddJobToQueueAsync(null!, null, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task AddJobToQueueAsync_WithNonexistentGcodeFile_ReturnsNull()
    {
        // Arrange
        var request = new QueuePrintJobDto
        {
            GcodeFileId = Guid.NewGuid(),
            Priority = 0
        };

        _mockDataService.Setup(x => x.GetGcodeFileAsync(request.GcodeFileId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((GcodeFile?)null);

        // Act
        JobQueuePrintJobDto? result = await _sut.AddJobToQueueAsync(request, null, CancellationToken.None);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task AddJobToQueueAsync_WithNoAvailablePrinters_ReturnsNull()
    {
        // Arrange
        var gcodeFile = new GcodeFile { Id = Guid.NewGuid(), FileName = "test.gcode" };
        var request = new QueuePrintJobDto
        {
            GcodeFileId = gcodeFile.Id,
            AssignedPrinterId = null,
            Priority = 0
        };

        _mockDataService.Setup(x => x.GetGcodeFileAsync(request.GcodeFileId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(gcodeFile);
        _mockDataService.Setup(x => x.GetAvailablePrintersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Printer>());

        // Act
        JobQueuePrintJobDto? result = await _sut.AddJobToQueueAsync(request, null, CancellationToken.None);

        // Assert - no compatible printer → null returned, job NOT created
        result.Should().BeNull();
        _mockRepo.Verify(x => x.AddAsync(It.IsAny<PrintJob>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AddJobToQueueAsync_WithValidRequest_CreatesJobAndReturnsDto()
    {
        // Arrange
        var printerId = Guid.NewGuid();
        var gcodeFile = new GcodeFile
        {
            Id = Guid.NewGuid(),
            Name = "test.gcode",
            FileName = "test.gcode",
            EstimatedPrintTimeMinutes = 120,
            EstimatedFilamentWeightG = 25.5
        };
        Printer printer = new PrinterBuilder().WithId(printerId).AsOnlineAndReady().Build();
        var request = new QueuePrintJobDto
        {
            GcodeFileId = gcodeFile.Id,
            AssignedPrinterId = printerId,
            Priority = (PrintJobPriority)5
        };

        _mockDataService.Setup(x => x.GetGcodeFileAsync(request.GcodeFileId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(gcodeFile);
        _mockDataService.Setup(x => x.GetNextQueuePositionAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);
        _mockDataService.Setup(x => x.GetAvailablePrintersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Printer> { printer });
        _mockRepo.Setup(x => x.AddAsync(It.IsAny<PrintJob>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockRepo.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        JobQueuePrintJobDto? result = await _sut.AddJobToQueueAsync(request, null, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result!.GcodeFileId.Should().Be(gcodeFile.Id);
        result.GcodeFileName.Should().Be("test.gcode");
        result.AssignedPrinterId.Should().Be(printerId);
        result.Status.Should().Be(PrintJobStatus.Queued);
        result.Priority.Should().Be(5);
        result.QueuePosition.Should().Be(1);
        result.EstimatedPrintTime.Should().Be(TimeSpan.FromMinutes(120));
        result.EstimatedFilamentUsage.Should().Be(25.5);

        _mockRepo.Verify(x => x.AddAsync(It.Is<PrintJob>(j =>
            j.GcodeFileId == gcodeFile.Id &&
            j.AssignedPrinterId == printerId &&
            j.Status == PrintJobStatus.Queued &&
            j.Priority == 5
        ), It.IsAny<CancellationToken>()), Times.Once);
        _mockRepo.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AddJobToQueueAsync_WithDeadline_MapsDeadlineToCreatedJobAndDto()
    {
        // Arrange
        var printerId = Guid.NewGuid();
        DateTime deadline = DateTime.UtcNow.AddHours(6);
        var gcodeFile = new GcodeFile
        {
            Id = Guid.NewGuid(),
            Name = "deadline-test.gcode",
            FileName = "deadline-test.gcode"
        };
        Printer printer = new PrinterBuilder().WithId(printerId).AsOnlineAndReady().Build();
        var request = new QueuePrintJobDto
        {
            GcodeFileId = gcodeFile.Id,
            AssignedPrinterId = printerId,
            Priority = PrintJobPriority.Normal,
            DeadlineAtUtc = deadline
        };

        _mockDataService.Setup(x => x.GetGcodeFileAsync(request.GcodeFileId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(gcodeFile);
        _mockDataService.Setup(x => x.GetNextQueuePositionAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);
        _mockDataService.Setup(x => x.GetAvailablePrintersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Printer> { printer });

        // Act
        JobQueuePrintJobDto? result = await _sut.AddJobToQueueAsync(request, null, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result!.DeadlineAtUtc.Should().BeCloseTo(deadline, TimeSpan.FromSeconds(1));
        _mockRepo.Verify(x => x.AddAsync(
            It.Is<PrintJob>(j => j.DeadlineAtUtc.HasValue && j.DeadlineAtUtc.Value == deadline),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task AddJobToQueueAsync_WithHighPriority_AssignsHigherPriority()
    {
        // Arrange
        var printerId = Guid.NewGuid();
        var gcodeFile = new GcodeFile { Id = Guid.NewGuid(), FileName = "urgent.gcode" };
        Printer printer = new PrinterBuilder().WithId(printerId).AsOnlineAndReady().Build();
        var request = new QueuePrintJobDto
        {
            GcodeFileId = gcodeFile.Id,
            AssignedPrinterId = printerId,
            Priority = (PrintJobPriority)10 // High priority
        };

        _mockDataService.Setup(x => x.GetGcodeFileAsync(request.GcodeFileId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(gcodeFile);
        _mockDataService.Setup(x => x.GetNextQueuePositionAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);
        _mockDataService.Setup(x => x.GetAvailablePrintersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Printer> { printer });

        // Act
        JobQueuePrintJobDto? result = await _sut.AddJobToQueueAsync(request, null, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result!.Priority.Should().Be(10);
    }

    #endregion

    #region AddJobToQueueAsync ACL Tests

    [Fact]
    public async Task AddJobToQueueAsync_UserWithGroupAccess_QueuesJob()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var groupId = Guid.NewGuid();
        var printerId = Guid.NewGuid();
        var gcodeFile = new GcodeFile
        {
            Id = Guid.NewGuid(),
            FileName = "test.gcode",
            PrinterGroupId = groupId
        };
        Printer printer = new PrinterBuilder().WithId(printerId).AsOnlineAndReady().Build();
        var request = new QueuePrintJobDto
        {
            GcodeFileId = gcodeFile.Id,
            AssignedPrinterId = printerId,
            Priority = 0
        };

        var mockGroupService = new Mock<IPrinterGroupService>();
        mockGroupService
            .Setup(x => x.CanUserSubmitToGroupAsync(groupId, userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var sut = new JobQueueService(
            _mockRepo.Object, _mockDataService.Object, _mockLogger.Object,
            printerGroupService: mockGroupService.Object);

        _mockDataService.Setup(x => x.GetGcodeFileAsync(request.GcodeFileId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(gcodeFile);
        _mockDataService.Setup(x => x.GetNextQueuePositionAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);
        _mockDataService.Setup(x => x.GetAvailablePrintersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Printer> { printer });

        // Act
        JobQueuePrintJobDto? result = await sut.AddJobToQueueAsync(request, userId, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        mockGroupService.Verify(x => x.CanUserSubmitToGroupAsync(groupId, userId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AddJobToQueueAsync_UserWithoutGroupAccess_ThrowsAccessDenied()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var groupId = Guid.NewGuid();
        var gcodeFile = new GcodeFile
        {
            Id = Guid.NewGuid(),
            FileName = "test.gcode",
            PrinterGroupId = groupId
        };
        var request = new QueuePrintJobDto
        {
            GcodeFileId = gcodeFile.Id,
            Priority = 0
        };

        var mockGroupService = new Mock<IPrinterGroupService>();
        mockGroupService
            .Setup(x => x.CanUserSubmitToGroupAsync(groupId, userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var sut = new JobQueueService(
            _mockRepo.Object, _mockDataService.Object, _mockLogger.Object,
            printerGroupService: mockGroupService.Object);

        _mockDataService.Setup(x => x.GetGcodeFileAsync(request.GcodeFileId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(gcodeFile);

        // Act
        Func<Task> act = async () => await sut.AddJobToQueueAsync(request, userId, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<QueueGroupAccessDeniedException>();
        _mockRepo.Verify(x => x.AddAsync(It.IsAny<PrintJob>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AddJobToQueueAsync_NullUserId_SkipsAclCheck()
    {
        // Arrange — system/API-key caller (userId = null) should bypass ACL
        var groupId = Guid.NewGuid();
        var printerId = Guid.NewGuid();
        var gcodeFile = new GcodeFile
        {
            Id = Guid.NewGuid(),
            FileName = "test.gcode",
            PrinterGroupId = groupId
        };
        Printer printer = new PrinterBuilder().WithId(printerId).AsOnlineAndReady().Build();
        var request = new QueuePrintJobDto
        {
            GcodeFileId = gcodeFile.Id,
            AssignedPrinterId = printerId,
            Priority = 0
        };

        var mockGroupService = new Mock<IPrinterGroupService>();

        var sut = new JobQueueService(
            _mockRepo.Object, _mockDataService.Object, _mockLogger.Object,
            printerGroupService: mockGroupService.Object);

        _mockDataService.Setup(x => x.GetGcodeFileAsync(request.GcodeFileId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(gcodeFile);
        _mockDataService.Setup(x => x.GetNextQueuePositionAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);
        _mockDataService.Setup(x => x.GetAvailablePrintersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Printer> { printer });

        // Act
        JobQueuePrintJobDto? result = await sut.AddJobToQueueAsync(request, null, CancellationToken.None);

        // Assert — job queued, ACL never consulted
        result.Should().NotBeNull();
        mockGroupService.Verify(x => x.CanUserSubmitToGroupAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AddJobToQueueAsync_NoGroupOnFile_SkipsAclCheck()
    {
        // Arrange — GcodeFile with no PrinterGroupId → open to all
        var userId = Guid.NewGuid();
        var printerId = Guid.NewGuid();
        var gcodeFile = new GcodeFile
        {
            Id = Guid.NewGuid(),
            FileName = "test.gcode",
            PrinterGroupId = null
        };
        Printer printer = new PrinterBuilder().WithId(printerId).AsOnlineAndReady().Build();
        var request = new QueuePrintJobDto
        {
            GcodeFileId = gcodeFile.Id,
            AssignedPrinterId = printerId,
            Priority = 0
        };

        var mockGroupService = new Mock<IPrinterGroupService>();

        var sut = new JobQueueService(
            _mockRepo.Object, _mockDataService.Object, _mockLogger.Object,
            printerGroupService: mockGroupService.Object);

        _mockDataService.Setup(x => x.GetGcodeFileAsync(request.GcodeFileId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(gcodeFile);
        _mockDataService.Setup(x => x.GetNextQueuePositionAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);
        _mockDataService.Setup(x => x.GetAvailablePrintersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Printer> { printer });

        // Act
        JobQueuePrintJobDto? result = await sut.AddJobToQueueAsync(request, userId, CancellationToken.None);

        // Assert — job queued, ACL never consulted
        result.Should().NotBeNull();
        mockGroupService.Verify(x => x.CanUserSubmitToGroupAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    #endregion

    #region GetJobAsync Tests

    [Fact]
    public async Task GetJobAsync_WithNonexistentId_ReturnsNull()
    {
        // Arrange
        var jobId = Guid.NewGuid();
        _mockDataService.Setup(x => x.GetPrintJobByIdAsync(jobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((PrintJob?)null);

        // Act
        JobQueuePrintJobDto? result = await _sut.GetJobAsync(jobId, CancellationToken.None);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetJobAsync_WithExistingJob_ReturnsDto()
    {
        // Arrange
        PrintJob job = new PrintJobBuilder()
            .WithName("Test Job")
            .AsQueued()
            .Build();

        _mockDataService.Setup(x => x.GetPrintJobByIdAsync(job.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(job);

        // Act
        JobQueuePrintJobDto? result = await _sut.GetJobAsync(job.Id, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result!.Id.Should().Be(job.Id);
        result.Status.Should().Be(PrintJobStatus.Queued);
    }

    #endregion

    #region RemoveJobAsync Tests

    [Fact]
    public async Task RemoveJobAsync_WithNonexistentJob_ReturnsFalse()
    {
        // Arrange
        var jobId = Guid.NewGuid();
        _mockDataService.Setup(x => x.GetPrintJobByIdAsync(jobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((PrintJob?)null);

        // Act
        bool result = await _sut.RemoveJobAsync(jobId, CancellationToken.None);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task RemoveJobAsync_WithPrintingJob_ReturnsFalse()
    {
        // Arrange
        PrintJob job = new PrintJobBuilder().AsPrinting().Build();
        _mockDataService.Setup(x => x.GetPrintJobByIdAsync(job.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(job);

        // Act
        bool result = await _sut.RemoveJobAsync(job.Id, CancellationToken.None);

        // Assert
        result.Should().BeFalse();
        _mockRepo.Verify(x => x.RemoveAsync(It.IsAny<PrintJob>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RemoveJobAsync_WithCompletedJob_ReturnsFalse()
    {
        // Arrange
        PrintJob job = new PrintJobBuilder().AsCompleted().Build();
        _mockDataService.Setup(x => x.GetPrintJobByIdAsync(job.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(job);

        // Act
        bool result = await _sut.RemoveJobAsync(job.Id, CancellationToken.None);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task RemoveJobAsync_WithQueuedJob_RemovesAndReturnsTrue()
    {
        // Arrange
        PrintJob job = new PrintJobBuilder().AsQueued().Build();
        _mockDataService.Setup(x => x.GetPrintJobByIdAsync(job.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(job);
        _mockRepo.Setup(x => x.RemoveAsync(job, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockRepo.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        bool result = await _sut.RemoveJobAsync(job.Id, CancellationToken.None);

        // Assert
        result.Should().BeTrue();
        _mockRepo.Verify(x => x.RemoveAsync(job, It.IsAny<CancellationToken>()), Times.Once);
        _mockRepo.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RemoveJobAsync_WithAssignedJob_RemovesAndReturnsTrue()
    {
        // Arrange
        PrintJob job = new PrintJobBuilder().AsAssigned().Build();
        _mockDataService.Setup(x => x.GetPrintJobByIdAsync(job.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(job);

        // Act
        bool result = await _sut.RemoveJobAsync(job.Id, CancellationToken.None);

        // Assert
        result.Should().BeTrue();
    }

    #endregion

    #region UpdateJobPriorityAsync Tests

    [Fact]
    public async Task UpdateJobPriorityAsync_WithNonexistentJob_ReturnsNull()
    {
        // Arrange
        var jobId = Guid.NewGuid();
        var request = new UpdateJobPriorityDto { Priority = 10 };
        _mockDataService.Setup(x => x.GetPrintJobByIdAsync(jobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((PrintJob?)null);

        // Act
        JobQueuePrintJobDto? result = await _sut.UpdateJobPriorityAsync(jobId, request, CancellationToken.None);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task UpdateJobPriorityAsync_WithValidRequest_UpdatesPriorityAndReturnsDto()
    {
        // Arrange
        PrintJob job = new PrintJobBuilder()
            .WithPriority(0)
            .AsQueued()
            .Build();
        var request = new UpdateJobPriorityDto { Priority = 10 };

        _mockDataService.Setup(x => x.GetPrintJobByIdAsync(job.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(job);
        _mockRepo.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        JobQueuePrintJobDto? result = await _sut.UpdateJobPriorityAsync(job.Id, request, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result!.Priority.Should().Be(10);
        job.Priority.Should().Be(10);
        job.UpdatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
        _mockRepo.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion

    #region UpdateJobAsync Tests

    [Fact]
    public async Task UpdateJobAsync_WithNullRequest_ThrowsArgumentNullException()
    {
        // Act & Assert
        Func<Task> act = async () => await _sut.UpdateJobAsync(Guid.NewGuid(), null!, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task UpdateJobAsync_WithNonexistentJob_ReturnsNull()
    {
        // Arrange
        var jobId = Guid.NewGuid();
        var request = new UpdatePrintJobStatusDto { Status = PrintJobStatus.Printing };
        _mockDataService.Setup(x => x.GetPrintJobByIdAsync(jobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((PrintJob?)null);

        // Act
        JobQueuePrintJobDto? result = await _sut.UpdateJobAsync(jobId, request, CancellationToken.None);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task UpdateJobAsync_WithStatusUpdate_UpdatesStatusAndReturnsDto()
    {
        // Arrange
        PrintJob job = new PrintJobBuilder().AsQueued().Build();
        var request = new UpdatePrintJobStatusDto { Status = PrintJobStatus.Printing };

        _mockDataService.Setup(x => x.GetPrintJobByIdAsync(job.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(job);

        // Act
        JobQueuePrintJobDto? result = await _sut.UpdateJobAsync(job.Id, request, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result!.Status.Should().Be(PrintJobStatus.Printing);
        job.Status.Should().Be(PrintJobStatus.Printing);
    }

    [Fact]
    public async Task UpdateJobAsync_WithPriorityUpdate_UpdatesPriority()
    {
        // Arrange
        PrintJob job = new PrintJobBuilder().WithPriority(0).AsQueued().Build();
        var request = new UpdatePrintJobStatusDto { Priority = (PrintJobPriority)10 };

        _mockDataService.Setup(x => x.GetPrintJobByIdAsync(job.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(job);

        // Act
        JobQueuePrintJobDto? result = await _sut.UpdateJobAsync(job.Id, request, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result!.Priority.Should().Be(10);
        job.Priority.Should().Be(10);
    }

    [Fact]
    public async Task UpdateJobAsync_WithInvalidPrinterAssignment_ReturnsNull()
    {
        // Arrange
        PrintJob job = new PrintJobBuilder().AsQueued().Build();
        var invalidPrinterId = Guid.NewGuid();
        var request = new UpdatePrintJobStatusDto { AssignedPrinterId = invalidPrinterId };

        _mockDataService.Setup(x => x.GetPrintJobByIdAsync(job.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(job);
        _mockDataService.Setup(x => x.GetAvailablePrintersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Printer>()); // No printers available

        // Act
        JobQueuePrintJobDto? result = await _sut.UpdateJobAsync(job.Id, request, CancellationToken.None);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task UpdateJobAsync_WithValidPrinterAssignment_UpdatesPrinterAndReloadsJob()
    {
        // Arrange
        var newPrinterId = Guid.NewGuid();
        Printer printer = new PrinterBuilder().WithId(newPrinterId).Build();
        PrintJob job = new PrintJobBuilder().AsQueued().Build();
        var request = new UpdatePrintJobStatusDto { AssignedPrinterId = newPrinterId };

        _mockDataService.SetupSequence(x => x.GetPrintJobByIdAsync(job.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(job) // First call
            .ReturnsAsync(job); // Second call after reload
        _mockDataService.Setup(x => x.GetAvailablePrintersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Printer> { printer });

        // Act
        JobQueuePrintJobDto? result = await _sut.UpdateJobAsync(job.Id, request, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        job.AssignedPrinterId.Should().Be(newPrinterId);
        _mockDataService.Verify(x => x.GetPrintJobByIdAsync(job.Id, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task UpdateJobAsync_WithFailureReason_SetsFailureReason()
    {
        // Arrange
        PrintJob job = new PrintJobBuilder().AsQueued().Build();
        var request = new UpdatePrintJobStatusDto
        {
            Status = PrintJobStatus.Failed,
            FailureReason = "Printer communication lost"
        };

        _mockDataService.Setup(x => x.GetPrintJobByIdAsync(job.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(job);

        // Act
        JobQueuePrintJobDto? result = await _sut.UpdateJobAsync(job.Id, request, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result!.Status.Should().Be(PrintJobStatus.Failed);
        result.FailureReason.Should().Be("Printer communication lost");
        job.FailureReason.Should().Be("Printer communication lost");
    }

    [Fact]
    public async Task UpdateJobAsync_WithActualFilamentUsage_UpdatesFilamentUsage()
    {
        // Arrange
        PrintJob job = new PrintJobBuilder().AsPrinting().Build();
        var request = new UpdatePrintJobStatusDto
        {
            Status = PrintJobStatus.Completed,
            ActualFilamentUsage = 28.3
        };

        _mockDataService.Setup(x => x.GetPrintJobByIdAsync(job.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(job);

        // Act
        JobQueuePrintJobDto? result = await _sut.UpdateJobAsync(job.Id, request, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result!.ActualFilamentUsage.Should().Be(28.3);
        job.ActualFilamentUsage.Should().Be(28.3);
    }

    [Fact]
    public async Task UpdateJobAsync_WithDeadline_UpdatesDeadlineAndReturnsDto()
    {
        // Arrange
        PrintJob job = new PrintJobBuilder().AsQueued().Build();
        DateTime deadline = DateTime.UtcNow.AddDays(2);
        var request = new UpdatePrintJobStatusDto
        {
            DeadlineAtUtc = deadline
        };

        _mockDataService.Setup(x => x.GetPrintJobByIdAsync(job.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(job);

        // Act
        JobQueuePrintJobDto? result = await _sut.UpdateJobAsync(job.Id, request, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result!.DeadlineAtUtc.Should().BeCloseTo(deadline, TimeSpan.FromSeconds(1));
        job.DeadlineAtUtc.Should().BeCloseTo(deadline, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task UpdateJobAsync_AlwaysUpdatesUpdatedAtTimestamp()
    {
        // Arrange
        PrintJob job = new PrintJobBuilder().AsQueued().Build();
        DateTime oldUpdatedAt = job.UpdatedAt;
        var request = new UpdatePrintJobStatusDto { Priority = (PrintJobPriority)5 };

        _mockDataService.Setup(x => x.GetPrintJobByIdAsync(job.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(job);

        // Act - Wait a moment to ensure timestamp difference
        await Task.Delay(10);
        JobQueuePrintJobDto? result = await _sut.UpdateJobAsync(job.Id, request, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        job.UpdatedAt.Should().BeAfter(oldUpdatedAt);
    }

    #endregion
}
