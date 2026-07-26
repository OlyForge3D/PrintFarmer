using Farm.Api.Services.PrintQueue;
using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos.PrintQueue;
using Farm.Infrastructure.Repositories.Queue;
using Farm.Infrastructure.Services;
using Farm.Infrastructure.Services.Cameras;
using Farm.Infrastructure.Services.Cost;
using Farm.Infrastructure.Services.FileManagement;
using Farm.Infrastructure.Services.Interfaces;
using Farm.Infrastructure.Services.Notifications;
using Farm.Infrastructure.Services.PartsInventory;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.SignalR;
using Farm.Infrastructure.Services.StorageManagement;
using Farm.Infrastructure.Settings;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services;

public class PrintJobManagementServiceQueueMappingTests
{
    [Fact]
    public async Task GetAllQueuedJobsAsync_ExternalPrintWithoutGcodeFile_UsesJobNameAsFileName()
    {
        // Externally-started/history-seeded prints have no local G-code file. The
        // file-name cell must fall back to the job's real name (from print_stats /
        // history), not the literal "Unknown".
        PrintJob external = new()
        {
            Id = Guid.NewGuid(),
            Name = "snapmaker-u1-benchy",
            Status = PrintJobStatus.Printing,
            GcodeFile = null,
            IsExternalPrint = true,
            QueuedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        Mock<IPrintJobManagementRepository> repository = new();
        repository.Setup(r => r.GetFilteredJobsAsync(
                It.IsAny<PrintJobStatus?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<DateTime?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([external]);

        PrintJobManagementService service = CreateService(repository);

        List<Farm.Infrastructure.Dtos.PrintQueue.QueuedPrintJobWithFileMetaDto> result =
            await service.GetAllQueuedJobsAsync();

        Farm.Infrastructure.Dtos.PrintQueue.QueuedPrintJobWithFileMetaDto dto = Assert.Single(result);
        Assert.NotNull(dto.GcodeFile);
        Assert.Equal("snapmaker-u1-benchy", dto.GcodeFile.FileName);
    }

    [Fact]
    public async Task GetAllQueuedJobsAsync_ExternalPrintWithBlankName_FallsBackToUnknown()
    {
        PrintJob external = new()
        {
            Id = Guid.NewGuid(),
            Name = "   ",
            Status = PrintJobStatus.Printing,
            GcodeFile = null,
            IsExternalPrint = true,
            QueuedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        Mock<IPrintJobManagementRepository> repository = new();
        repository.Setup(r => r.GetFilteredJobsAsync(
                It.IsAny<PrintJobStatus?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<DateTime?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([external]);

        PrintJobManagementService service = CreateService(repository);

        List<Farm.Infrastructure.Dtos.PrintQueue.QueuedPrintJobWithFileMetaDto> result =
            await service.GetAllQueuedJobsAsync();

        Farm.Infrastructure.Dtos.PrintQueue.QueuedPrintJobWithFileMetaDto dto = Assert.Single(result);
        Assert.Equal("Unknown", dto.GcodeFile.FileName);
    }

    [Fact]
    public async Task GetAllQueuedJobsAsync_ActiveView_DoesNotApplyQueuedDateWindow()
    {
        // The active queue reflects current state; a job still waiting must appear regardless of
        // when it was queued. So the queue-date window must be dropped for the default/active view,
        // otherwise old active jobs are hidden while the stats count still includes them.
        DateTime from = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        DateTime to = new(2026, 1, 8, 0, 0, 0, DateTimeKind.Utc);

        DateTime? capturedFrom = to;
        DateTime? capturedTo = from;
        Mock<IPrintJobManagementRepository> repository = new();
        repository.Setup(r => r.GetFilteredJobsAsync(
                It.IsAny<PrintJobStatus?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<DateTime?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<CancellationToken>()))
            .Callback((PrintJobStatus? _, string? _, string? _, DateTime? _, DateTime? _, string _, int _, int _, DateTime? qFrom, DateTime? qTo, CancellationToken _) =>
            {
                capturedFrom = qFrom;
                capturedTo = qTo;
            })
            .ReturnsAsync([]);

        PrintJobManagementService service = CreateService(repository);

        await service.GetAllQueuedJobsAsync(queuedFrom: from, queuedTo: to);

        Assert.Null(capturedFrom);
        Assert.Null(capturedTo);
    }

    [Fact]
    public async Task GetAllQueuedJobsAsync_TerminalView_AppliesQueuedDateWindow()
    {
        // Terminal (History-style) views may legitimately be time-windowed, so the date range
        // must still flow through when a terminal status is explicitly requested.
        DateTime from = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        DateTime to = new(2026, 1, 8, 0, 0, 0, DateTimeKind.Utc);

        DateTime? capturedFrom = null;
        DateTime? capturedTo = null;
        Mock<IPrintJobManagementRepository> repository = new();
        repository.Setup(r => r.GetFilteredJobsAsync(
                It.IsAny<PrintJobStatus?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<DateTime?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<CancellationToken>()))
            .Callback((PrintJobStatus? _, string? _, string? _, DateTime? _, DateTime? _, string _, int _, int _, DateTime? qFrom, DateTime? qTo, CancellationToken _) =>
            {
                capturedFrom = qFrom;
                capturedTo = qTo;
            })
            .ReturnsAsync([]);

        PrintJobManagementService service = CreateService(repository);

        await service.GetAllQueuedJobsAsync(filterStatus: "Completed", queuedFrom: from, queuedTo: to);

        Assert.Equal(from, capturedFrom);
        Assert.Equal(to, capturedTo);
    }

    [Fact]
    public async Task EnqueueJobAsync_AssignedJob_CapturesSnapshotAndDispatchLogBeforeSave()
    {
        Guid printerId = Guid.NewGuid();
        var gcode = new GcodeFile
        {
            Id = Guid.NewGuid(),
            Name = "assigned.gcode",
            FileName = "assigned.gcode",
        };
        var repository = new Mock<IPrintJobManagementRepository>(MockBehavior.Strict);
        var snapshots = new Mock<IPartOutputSnapshotService>(MockBehavior.Strict);
        repository.Setup(value => value.GetGcodeFileAsync(gcode.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(gcode);
        repository.Setup(value => value.GetMaxQueuePositionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);
        repository.Setup(value => value.AddWithoutSaveAsync(
                It.Is<PrintJob>(job => job.AssignedPrinterId == printerId),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        snapshots.Setup(value => value.CaptureJobSnapshotIfAbsentAsync(
                It.Is<PrintJob>(job => job.DispatchedAt != null),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        repository.Setup(value => value.AddDispatchLog(
            It.Is<DispatchLog>(log =>
                log.PrinterId == printerId
                && log.Action == Farm.Infrastructure.Services.Queue.Dispatch.DispatchAction.Dispatched)));
        repository.Setup(value => value.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        PrintJobManagementService service = CreateService(repository, snapshots.Object);

        _ = await service.EnqueueJobAsync(
            new EnqueueQueueJobRequest
            {
                GcodeFileId = gcode.Id.ToString(),
                AssignedPrinterId = printerId.ToString(),
            },
            "operator");

        repository.VerifyAll();
        snapshots.VerifyAll();
    }

    [Fact]
    public async Task UpdateJobAsync_FirstAssignment_CapturesSnapshotBeforeRepositorySave()
    {
        Guid printerId = Guid.NewGuid();
        var job = new PrintJob
        {
            Id = Guid.NewGuid(),
            Name = "update.gcode",
            Status = PrintJobStatus.Queued,
        };
        var repository = new Mock<IPrintJobManagementRepository>(MockBehavior.Strict);
        var snapshots = new Mock<IPartOutputSnapshotService>(MockBehavior.Strict);
        repository.Setup(value => value.GetByIdAsync(job.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(job);
        snapshots.Setup(value => value.CaptureJobSnapshotIfAbsentAsync(
                job,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        repository.Setup(value => value.AddDispatchLog(
            It.Is<DispatchLog>(log => log.PrintJobId == job.Id && log.PrinterId == printerId)));
        repository.Setup(value => value.UpdateAsync(job, It.IsAny<CancellationToken>()))
            .ReturnsAsync(job);
        PrintJobManagementService service = CreateService(repository, snapshots.Object);

        _ = await service.UpdateJobAsync(
            job.Id.ToString(),
            new UpdateQueueJobRequest { AssignedPrinterId = printerId.ToString() },
            "operator");

        Assert.Equal(printerId, job.AssignedPrinterId);
        Assert.NotNull(job.DispatchedAt);
        snapshots.VerifyAll();
        repository.VerifyAll();
    }

    [Fact]
    public async Task DispatchJobAsync_FirstStartAttempt_CapturesSnapshotBeforeFileFailure()
    {
        Guid printerId = Guid.NewGuid();
        var job = new PrintJob
        {
            Id = Guid.NewGuid(),
            Name = "missing.gcode",
            Status = PrintJobStatus.Assigned,
            AssignedPrinterId = printerId,
            AssignedPrinter = new Printer { Id = printerId, Name = "printer" },
            GcodeFileId = Guid.NewGuid(),
            GcodeFile = new GcodeFile
            {
                Id = Guid.NewGuid(),
                Name = "missing.gcode",
                FileName = "missing.gcode",
                FilePath = "/missing",
            },
        };
        var repository = new Mock<IPrintJobManagementRepository>(MockBehavior.Strict);
        var snapshots = new Mock<IPartOutputSnapshotService>(MockBehavior.Strict);
        var storage = new Mock<IStoragePathService>(MockBehavior.Strict);
        repository.Setup(value => value.GetByIdWithRelationsAsync(job.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(job);
        snapshots.Setup(value => value.CaptureJobSnapshotIfAbsentAsync(
                job,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        repository.Setup(value => value.AddDispatchLog(
            It.Is<DispatchLog>(log => log.PrintJobId == job.Id && log.PrinterId == printerId)));
        repository.Setup(value => value.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        storage.Setup(value => value.GetGcodeStorageDirectory())
            .Returns("/home/jpapiez/s/pf-wt/714/nonexistent-test-storage");
        PrintJobManagementService service = CreateService(
            repository,
            snapshots.Object,
            storage.Object);

        Farm.Infrastructure.Dtos.PrintQueue.QueuedPrintJobDto result =
            await service.DispatchJobAsync(job.Id.ToString(), "operator");

        Assert.Equal(nameof(PrintJobStatus.Assigned), result.Status);
        Assert.NotNull(job.DispatchedAt);
        Assert.Equal("The G-code artifact is unavailable for dispatch.", job.FailureReason);
        snapshots.VerifyAll();
        repository.Verify(
            value => value.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    // #714: harvestedAt must be projected onto QueuedPrintJobDto so mobile
    // clients can filter already-harvested completed jobs out of the scan
    // picker and gate the Harvest affordance in job detail.
    [Fact]
    public async Task GetAllQueuedJobsAsync_UnharvestedJob_ReturnsNullHarvestedAt()
    {
        PrintJob job = new()
        {
            Id = Guid.NewGuid(),
            Name = "unharvested.gcode",
            Status = PrintJobStatus.Completed,
            QueuedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            HarvestedAt = null
        };

        Mock<IPrintJobManagementRepository> repository = new();
        repository.Setup(r => r.GetFilteredJobsAsync(
                It.IsAny<PrintJobStatus?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<DateTime?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([job]);

        PrintJobManagementService service = CreateService(repository);

        List<Farm.Infrastructure.Dtos.PrintQueue.QueuedPrintJobWithFileMetaDto> result =
            await service.GetAllQueuedJobsAsync();

        Farm.Infrastructure.Dtos.PrintQueue.QueuedPrintJobWithFileMetaDto dto = Assert.Single(result);
        Assert.Null(dto.Job.HarvestedAt);
    }

    [Fact]
    public async Task GetAllQueuedJobsAsync_HarvestedJob_ProjectsHarvestedAt()
    {
        DateTime harvestedAt = new(2026, 7, 15, 12, 34, 56, DateTimeKind.Utc);
        PrintJob job = new()
        {
            Id = Guid.NewGuid(),
            Name = "harvested.gcode",
            Status = PrintJobStatus.Completed,
            QueuedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            HarvestedAt = harvestedAt
        };

        Mock<IPrintJobManagementRepository> repository = new();
        repository.Setup(r => r.GetFilteredJobsAsync(
                It.IsAny<PrintJobStatus?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<DateTime?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([job]);

        PrintJobManagementService service = CreateService(repository);

        List<Farm.Infrastructure.Dtos.PrintQueue.QueuedPrintJobWithFileMetaDto> result =
            await service.GetAllQueuedJobsAsync();

        Farm.Infrastructure.Dtos.PrintQueue.QueuedPrintJobWithFileMetaDto dto = Assert.Single(result);
        Assert.Equal(harvestedAt, dto.Job.HarvestedAt);
        Assert.Equal(nameof(PrintJobStatus.Completed), dto.Job.Status);
    }

    private static PrintJobManagementService CreateService(
        Mock<IPrintJobManagementRepository> repository,
        IPartOutputSnapshotService? snapshots = null,
        IStoragePathService? storage = null)
    {
        return new PrintJobManagementService(
            repository.Object,
            NullLogger<PrintJobManagementService>.Instance,
            Mock.Of<IPrintersService>(),
            storage ?? Mock.Of<IStoragePathService>(),
            Mock.Of<IHubContext<PrinterHub>>(),
            Mock.Of<IStoredFileOperationsService>(),
            Mock.Of<IPrinterStatusCacheReader>(),
            notificationService: Mock.Of<INotificationService>(),
            retryService: Mock.Of<IRetryService>(),
            printerStatusRefreshService: Mock.Of<IPrinterStatusRefreshService>(),
            jobCostCalculationService: Mock.Of<IJobCostCalculationService>(),
            cameraSnapshotService: Mock.Of<ICameraSnapshotService>(),
            serviceScopeFactory: Mock.Of<IServiceScopeFactory>(),
            settingsService: null,
            partOutputSnapshotService: snapshots);
    }
}
