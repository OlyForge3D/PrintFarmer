using System.ComponentModel.DataAnnotations;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
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
    [Fact]
    [Trait("Category", "Unit")]
    public async Task AddJobToQueueAsync_CalibrationReplay_ReturnsExistingJobAndDoesNotDuplicateOutbox()
    {
        await using AppDbContext db = CreateDbContext();
        Mock<IQueueDataService> dataService = CreateQueueDataService(db);
        JobQueueService sut = CreateSut(db, dataService.Object);

        Printer printer = await db.Printers.SingleAsync();
        GcodeFile gcode = await db.GcodeFiles.SingleAsync();
        QueuePrintJobDto request = CreateCalibrationRequest(gcode.Id, printer.Id, "key-1");

        JobQueuePrintJobDto? first = await sut.AddJobToQueueAsync(request, Guid.NewGuid(), CancellationToken.None);
        JobQueuePrintJobDto? replay = await sut.AddJobToQueueAsync(request, Guid.NewGuid(), CancellationToken.None);

        first.Should().NotBeNull();
        replay.Should().NotBeNull();
        replay!.Id.Should().Be(first!.Id);
        replay.IsIdempotentReplay.Should().BeTrue();

        (await db.PrintJobs.CountAsync()).Should().Be(1);
        (await db.QueueDispatchOutbox.CountAsync()).Should().Be(1);
        (await db.QueueDispatchOutbox.SingleAsync()).Status.Should().Be(QueueOutboxEventStatus.Pending);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AddJobToQueueAsync_CalibrationReplayWithDifferentPayload_ThrowsConflictAndKeepsSingleOutbox()
    {
        await using AppDbContext db = CreateDbContext();
        Mock<IQueueDataService> dataService = CreateQueueDataService(db);
        JobQueueService sut = CreateSut(db, dataService.Object);

        Printer printer = await db.Printers.SingleAsync();
        GcodeFile gcode = await db.GcodeFiles.SingleAsync();
        QueuePrintJobDto firstRequest = CreateCalibrationRequest(gcode.Id, printer.Id, "key-2");
        QueuePrintJobDto conflictingRequest = CreateCalibrationRequest(gcode.Id, printer.Id, "key-2");
        conflictingRequest.SpecificationSha256 = new string('9', 64);

        _ = await sut.AddJobToQueueAsync(firstRequest, Guid.NewGuid(), CancellationToken.None);

        Func<Task> act = async () => await sut.AddJobToQueueAsync(conflictingRequest, Guid.NewGuid(), CancellationToken.None);

        await act.Should().ThrowAsync<QueueJobIdempotencyConflictException>();
        (await db.PrintJobs.CountAsync()).Should().Be(1);
        (await db.QueueDispatchOutbox.CountAsync()).Should().Be(1);
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
        GcodeFile gcode = new()
        {
            Id = Guid.NewGuid(),
            Name = "calibration.gcode",
            FileName = "calibration.gcode",
            EstimatedPrintTimeMinutes = 45,
            EstimatedFilamentWeightG = 11.2,
        };
        Printer printer = new()
        {
            Id = printerId,
            Name = "Calibration Printer",
            IsEnabled = true,
            InMaintenance = false,
            IsAvailable = true,
        };

        db.GcodeFiles.Add(gcode);
        db.Printers.Add(printer);
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
            IdempotencyScope = "scope-fixed",
            CalibrationProjectId = Guid.NewGuid(),
            CalibrationAttemptId = Guid.NewGuid(),
            CalibrationConfigSnapshotId = Guid.NewGuid(),
            CalibrationOrchestrationId = Guid.NewGuid(),
            SourceArtifactId = Guid.NewGuid(),
            GcodeContentSha256 = new string('1', 64),
            RequiredFirmwareFamily = PrinterFirmwareFamily.Klipper,
            RequiredGcodeDialect = PrinterGcodeDialect.Klipper,
            RequiredSlicerEngine = "OrcaSlicer",
            RequiredSlicerDistribution = "upstream",
            RequiredSlicerVersion = "2.3.0",
            RequiredSlicerContainerDigest = "sha256:test",
            SpecificationSha256 = new string('2', 64),
            MachineProfileSha256 = new string('3', 64),
            ProcessProfileSha256 = new string('4', 64),
            FilamentProfileSha256 = new string('5', 64),
            PrinterConfigSnapshotSha256 = new string('6', 64),
            PinnedPrinterConfigRevision = 7,
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
