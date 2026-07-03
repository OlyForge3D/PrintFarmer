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

namespace Farm.Web.Api.Tests.Services;

public class PrintJobManagementServiceHistorySeedingTests
{
    [Fact]
    public async Task SyncActiveExternalJobsFromPrintersAsync_WithNonTerminalExternalHistoryJob_IngestsAndTracksJob()
    {
        Guid printerId = Guid.NewGuid();
        DateTime startUtc = DateTime.UtcNow.AddMinutes(-5);
        long startUnix = new DateTimeOffset(startUtc).ToUnixTimeSeconds();

        Printer printer = new()
        {
            Id = printerId,
            Name = "Prusa Active External",
            Backend = (int)PrinterBackend.PrusaLink,
            IsEnabled = true,
            ServiceState = new PrinterServiceState { PrinterId = printerId, LastHistorySeedUtc = DateTime.UtcNow.AddMinutes(-30) }
        };

        HistoryListResponse historyResponse = new()
        {
            Count = 2,
            Jobs =
            [
                new HistoryJob
                {
                    JobId = "ext-active-1",
                    Filename = "external-active.gcode",
                    Status = "printing",
                    StartTime = startUnix,
                    EndTime = null,
                    FilamentUsed = 250,
                    Metadata = []
                },
                new HistoryJob
                {
                    JobId = "ext-done-1",
                    Filename = "external-done.gcode",
                    Status = "completed",
                    StartTime = startUnix,
                    EndTime = startUnix + 60,
                    FilamentUsed = 250,
                    Metadata = []
                }
            ]
        };

        Mock<IPrintJobManagementRepository> repository = new();
        repository.Setup(r => r.GetEnabledPrintersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([printer]);
        repository.Setup(r => r.GetExternalJobIdsForPrinterAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        repository.Setup(r => r.FindExistingJobForHistoryMatchAsync(
                printerId,
                It.IsAny<string>(),
                It.IsAny<DateTime>(),
                It.IsAny<DateTime?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((PrintJob?)null);
        repository.Setup(r => r.FindGcodeFileByFilenameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((GcodeFile?)null);
        repository.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        repository.Setup(r => r.UpdatePrinterLastHistorySeedAsync(printerId, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        PrintJob? addedJob = null;
        repository.Setup(r => r.Add(It.IsAny<PrintJob>()))
            .Callback<PrintJob>(job => addedJob = job);

        Mock<IPrintersService> printersService = new();
        printersService.Setup(p => p.GetHistoryListAsync(
                printerId,
                1000,
                0,
                It.IsAny<DateTime?>(),
                null,
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(historyResponse);

        PrintJobManagementService service = CreateService(repository, printersService);

        await service.SyncActiveExternalJobsFromPrintersAsync();

        repository.Verify(r => r.Add(It.IsAny<PrintJob>()), Times.Once);
        repository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        Assert.NotNull(addedJob);
        Assert.Equal("ext-active-1", addedJob!.ExternalJobId);
        Assert.Equal(PrintJobStatus.Printing, addedJob.Status);
    }

    [Fact]
    public async Task SyncActiveExternalJobsFromPrintersAsync_WhenExternalJobAlreadyKnown_RefreshesNonTerminalJobWithoutInsert()
    {
        Guid printerId = Guid.NewGuid();
        DateTime startUtc = DateTime.UtcNow.AddMinutes(-10);
        long startUnix = new DateTimeOffset(startUtc).ToUnixTimeSeconds();

        Printer printer = new()
        {
            Id = printerId,
            Name = "Prusa Active Dedupe",
            Backend = (int)PrinterBackend.PrusaLink,
            IsEnabled = true,
            ServiceState = new PrinterServiceState { PrinterId = printerId, LastHistorySeedUtc = DateTime.UtcNow.AddMinutes(-20) }
        };

        PrintJob seededJob = new()
        {
            Id = Guid.NewGuid(),
            Name = "external-active",
            ExternalJobId = "ext-active-2",
            SourcePrinterId = printerId,
            WasSeededFromHistory = true,
            Status = PrintJobStatus.Queued,
            CreatedAt = DateTime.UtcNow.AddHours(-1),
            UpdatedAt = DateTime.UtcNow.AddHours(-1),
            QueuedAt = DateTime.UtcNow.AddHours(-1)
        };

        HistoryListResponse historyResponse = new()
        {
            Count = 1,
            Jobs =
            [
                new HistoryJob
                {
                    JobId = "ext-active-2",
                    Filename = "external-active.gcode",
                    Status = "in_progress",
                    StartTime = startUnix,
                    EndTime = null,
                    FilamentUsed = 300,
                    Metadata = []
                }
            ]
        };

        Mock<IPrintJobManagementRepository> repository = new();
        repository.Setup(r => r.GetEnabledPrintersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([printer]);
        repository.Setup(r => r.GetExternalJobIdsForPrinterAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(["ext-active-2"]);
        repository.Setup(r => r.FindExistingJobForHistoryMatchAsync(
                printerId,
                "external-active.gcode",
                It.IsAny<DateTime>(),
                It.IsAny<DateTime?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((PrintJob?)null);
        repository.Setup(r => r.GetByExternalIdAsync(printerId, "ext-active-2", It.IsAny<CancellationToken>()))
            .ReturnsAsync(seededJob);
        repository.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        repository.Setup(r => r.UpdatePrinterLastHistorySeedAsync(printerId, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        Mock<IPrintersService> printersService = new();
        printersService.Setup(p => p.GetHistoryListAsync(
                printerId,
                1000,
                0,
                It.IsAny<DateTime?>(),
                null,
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(historyResponse);

        PrintJobManagementService service = CreateService(repository, printersService);

        await service.SyncActiveExternalJobsFromPrintersAsync();

        repository.Verify(r => r.Add(It.IsAny<PrintJob>()), Times.Never);
        repository.Verify(r => r.GetByExternalIdAsync(printerId, "ext-active-2", It.IsAny<CancellationToken>()), Times.Once);
        repository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(PrintJobStatus.Printing, seededJob.Status);
    }

    [Fact]
    public async Task SeedHistoryFromPrintersAsync_WithNonTerminalExternalHistoryJob_IngestsAndTracksJob()
    {
        Guid printerId = Guid.NewGuid();
        DateTime startUtc = DateTime.UtcNow.AddMinutes(-15);
        long startUnix = new DateTimeOffset(startUtc).ToUnixTimeSeconds();

        Printer printer = new()
        {
            Id = printerId,
            Name = "Prusa External",
            Backend = (int)PrinterBackend.PrusaLink,
            IsEnabled = true,
            ServiceState = null
        };

        HistoryListResponse historyResponse = new()
        {
            Count = 1,
            Jobs =
            [
                new HistoryJob
                {
                    JobId = "ext-running-1",
                    Filename = "external-job.gcode",
                    Status = "in_progress",
                    StartTime = startUnix,
                    EndTime = null,
                    FilamentUsed = 1000,
                    Metadata = []
                }
            ]
        };

        Mock<IPrintJobManagementRepository> repository = new();
        repository.Setup(r => r.GetEnabledPrintersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([printer]);
        repository.Setup(r => r.GetExternalJobIdsForPrinterAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        repository.Setup(r => r.FindExistingJobForHistoryMatchAsync(
                printerId,
                "external-job.gcode",
                It.IsAny<DateTime>(),
                It.IsAny<DateTime?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((PrintJob?)null);
        repository.Setup(r => r.FindGcodeFileByFilenameAsync("external-job.gcode", It.IsAny<CancellationToken>()))
            .ReturnsAsync((GcodeFile?)null);

        PrintJob? addedJob = null;
        repository.Setup(r => r.Add(It.IsAny<PrintJob>()))
            .Callback<PrintJob>(job => addedJob = job);

        repository.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        repository.Setup(r => r.UpdatePrinterLastHistorySeedAsync(printerId, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        Mock<IPrintersService> printersService = new();
        printersService.Setup(p => p.GetHistoryListAsync(
                printerId,
                10000,
                0,
                null,
                null,
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(historyResponse);

        PrintJobManagementService service = CreateService(repository, printersService);

        await service.SeedHistoryFromPrintersAsync();

        repository.Verify(r => r.Add(It.IsAny<PrintJob>()), Times.Once);
        repository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        repository.Verify(r => r.UpdatePrinterLastHistorySeedAsync(printerId, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);

        Assert.NotNull(addedJob);
        Assert.Equal("ext-running-1", addedJob!.ExternalJobId);
        Assert.Equal(printerId, addedJob.SourcePrinterId);
        Assert.True(addedJob.WasSeededFromHistory);
        Assert.Equal(PrintJobStatus.Printing, addedJob.Status);
    }

    [Fact]
    public async Task SeedHistoryFromPrintersAsync_WhenHistoryMatchesExistingPrintFarmerJob_LinksExistingAndDoesNotInsertDuplicate()
    {
        Guid printerId = Guid.NewGuid();
        DateTime startUtc = DateTime.UtcNow.AddMinutes(-20);
        DateTime endUtc = DateTime.UtcNow.AddMinutes(-5);
        long startUnix = new DateTimeOffset(startUtc).ToUnixTimeSeconds();
        long endUnix = new DateTimeOffset(endUtc).ToUnixTimeSeconds();

        Printer printer = new()
        {
            Id = printerId,
            Name = "Prusa Existing",
            Backend = (int)PrinterBackend.PrusaLink,
            IsEnabled = true,
            ServiceState = null
        };

        PrintJob existingPrintFarmerJob = new()
        {
            Id = Guid.NewGuid(),
            Name = "linked-job",
            AssignedPrinterId = printerId,
            Status = PrintJobStatus.Printing,
            CreatedAt = DateTime.UtcNow.AddHours(-1),
            UpdatedAt = DateTime.UtcNow.AddHours(-1),
            QueuedAt = DateTime.UtcNow.AddHours(-1)
        };

        HistoryListResponse historyResponse = new()
        {
            Count = 1,
            Jobs =
            [
                new HistoryJob
                {
                    JobId = "hist-42",
                    Filename = "linked-job.gcode",
                    Status = "completed",
                    StartTime = startUnix,
                    EndTime = endUnix,
                    FilamentUsed = 750,
                    Metadata = []
                }
            ]
        };

        Mock<IPrintJobManagementRepository> repository = new();
        repository.Setup(r => r.GetEnabledPrintersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([printer]);
        repository.Setup(r => r.GetExternalJobIdsForPrinterAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        repository.Setup(r => r.FindExistingJobForHistoryMatchAsync(
                printerId,
                "linked-job.gcode",
                It.IsAny<DateTime>(),
                It.IsAny<DateTime?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingPrintFarmerJob);
        repository.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        repository.Setup(r => r.UpdatePrinterLastHistorySeedAsync(printerId, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        Mock<IPrintersService> printersService = new();
        printersService.Setup(p => p.GetHistoryListAsync(
                printerId,
                10000,
                0,
                null,
                null,
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(historyResponse);

        PrintJobManagementService service = CreateService(repository, printersService);

        await service.SeedHistoryFromPrintersAsync();

        repository.Verify(r => r.Add(It.IsAny<PrintJob>()), Times.Never);
        repository.Verify(r => r.Remove(It.IsAny<PrintJob>()), Times.Never);
        repository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);

        Assert.Equal("hist-42", existingPrintFarmerJob.ExternalJobId);
        Assert.Equal(printerId, existingPrintFarmerJob.SourcePrinterId);
        Assert.Equal(PrintJobStatus.Completed, existingPrintFarmerJob.Status);
    }

    [Fact]
    public async Task SeedHistoryFromPrintersAsync_WhenExternalJobAlreadyKnown_DoesNotInsertDuplicateRow()
    {
        Guid printerId = Guid.NewGuid();
        DateTime startUtc = DateTime.UtcNow.AddMinutes(-30);
        long startUnix = new DateTimeOffset(startUtc).ToUnixTimeSeconds();

        Printer printer = new()
        {
            Id = printerId,
            Name = "Prusa Dedupe",
            Backend = (int)PrinterBackend.PrusaLink,
            IsEnabled = true,
            ServiceState = new PrinterServiceState { PrinterId = printerId, LastHistorySeedUtc = null }
        };

        HistoryListResponse historyResponse = new()
        {
            Count = 1,
            Jobs =
            [
                new HistoryJob
                {
                    JobId = "dup-99",
                    Filename = "dup.gcode",
                    Status = "completed",
                    StartTime = startUnix,
                    EndTime = null,
                    FilamentUsed = 400,
                    Metadata = []
                }
            ]
        };

        PrintJob seededJob = new()
        {
            Id = Guid.NewGuid(),
            Name = "dup",
            ExternalJobId = "dup-99",
            SourcePrinterId = printerId,
            WasSeededFromHistory = true,
            Status = PrintJobStatus.Queued,
            CreatedAt = DateTime.UtcNow.AddHours(-2),
            UpdatedAt = DateTime.UtcNow.AddHours(-2),
            QueuedAt = DateTime.UtcNow.AddHours(-2)
        };

        Mock<IPrintJobManagementRepository> repository = new();
        repository.Setup(r => r.GetEnabledPrintersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([printer]);
        repository.Setup(r => r.GetExternalJobIdsForPrinterAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(["dup-99"]);
        repository.Setup(r => r.FindExistingJobForHistoryMatchAsync(
                printerId,
                "dup.gcode",
                It.IsAny<DateTime>(),
                It.IsAny<DateTime?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((PrintJob?)null);
        repository.Setup(r => r.GetByExternalIdAsync(printerId, "dup-99", It.IsAny<CancellationToken>()))
            .ReturnsAsync(seededJob);
        repository.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        repository.Setup(r => r.UpdatePrinterLastHistorySeedAsync(printerId, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        Mock<IPrintersService> printersService = new();
        printersService.Setup(p => p.GetHistoryListAsync(
                printerId,
                10000,
                0,
                null,
                null,
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(historyResponse);

        PrintJobManagementService service = CreateService(repository, printersService);

        await service.SeedHistoryFromPrintersAsync();

        repository.Verify(r => r.Add(It.IsAny<PrintJob>()), Times.Never);
        repository.Verify(r => r.Remove(It.IsAny<PrintJob>()), Times.Never);
        repository.Verify(r => r.GetByExternalIdAsync(printerId, "dup-99", It.IsAny<CancellationToken>()), Times.Once);

        Assert.Equal(PrintJobStatus.Completed, seededJob.Status);
        Assert.Equal("dup-99", seededJob.ExternalJobId);
    }

    [Fact]
    public async Task SeedHistoryFromPrintersAsync_WhenKnownExternalJobIsNonTerminal_UpdatesExistingSeededJob()
    {
        Guid printerId = Guid.NewGuid();
        DateTime startUtc = DateTime.UtcNow.AddMinutes(-10);
        long startUnix = new DateTimeOffset(startUtc).ToUnixTimeSeconds();

        Printer printer = new()
        {
            Id = printerId,
            Name = "Prusa Active Update",
            Backend = (int)PrinterBackend.PrusaLink,
            IsEnabled = true,
            ServiceState = new PrinterServiceState { PrinterId = printerId, LastHistorySeedUtc = null }
        };

        HistoryListResponse historyResponse = new()
        {
            Count = 1,
            Jobs =
            [
                new HistoryJob
                {
                    JobId = "active-77",
                    Filename = "active-job.gcode",
                    Status = "in_progress",
                    StartTime = startUnix,
                    EndTime = null,
                    FilamentUsed = 600,
                    Metadata = []
                }
            ]
        };

        PrintJob seededJob = new()
        {
            Id = Guid.NewGuid(),
            Name = "active-job",
            ExternalJobId = "active-77",
            SourcePrinterId = printerId,
            WasSeededFromHistory = true,
            Status = PrintJobStatus.Queued,
            CreatedAt = DateTime.UtcNow.AddHours(-1),
            UpdatedAt = DateTime.UtcNow.AddHours(-1),
            QueuedAt = DateTime.UtcNow.AddHours(-1)
        };

        Mock<IPrintJobManagementRepository> repository = new();
        repository.Setup(r => r.GetEnabledPrintersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([printer]);
        repository.Setup(r => r.GetExternalJobIdsForPrinterAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(["active-77"]);
        repository.Setup(r => r.FindExistingJobForHistoryMatchAsync(
                printerId,
                "active-job.gcode",
                It.IsAny<DateTime>(),
                It.IsAny<DateTime?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((PrintJob?)null);
        repository.Setup(r => r.GetByExternalIdAsync(printerId, "active-77", It.IsAny<CancellationToken>()))
            .ReturnsAsync(seededJob);
        repository.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        repository.Setup(r => r.UpdatePrinterLastHistorySeedAsync(printerId, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        Mock<IPrintersService> printersService = new();
        printersService.Setup(p => p.GetHistoryListAsync(
                printerId,
                10000,
                0,
                null,
                null,
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(historyResponse);

        PrintJobManagementService service = CreateService(repository, printersService);

        await service.SeedHistoryFromPrintersAsync();

        repository.Verify(r => r.Add(It.IsAny<PrintJob>()), Times.Never);
        repository.Verify(r => r.GetByExternalIdAsync(printerId, "active-77", It.IsAny<CancellationToken>()), Times.Once);
        repository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);

        Assert.Equal(PrintJobStatus.Printing, seededJob.Status);
        Assert.Equal("active-77", seededJob.ExternalJobId);
    }

    [Fact]
    public async Task SeedHistoryFromPrintersAsync_WithTerminalExternalHistoryJob_StillInsertsNewHistoryJob()
    {
        Guid printerId = Guid.NewGuid();
        DateTime startUtc = DateTime.UtcNow.AddMinutes(-40);
        DateTime endUtc = DateTime.UtcNow.AddMinutes(-5);
        long startUnix = new DateTimeOffset(startUtc).ToUnixTimeSeconds();
        long endUnix = new DateTimeOffset(endUtc).ToUnixTimeSeconds();

        Printer printer = new()
        {
            Id = printerId,
            Name = "Prusa Terminal Insert",
            Backend = (int)PrinterBackend.PrusaLink,
            IsEnabled = true,
            ServiceState = null
        };

        HistoryListResponse historyResponse = new()
        {
            Count = 1,
            Jobs =
            [
                new HistoryJob
                {
                    JobId = "terminal-55",
                    Filename = "terminal-job.gcode",
                    Status = "completed",
                    StartTime = startUnix,
                    EndTime = endUnix,
                    FilamentUsed = 500,
                    Metadata = []
                }
            ]
        };

        Mock<IPrintJobManagementRepository> repository = new();
        repository.Setup(r => r.GetEnabledPrintersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([printer]);
        repository.Setup(r => r.GetExternalJobIdsForPrinterAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        repository.Setup(r => r.FindExistingJobForHistoryMatchAsync(
                printerId,
                "terminal-job.gcode",
                It.IsAny<DateTime>(),
                It.IsAny<DateTime?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((PrintJob?)null);
        repository.Setup(r => r.FindGcodeFileByFilenameAsync("terminal-job.gcode", It.IsAny<CancellationToken>()))
            .ReturnsAsync((GcodeFile?)null);

        PrintJob? addedJob = null;
        repository.Setup(r => r.Add(It.IsAny<PrintJob>()))
            .Callback<PrintJob>(job => addedJob = job);
        repository.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        repository.Setup(r => r.UpdatePrinterLastHistorySeedAsync(printerId, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        Mock<IPrintersService> printersService = new();
        printersService.Setup(p => p.GetHistoryListAsync(
                printerId,
                10000,
                0,
                null,
                null,
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(historyResponse);

        PrintJobManagementService service = CreateService(repository, printersService);

        await service.SeedHistoryFromPrintersAsync();

        repository.Verify(r => r.Add(It.IsAny<PrintJob>()), Times.Once);
        repository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);

        Assert.NotNull(addedJob);
        Assert.Equal("terminal-55", addedJob!.ExternalJobId);
        Assert.Equal(printerId, addedJob.SourcePrinterId);
        Assert.Equal(PrintJobStatus.Completed, addedJob.Status);
    }

    private static PrintJobManagementService CreateService(
        Mock<IPrintJobManagementRepository> repository,
        Mock<IPrintersService> printersService)
    {
        return new PrintJobManagementService(
            repository.Object,
            NullLogger<PrintJobManagementService>.Instance,
            printersService.Object,
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
            settingsService: Mock.Of<ISettingsService>());
    }
}
