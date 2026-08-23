using System.ComponentModel.DataAnnotations;
using Farm.Api.Services.PrintQueue;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
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
using Farm.Infrastructure.Services.Queue.Dispatch;
using Farm.Infrastructure.Services.SignalR;
using Farm.Infrastructure.Services.StorageManagement;
using Farm.Infrastructure.Settings;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
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
    public async Task GetAllQueuedJobsAsync_GcodeFileWithoutThumbnailMetadata_OmitsThumbnailUrl()
    {
        // Regression test for #1911: a queued gcode file with no embedded thumbnail
        // metadata (ThumbnailFileName is null) must NOT get a thumbnail URL. Building
        // one unconditionally makes the frontend request a URL that 404s and renders
        // the browser's broken-image icon instead of the placeholder.
        GcodeFile fileWithoutThumbnail = new()
        {
            Id = Guid.NewGuid(),
            Name = "no-thumbnail.gcode",
            FileName = "no-thumbnail.gcode",
            ThumbnailFileName = null,
        };

        PrintJob job = new()
        {
            Id = Guid.NewGuid(),
            Name = "no-thumbnail-print",
            Status = PrintJobStatus.Queued,
            GcodeFile = fileWithoutThumbnail,
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
            .ReturnsAsync([job]);

        Mock<IStoredFileOperationsService> fileOperations = new();
        fileOperations
            .Setup(f => f.BuildGcodeThumbnailUrl(It.IsAny<Guid>()))
            .Returns<Guid>(id => $"/api/gcode-files/thumbnail/{id}");

        PrintJobManagementService service = CreateService(repository, fileOperations: fileOperations);

        List<Farm.Infrastructure.Dtos.PrintQueue.QueuedPrintJobWithFileMetaDto> result =
            await service.GetAllQueuedJobsAsync();

        Farm.Infrastructure.Dtos.PrintQueue.QueuedPrintJobWithFileMetaDto dto = Assert.Single(result);
        Assert.Null(dto.GcodeFile.ThumbnailUrl);

        // MapToQueuedPrintJobDto (the sibling mapping method) sets its own,
        // independent ThumbnailUrl on QueuedPrintJobDto — it must be gated the
        // same way as QueueGcodeFileMetaDto.ThumbnailUrl above.
        Assert.Null(dto.Job.ThumbnailUrl);
    }

    [Fact]
    public async Task GetAllQueuedJobsAsync_GcodeFileWithThumbnailMetadata_BuildsThumbnailUrl()
    {
        // Companion to the regression test above: a gcode file that DOES have
        // embedded thumbnail metadata must still get a thumbnail URL.
        Guid fileId = Guid.NewGuid();
        GcodeFile fileWithThumbnail = new()
        {
            Id = fileId,
            Name = "with-thumbnail.gcode",
            FileName = "with-thumbnail.gcode",
            ThumbnailFileName = "with-thumbnail.png",
        };

        PrintJob job = new()
        {
            Id = Guid.NewGuid(),
            Name = "with-thumbnail-print",
            Status = PrintJobStatus.Queued,
            GcodeFile = fileWithThumbnail,
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
            .ReturnsAsync([job]);

        Mock<IStoredFileOperationsService> fileOperations = new();
        fileOperations
            .Setup(f => f.BuildGcodeThumbnailUrl(It.IsAny<Guid>()))
            .Returns<Guid>(id => $"/api/gcode-files/thumbnail/{id}");

        PrintJobManagementService service = CreateService(repository, fileOperations: fileOperations);

        List<Farm.Infrastructure.Dtos.PrintQueue.QueuedPrintJobWithFileMetaDto> result =
            await service.GetAllQueuedJobsAsync();

        Farm.Infrastructure.Dtos.PrintQueue.QueuedPrintJobWithFileMetaDto dto = Assert.Single(result);
        Assert.Equal($"/api/gcode-files/thumbnail/{fileId}", dto.GcodeFile.ThumbnailUrl);

        // Companion assertion: MapToQueuedPrintJobDto's own ThumbnailUrl must
        // also be populated when thumbnail metadata is present.
        Assert.Equal($"/api/gcode-files/thumbnail/{fileId}", dto.Job.ThumbnailUrl);
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
        repository.Setup(value => value.GetMaxQueuePositionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);
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
        var dispatchClaim = new Mock<IDispatchClaimService>(MockBehavior.Strict);
        QueueDispatchAttempt attempt = new()
        {
            Id = Guid.NewGuid(),
            PrintJobId = job.Id,
            PrinterId = printerId,
            BackendFileName = "missing.gcode",
            AttemptNumber = 1,
        };
        repository.Setup(value => value.GetByIdWithRelationsAsync(job.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(job);
        snapshots.Setup(value => value.CaptureJobSnapshotIfAbsentAsync(
                job,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        repository.Setup(value => value.AddDispatchLog(
            It.Is<DispatchLog>(log => log.PrintJobId == job.Id && log.PrinterId == printerId)));
        dispatchClaim.Setup(value => value.AcquireClaimAsync(
                It.IsAny<DispatchClaimRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(DispatchClaimResult.Ok(attempt));
        // ReleaseClaimOnKnownFailureAsync returns true → BuildDispatchResultAsync path
        // (persistence is now fully owned by IDispatchClaimService, not _repository)
        dispatchClaim.Setup(value => value.ReleaseClaimOnKnownFailureAsync(
                attempt.Id,
                "backend_rejected",
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        storage.Setup(value => value.GetGcodeStorageDirectory())
            .Returns("/home/jpapiez/s/pf-wt/714/nonexistent-test-storage");
        PrintJobManagementService service = CreateService(
            repository,
            snapshots.Object,
            storage.Object,
            dispatchClaimService: dispatchClaim.Object);

        Farm.Infrastructure.Dtos.PrintQueue.QueuedPrintJobDto result =
            await service.DispatchJobAsync(job.Id.ToString(), "operator");

        Assert.Equal(nameof(PrintJobStatus.Assigned), result.Status);
        Assert.NotNull(job.DispatchedAt);
        Assert.Equal("The G-code artifact is unavailable for dispatch.", job.FailureReason);
        snapshots.VerifyAll();
        dispatchClaim.Verify(
            value => value.AcquireClaimAsync(
                It.IsAny<DispatchClaimRequest>(), It.IsAny<CancellationToken>()),
            Times.Once);
        dispatchClaim.Verify(
            value => value.ReleaseClaimOnKnownFailureAsync(
                attempt.Id, "backend_rejected", It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
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

    [Fact]
    public async Task GetJobByIdAsync_AfterOtherClientStartsAttemptB_HydratesLatestAttempt()
    {
        Guid jobId = Guid.NewGuid();
        PrintJob job = new()
        {
            Id = jobId,
            Name = "Recovered job",
            Status = PrintJobStatus.Starting,
            Revision = 1,
            QueuedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        DbContextOptions<AppDbContext> options =
            new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
        await using AppDbContext db = new(options);
        db.QueueDispatchAttempts.AddRange(
            new QueueDispatchAttempt
            {
                Id = Guid.NewGuid(),
                PrintJobId = jobId,
                PrinterId = Guid.NewGuid(),
                AttemptNumber = 1,
                ActorSubject = "user:a",
                StartPathKind = "Manual",
                ClaimedAtUtc = DateTime.UtcNow.AddMinutes(-1),
                Outcome = DispatchAttemptOutcome.Accepted,
            },
            new QueueDispatchAttempt
            {
                Id = Guid.NewGuid(),
                PrintJobId = jobId,
                PrinterId = Guid.NewGuid(),
                AttemptNumber = 2,
                ActorSubject = "user:b",
                StartPathKind = "Manual",
                ClaimedAtUtc = DateTime.UtcNow,
                Outcome = DispatchAttemptOutcome.Unknown,
                RequiresReconciliation = true,
            });
        await db.SaveChangesAsync();
        Mock<IPrintJobManagementRepository> repository = new();
        repository
            .Setup(candidate => candidate.GetByIdWithGcodeFileAsync(
                jobId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(job);
        PrintJobManagementService service = CreateService(repository, appDbContext: db);

        Farm.Infrastructure.Dtos.PrintQueue.QueuedPrintJobDto? recovered =
            await service.GetJobByIdAsync(jobId.ToString());

        Assert.NotNull(recovered?.DispatchResult);
        Assert.Equal(2, recovered.DispatchResult!.AttemptNumber);
        Assert.Equal(
            DispatchAttemptOutcome.Unknown,
            recovered.DispatchResult.Outcome);
    }

    [Fact]
    public async Task GetPrinterQueueSummariesAsync_MapsRepositoryRecordsToDto()
    {
        Guid printerWithPrinting = Guid.NewGuid();
        Guid printerQueuedOnly = Guid.NewGuid();
        var repositorySummaries = new List<PrinterQueueSummary>
        {
            new(printerWithPrinting, QueuedCount: 2, PrintingCount: 1, PrintingPosition: 1),
            new(printerQueuedOnly, QueuedCount: 3, PrintingCount: 0, PrintingPosition: null),
        };

        Mock<IPrintJobManagementRepository> repository = new();
        repository
            .Setup(r => r.GetPrinterQueueSummariesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(repositorySummaries);
        PrintJobManagementService service = CreateService(repository);

        List<Farm.Infrastructure.Dtos.PrintQueue.PrinterQueueSummaryDto> result =
            await service.GetPrinterQueueSummariesAsync();

        Assert.Equal(2, result.Count);
        Farm.Infrastructure.Dtos.PrintQueue.PrinterQueueSummaryDto printing =
            Assert.Single(result, r => r.PrinterId == printerWithPrinting);
        Assert.Equal(2, printing.QueuedCount);
        Assert.Equal(1, printing.PrintingCount);
        Assert.Equal(1, printing.PrintingPosition);

        Farm.Infrastructure.Dtos.PrintQueue.PrinterQueueSummaryDto queuedOnly =
            Assert.Single(result, r => r.PrinterId == printerQueuedOnly);
        Assert.Equal(3, queuedOnly.QueuedCount);
        Assert.Equal(0, queuedOnly.PrintingCount);
        Assert.Null(queuedOnly.PrintingPosition);
    }

    [Theory]
    [InlineData(PrintJobPriority.Low)]
    [InlineData(PrintJobPriority.Normal)]
    [InlineData(PrintJobPriority.High)]
    [InlineData(PrintJobPriority.Urgent)]
    public async Task UpdateJobPriorityAsync_DefinedPriority_PreservesMeaningInPersistenceAndDto(
        PrintJobPriority priority)
    {
        PrintJob job = new()
        {
            Id = Guid.NewGuid(),
            Name = "priority.gcode",
            Priority = (int)PrintJobPriority.Normal,
            Status = PrintJobStatus.Queued,
            QueuedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        Mock<IPrintJobManagementRepository> repository = new(MockBehavior.Strict);
        repository.Setup(value => value.GetByIdAsync(job.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(job);
        repository.Setup(value => value.UpdateAsync(job, It.IsAny<CancellationToken>()))
            .ReturnsAsync(job);
        PrintJobManagementService service = CreateService(repository);

        QueuedPrintJobDto result = await service.UpdateJobPriorityAsync(
            job.Id.ToString(),
            priority,
            "operator");

        Assert.Equal((int)priority, job.Priority);
        Assert.Equal(priority, result.Priority);
        repository.VerifyAll();
    }

    [Fact]
    public async Task UpdateJobPriorityAsync_UndefinedPriority_RejectsBeforePersistence()
    {
        PrintJob job = new()
        {
            Id = Guid.NewGuid(),
            Name = "priority.gcode",
            Priority = (int)PrintJobPriority.Normal,
            Status = PrintJobStatus.Queued,
            QueuedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        Mock<IPrintJobManagementRepository> repository = new(MockBehavior.Strict);
        repository.Setup(value => value.GetByIdAsync(job.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(job);
        PrintJobManagementService service = CreateService(repository);

        ValidationException exception = await Assert.ThrowsAsync<ValidationException>(
            () => service.UpdateJobPriorityAsync(
                job.Id.ToString(),
                (PrintJobPriority)99,
                "operator"));

        Assert.Contains("not a valid PrintJobPriority", exception.Message, StringComparison.Ordinal);
        Assert.Equal((int)PrintJobPriority.Normal, job.Priority);
        repository.VerifyAll();
    }

    private static PrintJobManagementService CreateService(
        Mock<IPrintJobManagementRepository> repository,
        IPartOutputSnapshotService? snapshots = null,
        IStoragePathService? storage = null,
        AppDbContext? appDbContext = null,
        IDispatchClaimService? dispatchClaimService = null,
        Mock<IStoredFileOperationsService>? fileOperations = null)
    {
        return new PrintJobManagementService(
            repository.Object,
            NullLogger<PrintJobManagementService>.Instance,
            Mock.Of<IPrintersService>(),
            storage ?? Mock.Of<IStoragePathService>(),
            Mock.Of<IHubContext<PrinterHub>>(),
            fileOperations?.Object ?? Mock.Of<IStoredFileOperationsService>(),
            Mock.Of<IPrinterStatusCacheReader>(),
            notificationService: Mock.Of<INotificationService>(),
            retryService: Mock.Of<IRetryService>(),
            printerStatusRefreshService: Mock.Of<IPrinterStatusRefreshService>(),
            jobCostCalculationService: Mock.Of<IJobCostCalculationService>(),
            cameraSnapshotService: Mock.Of<ICameraSnapshotService>(),
            serviceScopeFactory: Mock.Of<IServiceScopeFactory>(),
            settingsService: null,
            partOutputSnapshotService: snapshots,
            appDbContext: appDbContext,
            dispatchClaimService: dispatchClaimService);
    }
}
