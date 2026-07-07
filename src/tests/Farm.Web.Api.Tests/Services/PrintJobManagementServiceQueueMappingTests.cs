using Farm.Api.Services.PrintQueue;
using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Queue;
using Farm.Infrastructure.Services;
using Farm.Infrastructure.Services.Cameras;
using Farm.Infrastructure.Services.Cost;
using Farm.Infrastructure.Services.FileManagement;
using Farm.Infrastructure.Services.Interfaces;
using Farm.Infrastructure.Services.Notifications;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.Queue.Dispatch;
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

    private static PrintJobManagementService CreateService(Mock<IPrintJobManagementRepository> repository)
    {
        return new PrintJobManagementService(
            repository.Object,
            NullLogger<PrintJobManagementService>.Instance,
            Mock.Of<IPrintersService>(),
            Mock.Of<IStoragePathService>(),
            Mock.Of<IHubContext<PrinterHub>>(),
            Mock.Of<IStoredFileOperationsService>(),
            Mock.Of<IPrinterStatusCacheReader>(),
            notificationService: Mock.Of<INotificationService>(),
            retryService: Mock.Of<IRetryService>(),
            printerStatusRefreshService: Mock.Of<IPrinterStatusRefreshService>(),
            jobCostCalculationService: Mock.Of<IJobCostCalculationService>(),
            cameraSnapshotService: Mock.Of<ICameraSnapshotService>(),
            serviceScopeFactory: Mock.Of<IServiceScopeFactory>(),
            dispatchScorer: Mock.Of<IDispatchScorer>(),
            settingsService: null);
    }
}
