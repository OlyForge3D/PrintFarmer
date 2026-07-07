using System.Reflection;
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
        repository.Setup(r => r.GetActualStartTimesForPrinterAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => []);
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
        repository.Setup(r => r.GetActualStartTimesForPrinterAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => []);
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
    public async Task SyncActiveExternalJobsFromPrintersAsync_DoesNotUseOrAdvanceSharedHistoryWatermark()
    {
        Guid printerId = Guid.NewGuid();
        DateTime lastSeedUtc = DateTime.UtcNow.AddMinutes(-45);
        DateTime startUtc = DateTime.UtcNow.AddMinutes(-2);
        long startUnix = new DateTimeOffset(startUtc).ToUnixTimeSeconds();

        Printer printer = new()
        {
            Id = printerId,
            Name = "Prusa Active Watermark Isolation",
            Backend = (int)PrinterBackend.PrusaLink,
            IsEnabled = true,
            ServiceState = new PrinterServiceState { PrinterId = printerId, LastHistorySeedUtc = lastSeedUtc }
        };

        HistoryListResponse historyResponse = new()
        {
            Count = 1,
            Jobs =
            [
                new HistoryJob
                {
                    JobId = "active-wm-1",
                    Filename = "active-watermark.gcode",
                    Status = "printing",
                    StartTime = startUnix,
                    EndTime = null,
                    FilamentUsed = 200,
                    Metadata = []
                }
            ]
        };

        Mock<IPrintJobManagementRepository> repository = new();
        repository.Setup(r => r.GetEnabledPrintersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([printer]);
        repository.Setup(r => r.GetExternalJobIdsForPrinterAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        repository.Setup(r => r.GetActualStartTimesForPrinterAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => []);
        repository.Setup(r => r.FindExistingJobForHistoryMatchAsync(
                printerId,
                It.IsAny<string>(),
                It.IsAny<DateTime>(),
                It.IsAny<DateTime?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((PrintJob?)null);
        repository.Setup(r => r.GetByExternalIdAsync(printerId, "active-wm-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync((PrintJob?)null);
        repository.Setup(r => r.FindGcodeFileByFilenameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((GcodeFile?)null);
        repository.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        Mock<IPrintersService> printersService = new();
        printersService.Setup(p => p.GetHistoryListAsync(
                printerId,
                1000,
                0,
                null,
                null,
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(historyResponse);

        PrintJobManagementService service = CreateService(repository, printersService);

        await service.SyncActiveExternalJobsFromPrintersAsync();

        printersService.Verify(p => p.GetHistoryListAsync(
            printerId,
            1000,
            0,
            null,
            null,
            null,
            It.IsAny<CancellationToken>()), Times.Once);
        repository.Verify(r => r.UpdatePrinterLastHistorySeedAsync(It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
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
        repository.Setup(r => r.GetActualStartTimesForPrinterAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => []);
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
        repository.Setup(r => r.GetActualStartTimesForPrinterAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => []);
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
        repository.Setup(r => r.GetActualStartTimesForPrinterAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => []);
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
    public async Task SeedHistoryFromPrintersAsync_WhenTwoExternalHistoryJobsSharePrinterAndStart_InsertsSingleStoredJob()
    {
        string dbName = $"HistoryStartDedupe_{Guid.NewGuid():N}";
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;

        Guid printerId = Guid.NewGuid();
        DateTime startUtc = TruncateToSecond(DateTime.UtcNow.AddMinutes(-30));
        long startUnix = new DateTimeOffset(startUtc).ToUnixTimeSeconds();

        await using AppDbContext db = new(options);
        db.Printers.Add(CreateEnabledPrinter(printerId, "Start Dedupe Printer"));
        await db.SaveChangesAsync();

        HistoryListResponse historyResponse = new()
        {
            Count = 2,
            Jobs =
            [
                new HistoryJob
                {
                    JobId = "same-start-1",
                    Filename = "same-start-a.gcode",
                    Status = "completed",
                    StartTime = startUnix,
                    EndTime = startUnix + 60,
                    FilamentUsed = 400,
                    Metadata = []
                },
                new HistoryJob
                {
                    JobId = "same-start-2",
                    Filename = "same-start-b.gcode",
                    Status = "completed",
                    StartTime = startUnix,
                    EndTime = startUnix + 90,
                    FilamentUsed = 450,
                    Metadata = []
                }
            ]
        };

        Mock<IPrintersService> printersService = CreateHistoryListMock(printerId, historyResponse);
        PrintJobManagementService service = CreateService(new EfPrintJobManagementRepository(db), printersService);

        await service.SeedHistoryFromPrintersAsync();

        List<PrintJob> storedJobs = await db.PrintJobs
            .Where(j => j.AssignedPrinterId == printerId && j.ActualStartTime == startUtc)
            .ToListAsync();
        PrintJob storedJob = Assert.Single(storedJobs);
        Assert.Equal("same-start-1", storedJob.ExternalJobId);
        Assert.True(storedJob.WasSeededFromHistory);
    }

    [Fact]
    public async Task SeedHistoryFromPrintersAsync_WhenTwoExternalHistoryJobsHaveMissingStart_InsertsBothStoredJobs()
    {
        string dbName = $"HistoryMissingStartDedupe_{Guid.NewGuid():N}";
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;

        Guid printerId = Guid.NewGuid();

        await using AppDbContext db = new(options);
        db.Printers.Add(CreateEnabledPrinter(printerId, "Missing Start Printer"));
        await db.SaveChangesAsync();

        HistoryListResponse historyResponse = new()
        {
            Count = 2,
            Jobs =
            [
                new HistoryJob
                {
                    JobId = "missing-start-1",
                    Filename = "missing-start-a.gcode",
                    Status = "completed",
                    StartTime = 0,
                    EndTime = null,
                    FilamentUsed = 400,
                    Metadata = []
                },
                new HistoryJob
                {
                    JobId = "missing-start-2",
                    Filename = "missing-start-b.gcode",
                    Status = "completed",
                    StartTime = 0,
                    EndTime = null,
                    FilamentUsed = 450,
                    Metadata = []
                }
            ]
        };

        Mock<IPrintersService> printersService = CreateHistoryListMock(printerId, historyResponse);
        PrintJobManagementService service = CreateService(new EfPrintJobManagementRepository(db), printersService);

        await service.SeedHistoryFromPrintersAsync();

        List<string?> storedExternalIds = await db.PrintJobs
            .Where(j => j.AssignedPrinterId == printerId)
            .OrderBy(j => j.ExternalJobId)
            .Select(j => j.ExternalJobId)
            .ToListAsync();

        Assert.Equal(["missing-start-1", "missing-start-2"], storedExternalIds);
    }

    [Fact]
    public async Task SeedHistoryFromPrintersAsync_WhenExternalIdAbsentJobAlreadyHasSamePrinterStartSecond_DoesNotInsertDuplicateStoredJob()
    {
        string dbName = $"HistoryNullExternalStartDedupe_{Guid.NewGuid():N}";
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;

        Guid printerId = Guid.NewGuid();
        DateTime startUtc = TruncateToSecond(DateTime.UtcNow.AddMinutes(-45));
        long startUnix = new DateTimeOffset(startUtc).ToUnixTimeSeconds();

        PrintJob existingExternalIdAbsentJob = new()
        {
            Id = Guid.NewGuid(),
            Name = "already-tracked",
            AssignedPrinterId = printerId,
            Status = PrintJobStatus.Completed,
            ActualStartTime = startUtc.AddTicks(TimeSpan.TicksPerMillisecond),
            ActualEndTime = startUtc.AddMinutes(10),
            CreatedAt = startUtc,
            UpdatedAt = startUtc.AddMinutes(10),
            QueuedAt = startUtc
        };

        await using AppDbContext db = new(options);
        db.Printers.Add(CreateEnabledPrinter(printerId, "External Id Absent Printer"));
        db.PrintJobs.Add(existingExternalIdAbsentJob);
        await db.SaveChangesAsync();

        HistoryListResponse historyResponse = new()
        {
            Count = 1,
            Jobs =
            [
                new HistoryJob
                {
                    JobId = "history-after-null-external",
                    Filename = "different-name.gcode",
                    Status = "completed",
                    StartTime = startUnix,
                    EndTime = startUnix + 600,
                    FilamentUsed = 300,
                    Metadata = []
                }
            ]
        };

        Mock<IPrintersService> printersService = CreateHistoryListMock(printerId, historyResponse);
        PrintJobManagementService service = CreateService(new EfPrintJobManagementRepository(db), printersService);

        await service.SeedHistoryFromPrintersAsync();

        List<PrintJob> storedJobs = await db.PrintJobs
            .Where(j => j.AssignedPrinterId == printerId)
            .ToListAsync();
        PrintJob storedJob = Assert.Single(storedJobs);
        Assert.Equal(existingExternalIdAbsentJob.Id, storedJob.Id);
        Assert.Null(storedJob.ExternalJobId);
        Assert.False(storedJob.WasSeededFromHistory);
    }

    [Fact]
    public async Task DeduplicateSeededHistoryAsync_TwoSeededJobsSamePrinterAndStart_RemovesNewerKeepsOldest()
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"HistoryDedupCleanup_{Guid.NewGuid():N}")
            .Options;

        Guid printerId = Guid.NewGuid();
        DateTime startUtc = TruncateToSecond(DateTime.UtcNow.AddMinutes(-30));

        PrintJob older = CreateSeededHistoryJob(printerId, "dup-old", startUtc, createdAt: startUtc);
        PrintJob newer = CreateSeededHistoryJob(printerId, "dup-new", startUtc, createdAt: startUtc.AddMinutes(5));

        await using AppDbContext db = new(options);
        db.Printers.Add(CreateEnabledPrinter(printerId, "Dedup Cleanup Printer"));
        db.PrintJobs.AddRange(older, newer);
        await db.SaveChangesAsync();

        PrintJobManagementService service = CreateService(new EfPrintJobManagementRepository(db), new Mock<IPrintersService>());

        DeduplicateHistoryResultDto result = await service.DeduplicateSeededHistoryAsync(dryRun: false);

        Assert.False(result.DryRun);
        Assert.Equal(1, result.DuplicateGroups);
        Assert.Equal(1, result.JobsRemoved);
        Assert.Contains(result.Groups, g => g.RetainedJobId == older.Id && g.RemovedJobIds.Contains(newer.Id));

        List<PrintJob> remaining = await db.PrintJobs.Where(j => j.AssignedPrinterId == printerId).ToListAsync();
        PrintJob survivor = Assert.Single(remaining);
        Assert.Equal(older.Id, survivor.Id);
    }

    [Fact]
    public async Task DeduplicateSeededHistoryAsync_DryRun_ReportsButRemovesNothing()
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"HistoryDedupDryRun_{Guid.NewGuid():N}")
            .Options;

        Guid printerId = Guid.NewGuid();
        DateTime startUtc = TruncateToSecond(DateTime.UtcNow.AddMinutes(-30));

        PrintJob older = CreateSeededHistoryJob(printerId, "dry-old", startUtc, createdAt: startUtc);
        PrintJob newer = CreateSeededHistoryJob(printerId, "dry-new", startUtc, createdAt: startUtc.AddMinutes(5));

        await using AppDbContext db = new(options);
        db.Printers.Add(CreateEnabledPrinter(printerId, "Dedup DryRun Printer"));
        db.PrintJobs.AddRange(older, newer);
        await db.SaveChangesAsync();

        PrintJobManagementService service = CreateService(new EfPrintJobManagementRepository(db), new Mock<IPrintersService>());

        DeduplicateHistoryResultDto result = await service.DeduplicateSeededHistoryAsync(dryRun: true);

        Assert.True(result.DryRun);
        Assert.Equal(1, result.DuplicateGroups);
        Assert.Equal(1, result.JobsRemoved);

        int remaining = await db.PrintJobs.CountAsync(j => j.AssignedPrinterId == printerId);
        Assert.Equal(2, remaining);
    }

    [Fact]
    public async Task DeduplicateSeededHistoryAsync_NativeAndSeededSameStart_KeepsNativeRemovesSeeded()
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"HistoryDedupNative_{Guid.NewGuid():N}")
            .Options;

        Guid printerId = Guid.NewGuid();
        DateTime startUtc = TruncateToSecond(DateTime.UtcNow.AddMinutes(-30));

        // The seeded row is created earlier than the native row to prove native is preferred
        // regardless of CreatedAt ordering.
        PrintJob seeded = CreateSeededHistoryJob(printerId, "ext-native-dup", startUtc, createdAt: startUtc.AddMinutes(-10));
        PrintJob native = CreateNativeJob(printerId, startUtc, createdAt: startUtc);

        await using AppDbContext db = new(options);
        db.Printers.Add(CreateEnabledPrinter(printerId, "Dedup Native Printer"));
        db.PrintJobs.AddRange(seeded, native);
        await db.SaveChangesAsync();

        PrintJobManagementService service = CreateService(new EfPrintJobManagementRepository(db), new Mock<IPrintersService>());

        DeduplicateHistoryResultDto result = await service.DeduplicateSeededHistoryAsync(dryRun: false);

        Assert.Equal(1, result.DuplicateGroups);
        Assert.Equal(1, result.JobsRemoved);
        Assert.Contains(result.Groups, g => g.RetainedJobId == native.Id && g.RemovedJobIds.Contains(seeded.Id));

        List<PrintJob> remaining = await db.PrintJobs.Where(j => j.AssignedPrinterId == printerId).ToListAsync();
        PrintJob survivor = Assert.Single(remaining);
        Assert.Equal(native.Id, survivor.Id);
        Assert.False(survivor.WasSeededFromHistory);
    }

    [Fact]
    public async Task DeduplicateSeededHistoryAsync_MissingStartTimes_AreNotTreatedAsDuplicates()
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"HistoryDedupEpoch_{Guid.NewGuid():N}")
            .Options;

        Guid printerId = Guid.NewGuid();

        // Start-less jobs map to the Unix epoch and must never be collapsed.
        PrintJob a = CreateSeededHistoryJob(printerId, "epoch-1", DateTime.UnixEpoch, createdAt: DateTime.UtcNow.AddMinutes(-20));
        PrintJob b = CreateSeededHistoryJob(printerId, "epoch-2", DateTime.UnixEpoch, createdAt: DateTime.UtcNow.AddMinutes(-19));

        await using AppDbContext db = new(options);
        db.Printers.Add(CreateEnabledPrinter(printerId, "Dedup Epoch Printer"));
        db.PrintJobs.AddRange(a, b);
        await db.SaveChangesAsync();

        PrintJobManagementService service = CreateService(new EfPrintJobManagementRepository(db), new Mock<IPrintersService>());

        DeduplicateHistoryResultDto result = await service.DeduplicateSeededHistoryAsync(dryRun: false);

        Assert.Equal(0, result.DuplicateGroups);
        Assert.Equal(0, result.JobsRemoved);
        Assert.Equal(2, await db.PrintJobs.CountAsync(j => j.AssignedPrinterId == printerId));
    }

    [Fact]
    public async Task DeduplicateSeededHistoryAsync_DistinctStartTimes_AreNotRemoved()
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"HistoryDedupDistinct_{Guid.NewGuid():N}")
            .Options;

        Guid printerId = Guid.NewGuid();
        DateTime startUtc = TruncateToSecond(DateTime.UtcNow.AddMinutes(-30));

        PrintJob first = CreateSeededHistoryJob(printerId, "distinct-1", startUtc, createdAt: startUtc);
        PrintJob second = CreateSeededHistoryJob(printerId, "distinct-2", startUtc.AddSeconds(30), createdAt: startUtc.AddSeconds(30));

        await using AppDbContext db = new(options);
        db.Printers.Add(CreateEnabledPrinter(printerId, "Dedup Distinct Printer"));
        db.PrintJobs.AddRange(first, second);
        await db.SaveChangesAsync();

        PrintJobManagementService service = CreateService(new EfPrintJobManagementRepository(db), new Mock<IPrintersService>());

        DeduplicateHistoryResultDto result = await service.DeduplicateSeededHistoryAsync(dryRun: false);

        Assert.Equal(0, result.DuplicateGroups);
        Assert.Equal(0, result.JobsRemoved);
        Assert.Equal(2, await db.PrintJobs.CountAsync(j => j.AssignedPrinterId == printerId));
    }

    [Fact]
    public async Task DeduplicateSeededHistoryAsync_ExternalPrintPlaceholderSurvivor_SkipsGroupToPreserveHistoryId()
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"HistoryDedupPlaceholder_{Guid.NewGuid():N}")
            .Options;

        Guid printerId = Guid.NewGuid();
        DateTime startUtc = TruncateToSecond(DateTime.UtcNow.AddMinutes(-30));

        // A native external-print placeholder (synthetic id, non-null SourcePrinterId) shares the
        // start-second with a seeded row carrying the real provider job id. The placeholder cannot be
        // relinked by a later harvest, so removing the seeded row would strand the real history id;
        // the group must be skipped and both rows retained.
        PrintJob placeholder = CreateExternalPrintPlaceholder(printerId, startUtc, createdAt: startUtc);
        PrintJob seeded = CreateSeededHistoryJob(printerId, "real-history-id", startUtc, createdAt: startUtc.AddMinutes(1));

        await using AppDbContext db = new(options);
        db.Printers.Add(CreateEnabledPrinter(printerId, "Dedup Placeholder Printer"));
        db.PrintJobs.AddRange(placeholder, seeded);
        await db.SaveChangesAsync();

        PrintJobManagementService service = CreateService(new EfPrintJobManagementRepository(db), new Mock<IPrintersService>());

        DeduplicateHistoryResultDto result = await service.DeduplicateSeededHistoryAsync(dryRun: false);

        Assert.Equal(0, result.DuplicateGroups);
        Assert.Equal(0, result.JobsRemoved);
        List<PrintJob> remaining = await db.PrintJobs.Where(j => j.AssignedPrinterId == printerId).ToListAsync();
        Assert.Equal(2, remaining.Count);
        Assert.Contains(remaining, j => j.Id == seeded.Id);
        Assert.Contains(remaining, j => j.Id == placeholder.Id);
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
        repository.Setup(r => r.GetActualStartTimesForPrinterAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => []);
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
        repository.Setup(r => r.GetActualStartTimesForPrinterAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => []);
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

    [Fact]
    public async Task SeedHistoryFromPrintersAsync_WhenSaveChangesHitsDuplicateConflict_DoesNotThrowOrAdvanceWatermark()
    {
        Guid printerId = Guid.NewGuid();
        DateTime startUtc = DateTime.UtcNow.AddMinutes(-8);
        long startUnix = new DateTimeOffset(startUtc).ToUnixTimeSeconds();

        Printer printer = new()
        {
            Id = printerId,
            Name = "Prusa Duplicate Conflict",
            Backend = (int)PrinterBackend.PrusaLink,
            IsEnabled = true,
            ServiceState = new PrinterServiceState { PrinterId = printerId, LastHistorySeedUtc = DateTime.UtcNow.AddHours(-1) }
        };

        HistoryListResponse historyResponse = new()
        {
            Count = 1,
            Jobs =
            [
                new HistoryJob
                {
                    JobId = "dup-overlap-1",
                    Filename = "dup-overlap.gcode",
                    Status = "completed",
                    StartTime = startUnix,
                    EndTime = startUnix + 120,
                    FilamentUsed = 320,
                    Metadata = []
                }
            ]
        };

        Mock<IPrintJobManagementRepository> repository = new();
        repository.Setup(r => r.GetEnabledPrintersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([printer]);
        repository.Setup(r => r.GetExternalJobIdsForPrinterAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        repository.Setup(r => r.GetActualStartTimesForPrinterAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => []);
        repository.Setup(r => r.GetByExternalIdAsync(printerId, "dup-overlap-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync((PrintJob?)null);
        repository.Setup(r => r.FindExistingJobForHistoryMatchAsync(
                printerId,
                "dup-overlap.gcode",
                It.IsAny<DateTime>(),
                It.IsAny<DateTime?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((PrintJob?)null);
        repository.Setup(r => r.FindGcodeFileByFilenameAsync("dup-overlap.gcode", It.IsAny<CancellationToken>()))
            .ReturnsAsync((GcodeFile?)null);
        repository.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DbUpdateException("SQLite Error 19: UNIQUE constraint failed: PrintJobs.ExternalJobId, PrintJobs.SourcePrinterId", innerException: null));

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

        await service.SeedHistoryFromPrintersAsync();

        repository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        repository.Verify(r => r.UpdatePrinterLastHistorySeedAsync(printerId, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SeedHistoryFromPrintersAsync_WhenSaveChangesHitsUnknownDbUpdateException_Rethrows()
    {
        Guid printerId = Guid.NewGuid();
        DateTime startUtc = DateTime.UtcNow.AddMinutes(-8);
        long startUnix = new DateTimeOffset(startUtc).ToUnixTimeSeconds();

        Printer printer = new()
        {
            Id = printerId,
            Name = "Prusa Unknown Db Conflict",
            Backend = (int)PrinterBackend.PrusaLink,
            IsEnabled = true,
            ServiceState = new PrinterServiceState { PrinterId = printerId, LastHistorySeedUtc = DateTime.UtcNow.AddHours(-1) }
        };

        HistoryListResponse historyResponse = new()
        {
            Count = 1,
            Jobs =
            [
                new HistoryJob
                {
                    JobId = "unknown-db-error-1",
                    Filename = "unknown-db-error.gcode",
                    Status = "completed",
                    StartTime = startUnix,
                    EndTime = startUnix + 120,
                    FilamentUsed = 320,
                    Metadata = []
                }
            ]
        };

        Mock<IPrintJobManagementRepository> repository = new();
        repository.Setup(r => r.GetEnabledPrintersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([printer]);
        repository.Setup(r => r.GetExternalJobIdsForPrinterAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        repository.Setup(r => r.GetActualStartTimesForPrinterAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => []);
        repository.Setup(r => r.GetByExternalIdAsync(printerId, "unknown-db-error-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync((PrintJob?)null);
        repository.Setup(r => r.FindExistingJobForHistoryMatchAsync(
                printerId,
                "unknown-db-error.gcode",
                It.IsAny<DateTime>(),
                It.IsAny<DateTime?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((PrintJob?)null);
        repository.Setup(r => r.FindGcodeFileByFilenameAsync("unknown-db-error.gcode", It.IsAny<CancellationToken>()))
            .ReturnsAsync((GcodeFile?)null);
        repository.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DbUpdateException("deadlock detected", innerException: null));

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

        await Assert.ThrowsAsync<DbUpdateException>(() => service.SeedHistoryFromPrintersAsync());

        repository.Verify(r => r.ClearTrackedChanges(), Times.Never);
        repository.Verify(r => r.UpdatePrinterLastHistorySeedAsync(printerId, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SeedHistoryFromPrintersAsync_WhenFirstPrinterSaveConflicts_SecondPrinterStillSavesSuccessfully()
    {
        Guid firstPrinterId = Guid.NewGuid();
        Guid secondPrinterId = Guid.NewGuid();
        DateTime startUtc = DateTime.UtcNow.AddMinutes(-8);
        long startUnix = new DateTimeOffset(startUtc).ToUnixTimeSeconds();

        Printer firstPrinter = new()
        {
            Id = firstPrinterId,
            Name = "Prusa Duplicate First",
            Backend = (int)PrinterBackend.PrusaLink,
            IsEnabled = true,
            ServiceState = new PrinterServiceState { PrinterId = firstPrinterId, LastHistorySeedUtc = DateTime.UtcNow.AddHours(-1) }
        };

        Printer secondPrinter = new()
        {
            Id = secondPrinterId,
            Name = "Prusa Healthy Second",
            Backend = (int)PrinterBackend.PrusaLink,
            IsEnabled = true,
            ServiceState = new PrinterServiceState { PrinterId = secondPrinterId, LastHistorySeedUtc = DateTime.UtcNow.AddHours(-1) }
        };

        HistoryListResponse firstHistoryResponse = new()
        {
            Count = 1,
            Jobs =
            [
                new HistoryJob
                {
                    JobId = "dup-first-1",
                    Filename = "dup-first.gcode",
                    Status = "completed",
                    StartTime = startUnix,
                    EndTime = startUnix + 120,
                    FilamentUsed = 320,
                    Metadata = []
                }
            ]
        };

        HistoryListResponse secondHistoryResponse = new()
        {
            Count = 1,
            Jobs =
            [
                new HistoryJob
                {
                    JobId = "ok-second-1",
                    Filename = "ok-second.gcode",
                    Status = "completed",
                    StartTime = startUnix,
                    EndTime = startUnix + 180,
                    FilamentUsed = 210,
                    Metadata = []
                }
            ]
        };

        Mock<IPrintJobManagementRepository> repository = new();
        repository.Setup(r => r.GetEnabledPrintersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([firstPrinter, secondPrinter]);
        repository.Setup(r => r.GetExternalJobIdsForPrinterAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        repository.Setup(r => r.GetActualStartTimesForPrinterAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => []);
        repository.Setup(r => r.GetByExternalIdAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PrintJob?)null);
        repository.Setup(r => r.FindExistingJobForHistoryMatchAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<DateTime>(),
                It.IsAny<DateTime?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((PrintJob?)null);
        repository.Setup(r => r.FindGcodeFileByFilenameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((GcodeFile?)null);

        int saveCalls = 0;
        repository.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Returns<CancellationToken>(_ =>
            {
                saveCalls++;
                if (saveCalls == 1)
                {
                    throw new DbUpdateException("SQLite Error 19: UNIQUE constraint failed: PrintJobs.ExternalJobId, PrintJobs.SourcePrinterId", innerException: null);
                }

                return Task.CompletedTask;
            });

        repository.Setup(r => r.UpdatePrinterLastHistorySeedAsync(secondPrinterId, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        Mock<IPrintersService> printersService = new();
        printersService.Setup(p => p.GetHistoryListAsync(
                firstPrinterId,
                1000,
                0,
                It.IsAny<DateTime?>(),
                null,
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(firstHistoryResponse);
        printersService.Setup(p => p.GetHistoryListAsync(
                secondPrinterId,
                1000,
                0,
                It.IsAny<DateTime?>(),
                null,
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(secondHistoryResponse);

        PrintJobManagementService service = CreateService(repository, printersService);

        await service.SeedHistoryFromPrintersAsync();

        repository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
        repository.Verify(r => r.ClearTrackedChanges(), Times.Once);
        repository.Verify(r => r.UpdatePrinterLastHistorySeedAsync(firstPrinterId, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
        repository.Verify(r => r.UpdatePrinterLastHistorySeedAsync(secondPrinterId, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SeedHistoryFromPrintersAsync_WhenSamePrinterSyncOverlaps_SerializesExecutionPerPrinter()
    {
        Guid printerId = Guid.NewGuid();
        DateTime startUtc = DateTime.UtcNow.AddMinutes(-3);
        long startUnix = new DateTimeOffset(startUtc).ToUnixTimeSeconds();

        Printer printer = new()
        {
            Id = printerId,
            Name = "Prusa Overlap Serialization",
            Backend = (int)PrinterBackend.PrusaLink,
            IsEnabled = true,
            ServiceState = new PrinterServiceState { PrinterId = printerId, LastHistorySeedUtc = DateTime.UtcNow.AddHours(-1) }
        };

        HistoryListResponse historyResponse = new()
        {
            Count = 1,
            Jobs =
            [
                new HistoryJob
                {
                    JobId = "overlap-serial-1",
                    Filename = "overlap-serial.gcode",
                    Status = "completed",
                    StartTime = startUnix,
                    EndTime = startUnix + 60,
                    FilamentUsed = 100,
                    Metadata = []
                }
            ]
        };

        Mock<IPrintJobManagementRepository> repository = new();
        repository.Setup(r => r.GetEnabledPrintersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([printer]);
        repository.Setup(r => r.GetExternalJobIdsForPrinterAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        repository.Setup(r => r.GetActualStartTimesForPrinterAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => []);
        repository.Setup(r => r.GetByExternalIdAsync(printerId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PrintJob?)null);
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

        int activeHistoryCalls = 0;
        int maxConcurrentHistoryCalls = 0;

        Mock<IPrintersService> printersService = new();
        printersService.Setup(p => p.GetHistoryListAsync(
                printerId,
                1000,
                0,
                It.IsAny<DateTime?>(),
                null,
                null,
                It.IsAny<CancellationToken>()))
            .Returns<Guid, int?, int?, DateTime?, DateTime?, string?, CancellationToken>(
                async (_, _, _, _, _, _, ct) =>
                {
                    int nowActive = Interlocked.Increment(ref activeHistoryCalls);
                    maxConcurrentHistoryCalls = Math.Max(maxConcurrentHistoryCalls, nowActive);

                    try
                    {
                        await Task.Delay(50, ct);
                        return historyResponse;
                    }
                    finally
                    {
                        Interlocked.Decrement(ref activeHistoryCalls);
                    }
                });

        PrintJobManagementService service = CreateService(repository, printersService);

        await Task.WhenAll(
            service.SeedHistoryFromPrintersAsync(),
            service.SeedHistoryFromPrintersAsync());

        repository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.AtLeastOnce());
        Assert.Equal(1, maxConcurrentHistoryCalls);
    }

    [Fact]
    public async Task SeedHistoryFromPrintersAsync_WithUnknownExternalStatus_MapsToQueued()
    {
        Guid printerId = Guid.NewGuid();
        DateTime startUtc = DateTime.UtcNow.AddMinutes(-12);
        long startUnix = new DateTimeOffset(startUtc).ToUnixTimeSeconds();

        Printer printer = new()
        {
            Id = printerId,
            Name = "Prusa Unknown Status",
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
                    JobId = "unknown-state-1",
                    Filename = "unknown-state.gcode",
                    Status = "mystery_external_state",
                    StartTime = startUnix,
                    EndTime = null,
                    FilamentUsed = 120,
                    Metadata = []
                }
            ]
        };

        Mock<IPrintJobManagementRepository> repository = new();
        repository.Setup(r => r.GetEnabledPrintersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([printer]);
        repository.Setup(r => r.GetExternalJobIdsForPrinterAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        repository.Setup(r => r.GetActualStartTimesForPrinterAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => []);
        repository.Setup(r => r.GetByExternalIdAsync(printerId, "unknown-state-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync((PrintJob?)null);
        repository.Setup(r => r.FindExistingJobForHistoryMatchAsync(
                printerId,
                "unknown-state.gcode",
                It.IsAny<DateTime>(),
                It.IsAny<DateTime?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((PrintJob?)null);
        repository.Setup(r => r.FindGcodeFileByFilenameAsync("unknown-state.gcode", It.IsAny<CancellationToken>()))
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
                10000,
                0,
                null,
                null,
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(historyResponse);

        PrintJobManagementService service = CreateService(repository, printersService);

        await service.SeedHistoryFromPrintersAsync();

        Assert.NotNull(addedJob);
        Assert.Equal(PrintJobStatus.Queued, addedJob!.Status);
    }

    [Fact]
    public async Task SeedHistoryFromPrintersAsync_WhenWaitIsCanceledBeforeSemaphoreAcquired_DoesNotLeakLockReference()
    {
        Guid printerId = Guid.NewGuid();

        Printer printer = new()
        {
            Id = printerId,
            Name = "Prusa Canceled Wait",
            Backend = (int)PrinterBackend.PrusaLink,
            IsEnabled = true,
            ServiceState = new PrinterServiceState { PrinterId = printerId, LastHistorySeedUtc = DateTime.UtcNow.AddHours(-1) }
        };

        Mock<IPrintJobManagementRepository> repository = new();
        repository.Setup(r => r.GetEnabledPrintersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([printer]);

        Mock<IPrintersService> printersService = new();
        PrintJobManagementService service = CreateService(repository, printersService);

        object lockState = AcquirePrinterLockStateReference(printerId);
        SemaphoreSlim semaphore = GetSemaphore(lockState);

        await semaphore.WaitAsync();
        try
        {
            using CancellationTokenSource cts = new();
            Task run = service.SeedHistoryFromPrintersAsync(cancellationToken: cts.Token);

            bool referencedByPendingRun = SpinWait.SpinUntil(
                () => GetReferenceCount(lockState) >= 2,
                millisecondsTimeout: 1000);

            Assert.True(referencedByPendingRun);

            cts.Cancel();
            await run;

            // One reference remains here: the manual acquisition in this test.
            Assert.Equal(1, GetReferenceCount(lockState));
        }
        finally
        {
            semaphore.Release();
            ReleaseLockStateReference(lockState);
        }

        int referenceCount = GetReferenceCount(lockState);
        Assert.Equal(0, referenceCount);
    }

    private static object AcquirePrinterLockStateReference(Guid printerId)
    {
        Type serviceType = typeof(PrintJobManagementService);
        MethodInfo? acquireMethod = serviceType.GetMethod("AcquirePrinterHistorySyncLock", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(acquireMethod);

        object? lockState = acquireMethod!.Invoke(null, [printerId]);
        Assert.NotNull(lockState);
        return lockState!;
    }

    private static void ReleaseLockStateReference(object lockState)
    {
        Type stateType = lockState.GetType();
        MethodInfo? releaseMethod = stateType.GetMethod("ReleaseReferenceAndMarkUsed", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(releaseMethod);

        _ = releaseMethod!.Invoke(lockState, null);
    }

    private static SemaphoreSlim GetSemaphore(object lockState)
    {
        Type stateType = lockState.GetType();
        PropertyInfo? semaphoreProperty = stateType.GetProperty("Semaphore", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(semaphoreProperty);

        object? semaphore = semaphoreProperty!.GetValue(lockState);
        Assert.NotNull(semaphore);
        return Assert.IsType<SemaphoreSlim>(semaphore);
    }

    private static int GetReferenceCount(object lockState)
    {
        Type stateType = lockState.GetType();
        PropertyInfo? referenceCountProperty = stateType.GetProperty("ReferenceCount", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(referenceCountProperty);

        object? value = referenceCountProperty!.GetValue(lockState);
        Assert.NotNull(value);
        return Assert.IsType<int>(value);
    }

    [Fact]
    public async Task GetQueueHistoryAsync_SeededJobWithoutSpool_ReportsEstimatedCostAndAggregateFilament()
    {
        // Seeded-from-history job: aggregate actual filament only, no spool association → estimated cost.
        PrintJob seeded = new()
        {
            Id = Guid.NewGuid(),
            Name = "seeded.gcode",
            Status = PrintJobStatus.Completed,
            WasSeededFromHistory = true,
            ActualFilamentUsage = 156.8,
            MaterialCostUsd = 3.14m,
            TotalCostUsd = 4.50m,
            SpoolmanSpoolId = null,
            SpoolmanFilamentId = null
        };

        // Native job with an associated Spoolman spool and per-toolhead usage → actual cost.
        PrintJob nativeWithSpool = new()
        {
            Id = Guid.NewGuid(),
            Name = "native.gcode",
            Status = PrintJobStatus.Completed,
            WasSeededFromHistory = false,
            SpoolmanSpoolId = 42,
            MaterialCostUsd = 2.19m,
            TotalCostUsd = 2.50m,
            ToolheadUsages =
            [
                new PrintJobToolheadUsage
                {
                    Id = Guid.NewGuid(),
                    ToolheadIndex = 0,
                    SpoolmanSpoolId = 42,
                    FilamentUsageGrams = 87.5,
                    MaterialCostUsd = 2.19m
                }
            ]
        };

        Mock<IPrintJobManagementRepository> repository = new();
        repository.Setup(r => r.GetHistoryAsync(
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<string>(),
                It.IsAny<List<string>?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(([seeded, nativeWithSpool], 2, 2, 0, 0, 1200L));

        PrintJobManagementService service = CreateService(repository, new Mock<IPrintersService>());

        QueueHistoryPageDto page = await service.GetQueueHistoryAsync();

        QueueHistoryEntryDto seededDto = page.Entries.Single(e => e.Id == seeded.Id.ToString());
        Assert.True(seededDto.CostIsEstimated);
        Assert.Equal(156.8, seededDto.ActualFilamentUsageGrams);
        Assert.Equal(3.14m, seededDto.MaterialCostUsd);
        Assert.Equal(4.50m, seededDto.TotalCostUsd);
        Assert.Empty(seededDto.ToolheadUsages);

        QueueHistoryEntryDto nativeDto = page.Entries.Single(e => e.Id == nativeWithSpool.Id.ToString());
        Assert.False(nativeDto.CostIsEstimated);
        Assert.Equal(2.19m, nativeDto.MaterialCostUsd);
    }

    private static PrintJobManagementService CreateService(
        Mock<IPrintJobManagementRepository> repository,
        Mock<IPrintersService> printersService)
    {
        return CreateService(repository.Object, printersService);
    }

    private static PrintJobManagementService CreateService(
        IPrintJobManagementRepository repository,
        Mock<IPrintersService> printersService)
    {
        return new PrintJobManagementService(
            repository,
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

    private static Mock<IPrintersService> CreateHistoryListMock(Guid printerId, HistoryListResponse historyResponse)
    {
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

        return printersService;
    }

    private static Printer CreateEnabledPrinter(Guid printerId, string name)
    {
        return new Printer
        {
            Id = printerId,
            Name = name,
            ServerUrl = "http://printer.local",
            BackendPort = 80,
            Backend = (int)PrinterBackend.PrusaLink,
            IsEnabled = true,
            ManufacturerId = Guid.NewGuid(),
            ModelId = Guid.NewGuid()
        };
    }

    private static PrintJob CreateSeededHistoryJob(Guid printerId, string externalJobId, DateTime startUtc, DateTime createdAt)
    {
        return new PrintJob
        {
            Id = Guid.NewGuid(),
            Name = externalJobId,
            Status = PrintJobStatus.Completed,
            ActualStartTime = startUtc,
            ActualEndTime = startUtc.AddMinutes(10),
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
            QueuedAt = createdAt,
            ExternalJobId = externalJobId,
            SourcePrinterId = printerId,
            AssignedPrinterId = printerId,
            WasSeededFromHistory = true
        };
    }

    private static PrintJob CreateNativeJob(Guid printerId, DateTime startUtc, DateTime createdAt)
    {
        return new PrintJob
        {
            Id = Guid.NewGuid(),
            Name = "native-print",
            Status = PrintJobStatus.Completed,
            ActualStartTime = startUtc,
            ActualEndTime = startUtc.AddMinutes(10),
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
            QueuedAt = createdAt,
            AssignedPrinterId = printerId,
            WasSeededFromHistory = false
        };
    }

    private static PrintJob CreateExternalPrintPlaceholder(Guid printerId, DateTime startUtc, DateTime createdAt)
    {
        // Mirrors PrintJobCompletionService's external-print placeholder: a native (non-seeded) row
        // with a synthetic ExternalJobId and a SourcePrinterId, which a later harvest cannot relink.
        return new PrintJob
        {
            Id = Guid.NewGuid(),
            Name = "External Print",
            Status = PrintJobStatus.Completed,
            ActualStartTime = startUtc,
            ActualEndTime = startUtc.AddMinutes(10),
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
            QueuedAt = createdAt,
            AssignedPrinterId = printerId,
            SourcePrinterId = printerId,
            IsExternalPrint = true,
            ExternalJobId = $"ext-{printerId:N}-{startUtc:yyyyMMddHHmmss}",
            WasSeededFromHistory = false
        };
    }

    private static DateTime TruncateToSecond(DateTime value)
    {
        return value.AddTicks(-(value.Ticks % TimeSpan.TicksPerSecond));
    }
}
