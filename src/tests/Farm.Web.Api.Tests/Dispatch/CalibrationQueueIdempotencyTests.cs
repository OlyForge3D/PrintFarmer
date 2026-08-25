using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.PrinterCalibration;
using Farm.Infrastructure.Repositories.Queue;
using Farm.Infrastructure.Services.Queue;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Dispatch;

/// <summary>
/// Tests for calibration queue idempotency, concurrency, and outbox deduplication.
/// </summary>
public class CalibrationQueueIdempotencyTests
{
    private static readonly Guid TestUserId = Guid.NewGuid();

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AddJobToQueueAsync_CalibrationReplay_FailsClosedWhenSnapshotCannotBeResolved()
    {
        await using AppDbContext db = CreateDbContext();
        Mock<IQueueDataService> dataService = CreateQueueDataService(db);
        JobQueueService sut = CreateSut(db, dataService.Object);

        Printer printer = await db.Printers.SingleAsync();
        GcodeFile gcode = await db.GcodeFiles.SingleAsync();
        QueuePrintJobDto request = CreateCalibrationRequest(gcode.Id, printer.Id, "key-1");

        Func<Task> act = async () => await sut.AddJobToQueueAsync(request, TestUserId, CancellationToken.None);

        await act.Should().ThrowAsync<CalibrationQueueResourceNotFoundException>()
            .WithMessage("*authoritative printer configuration snapshot was not found*");
        (await db.PrintJobs.CountAsync()).Should().Be(0);
        (await db.QueueDispatchOutbox.CountAsync()).Should().Be(0);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AddJobToQueueAsync_CalibrationReplayWithDifferentPayload_FailsClosedBeforeAnyJobIsCreated()
    {
        await using AppDbContext db = CreateDbContext();
        Mock<IQueueDataService> dataService = CreateQueueDataService(db);
        JobQueueService sut = CreateSut(db, dataService.Object);

        Printer printer = await db.Printers.SingleAsync();
        GcodeFile gcode = await db.GcodeFiles.SingleAsync();
        QueuePrintJobDto firstRequest = CreateCalibrationRequest(gcode.Id, printer.Id, "key-2");
        QueuePrintJobDto conflictingRequest = CreateCalibrationRequest(gcode.Id, printer.Id, "key-2");

        // Provenance/hashes are server-derived (defect 3), so the payload difference must be
        // on an input the client still controls and that changes the physical outcome.
        conflictingRequest.Priority = PrintJobPriority.Urgent;

        Func<Task> first = async () => await sut.AddJobToQueueAsync(firstRequest, TestUserId, CancellationToken.None);
        Func<Task> conflicting = async () => await sut.AddJobToQueueAsync(conflictingRequest, TestUserId, CancellationToken.None);

        await first.Should().ThrowAsync<CalibrationQueueResourceNotFoundException>()
            .WithMessage("*authoritative printer configuration snapshot was not found*");
        await conflicting.Should().ThrowAsync<CalibrationQueueResourceNotFoundException>()
            .WithMessage("*authoritative printer configuration snapshot was not found*");
        (await db.PrintJobs.CountAsync()).Should().Be(0);
        (await db.QueueDispatchOutbox.CountAsync()).Should().Be(0);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AddJobToQueueAsync_CalibrationWithoutDatabaseContext_ThrowsInvalidOperationException()
    {
        await using AppDbContext db = CreateDbContext();
        Mock<IQueueDataService> dataService = CreateQueueDataService(db);
        JobQueueService sut = new(
            Mock.Of<IQueueRepository>(),
            dataService.Object,
            Mock.Of<ILogger<JobQueueService>>());

        Printer printer = await db.Printers.SingleAsync();
        GcodeFile gcode = await db.GcodeFiles.SingleAsync();
        QueuePrintJobDto request = CreateCalibrationRequest(gcode.Id, printer.Id, "key-3");

        Func<Task> act = async () => await sut.AddJobToQueueAsync(request, Guid.NewGuid(), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*database context*");
    }

    private static JobQueueService CreateSut(AppDbContext db, IQueueDataService dataService) =>
        new(
            new EfQueueRepository(db),
            dataService,
            Mock.Of<ILogger<JobQueueService>>(),
            db: db);

    private static Mock<IQueueDataService> CreateQueueDataService(AppDbContext db)
    {
        Guid printerId = Guid.NewGuid();
        Guid projectId = Guid.NewGuid();
        Guid attemptId = Guid.NewGuid();
        Guid orchestrationId = Guid.NewGuid();
        Guid sourceArtifactId = Guid.NewGuid();
        Guid sliceJobId = Guid.NewGuid();
        Guid modelId = Guid.NewGuid();
        Guid toolheadId = Guid.NewGuid();
        Guid spoolId = Guid.NewGuid();
        GcodeFile gcode = new()
        {
            Id = Guid.NewGuid(),
            Name = "calibration.gcode",
            FileName = "calibration.gcode",
            EstimatedPrintTimeMinutes = 45,
            EstimatedFilamentWeightG = 11.2,
            FileSizeBytes = 1024,

            // Server-authoritative calibration lineage (issue #900, defect 3): the JobKind
            // and provenance are derived from THIS artifact, never from the client request.
            IsImmutable = true,
            PromotedAtUtc = DateTime.UtcNow.AddMinutes(-1),
            ContentSha256 = new string('1', 64),
            CalibrationProjectId = projectId,
            CalibrationAttemptId = attemptId,
            CalibrationOrchestrationId = orchestrationId,
            SourceArtifactId = sourceArtifactId,
            SourceSliceJobId = sliceJobId,
            SourceModelSha256 = new string('8', 64),
            CalibrationManifestSha256 = new string('9', 64),
            SpecificationSha256 = new string('2', 64),
            MachineProfileSha256 = new string('3', 64),
            ProcessProfileSha256 = new string('4', 64),
            FilamentProfileSha256 = new string('5', 64),
            SlicerEngineName = "OrcaSlicer",
            SlicerDistribution = "upstream",
            PinnedSlicerVersion = "2.3.0",
            SlicerContainerDigest = "sha256:test",
            FirmwareFamily = nameof(PrinterFirmwareFamily.Klipper),
            GcodeDialect = nameof(PrinterGcodeDialect.Klipper),
            PrinterModelId = modelId,
            ObjectDimensionX = 20,
            ObjectDimensionY = 20,
            ObjectDimensionZ = 20,
        };
        Printer printer = new()
        {
            Id = printerId,
            Name = "Calibration Printer",
            IsEnabled = true,
            InMaintenance = false,
            IsAvailable = true,
            ModelId = modelId,
            FirmwareFamily = PrinterFirmwareFamily.Klipper,
            GcodeDialect = PrinterGcodeDialect.Klipper,
            ConfigurationRevision = 7,
            MaxBuildVolumeX = 200,
            MaxBuildVolumeY = 200,
            MaxBuildVolumeZ = 200,
        };
        var toolhead = new Toolhead
        {
            Id = toolheadId,
            PrinterId = printerId,
            Name = "Primary",
            Index = 0,
            IsPrimary = true,
            NozzleDiameter = 0.4,
        };
        printer.Toolheads.Add(toolhead);
        var spool = new Spool
        {
            Id = spoolId,
            Material = "PLA",
            Sku = "PLA-TEST-SKU",
            LotNumber = "LOT-TEST",
            WeightGrams = 1000,
            InUse = true,
            AssignedPrinterId = printerId,
        };
        var project = new CalibrationProject
        {
            Id = projectId,
            OwnerUserId = TestUserId,
            Name = "Project",
            PrinterId = printerId,
            SelectedToolheadId = toolheadId,
            SelectedToolheadIndex = 0,
            FilamentProvider = "local",
            FilamentProductId = "pla",
            FilamentProductName = "PLA",
            FilamentMaterial = "PLA",
            FilamentSku = "PLA-TEST-SKU",
            LocalSpoolId = spoolId,
            FilamentSnapshotJson = """{"material":"PLA"}""",
        };
        var attempt = new CalibrationAttempt
        {
            Id = attemptId,
            ProjectId = projectId,
            SpecificationSha256 = new string('2', 64),
        };
        var orchestration = new CalibrationOrchestration
        {
            Id = orchestrationId,
            ProjectId = projectId,
            AttemptId = attemptId,
            SpecificationSha256 = new string('2', 64),
            SliceJobId = sliceJobId,
            GcodeFileId = gcode.Id,
        };

        db.GcodeFiles.Add(gcode);
        db.Printers.Add(printer);
        db.PrinterDispatchStates.Add(new PrinterDispatchState { PrinterId = printerId });
        db.Spools.Add(spool);
        db.CalibrationProjects.Add(project);
        db.CalibrationAttempts.Add(attempt);
        db.CalibrationOrchestrations.Add(orchestration);
        db.SaveChanges();

        Mock<IQueueDataService> mock = new();
        mock.Setup(s => s.GetGcodeFileAsync(gcode.Id, It.IsAny<CancellationToken>())).ReturnsAsync(gcode);
        mock.Setup(s => s.GetNextQueuePositionAsync(printerId, It.IsAny<CancellationToken>())).ReturnsAsync(1);
        mock.Setup(s => s.GetAvailablePrintersAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<Printer> { printer });
        return mock;
    }

    private static QueuePrintJobDto CreateCalibrationRequest(Guid gcodeFileId, Guid printerId, string idempotencyKey) =>
        new()
        {
            GcodeFileId = gcodeFileId,
            AssignedPrinterId = printerId,
            JobKind = JobKind.FilamentCalibration,
            IdempotencyKey = idempotencyKey,
            Copies = 1,
            Priority = PrintJobPriority.High,
        };

    private static AppDbContext CreateDbContext()
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }
}
