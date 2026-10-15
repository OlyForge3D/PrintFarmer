using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.SignalR;
using Farm.Web.Api.Tests.TestInfrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services;

public sealed class PrintJobCompletionOccupancyTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;

    public PrintJobCompletionOccupancyTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        TestSqlitePragmaEnforcer.EnsureForeignKeysEnabled(_connection);

        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;
        _db = new AppDbContext(options);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task EnsureExternalPrintJobExistsAsync_WhenPrinterHasPausedJob_DoesNotCreateSecondJob()
    {
        Printer printer = await CreatePrinterAsync();
        _db.PrintJobs.Add(new PrintJob
        {
            Id = Guid.NewGuid(),
            Name = "Paused Print",
            AssignedPrinterId = printer.Id,
            Status = PrintJobStatus.Paused,
            QueuedAt = DateTime.UtcNow,
            ActualStartTime = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();
        PrintJobCompletionService service = new(
            _db,
            Mock.Of<IHubContext<PrinterHub>>(),
            NullLogger<PrintJobCompletionService>.Instance);

        bool created = await service.EnsureExternalPrintJobExistsAsync(
            printer.Id,
            "external.gcode");

        created.Should().BeFalse();
        List<PrintJob> jobs = await _db.PrintJobs
            .Where(job => job.AssignedPrinterId == printer.Id)
            .ToListAsync();
        jobs.Should().ContainSingle();
        jobs[0].Status.Should().Be(PrintJobStatus.Paused);
        jobs[0].IsExternalPrint.Should().BeFalse();
    }

    [Fact]
    public async Task MarkCurrentJobAsCompletedAsync_WhenExternalJobFinishes_ClearsActiveExternalMarker()
    {
        Printer printer = await CreatePrinterAsync();
        PrintJobCompletionService service = CreateService();

        bool created = await service.EnsureExternalPrintJobExistsAsync(
            printer.Id,
            "external-100.gcode");

        created.Should().BeTrue();

        bool completed = await service.MarkCurrentJobAsCompletedAsync(
            printer.Id,
            "complete",
            new PrinterTerminalObservation("external-100.gcode"));

        completed.Should().BeTrue();
        PrintJob job = await _db.PrintJobs.SingleAsync(j => j.AssignedPrinterId == printer.Id);
        job.Status.Should().Be(PrintJobStatus.Completed);
        job.ActualEndTime.Should().NotBeNull();
        job.ActiveExternalPrinterId.Should().BeNull();
    }

    [Fact]
    public async Task SyncOrphanedPrintingJobsAsync_WhenExternalJobPrinterIsIdle_CompletesJob()
    {
        Printer printer = await CreatePrinterAsync();
        DateTime now = DateTime.UtcNow;
        PrintJob job = new()
        {
            Id = Guid.NewGuid(),
            Name = "idle-external.gcode",
            AssignedPrinterId = printer.Id,
            SourcePrinterId = printer.Id,
            Status = PrintJobStatus.Printing,
            ActualStartTime = now.AddMinutes(-30),
            CreatedAt = now.AddMinutes(-30),
            UpdatedAt = now.AddMinutes(-30),
            QueuedAt = now.AddMinutes(-30),
            IsExternalPrint = true,
            ActiveExternalPrinterId = printer.Id,
            ExternalJobId = $"ext-{printer.Id:N}-idle",
        };
        _db.PrintJobs.Add(job);
        await _db.SaveChangesAsync();
        PrintJobCompletionService service = CreateService();

        int synced = await service.SyncOrphanedPrintingJobsAsync(
            id => id == printer.Id ? FreshSnapshot(printer.Id, "idle") : null,
            "system");

        synced.Should().Be(1);
        job.Status.Should().Be(PrintJobStatus.Completed);
        job.ActualEndTime.Should().NotBeNull();
        job.ActiveExternalPrinterId.Should().BeNull();
    }

    [Fact]
    public async Task SyncOrphanedPrintingJobsAsync_WhenSeededHistoryJobPrinterIsReady_CompletesJob()
    {
        Printer printer = await CreatePrinterAsync();
        DateTime now = DateTime.UtcNow;
        PrintJob job = new()
        {
            Id = Guid.NewGuid(),
            Name = "history-started",
            AssignedPrinterId = printer.Id,
            SourcePrinterId = printer.Id,
            Status = PrintJobStatus.Printing,
            ActualStartTime = now.AddMinutes(-45),
            CreatedAt = now.AddMinutes(-45),
            UpdatedAt = now.AddMinutes(-45),
            QueuedAt = now.AddMinutes(-45),
            WasSeededFromHistory = true,
            ExternalJobId = "history-active-1",
        };
        _db.PrintJobs.Add(job);
        await _db.SaveChangesAsync();
        PrintJobCompletionService service = CreateService();

        int synced = await service.SyncOrphanedPrintingJobsAsync(
            id => id == printer.Id ? FreshSnapshot(printer.Id, "ready") : null,
            "system");

        synced.Should().Be(1);
        job.Status.Should().Be(PrintJobStatus.Completed);
        job.ActualEndTime.Should().NotBeNull();
    }

    [Fact]
    public async Task SyncOrphanedPrintingJobsAsync_WhenExternalIdleSnapshotIsStale_DoesNotCompleteJob()
    {
        Printer printer = await CreatePrinterAsync();
        DateTime now = DateTime.UtcNow;
        PrintJob job = new()
        {
            Id = Guid.NewGuid(),
            Name = "stale-idle-external.gcode",
            AssignedPrinterId = printer.Id,
            SourcePrinterId = printer.Id,
            Status = PrintJobStatus.Printing,
            ActualStartTime = now.AddMinutes(-30),
            CreatedAt = now.AddMinutes(-30),
            UpdatedAt = now.AddMinutes(-30),
            QueuedAt = now.AddMinutes(-30),
            IsExternalPrint = true,
            ActiveExternalPrinterId = printer.Id,
            ExternalJobId = $"ext-{printer.Id:N}-stale-idle",
        };
        _db.PrintJobs.Add(job);
        await _db.SaveChangesAsync();
        PrintJobCompletionService service = CreateService();

        int synced = await service.SyncOrphanedPrintingJobsAsync(
            id => id == printer.Id ? StaleSnapshot(printer.Id, "idle") : null,
            "system");

        synced.Should().Be(0);
        job.Status.Should().Be(PrintJobStatus.Printing);
        job.ActualEndTime.Should().BeNull();
        job.ActiveExternalPrinterId.Should().Be(printer.Id);
    }

    [Fact]
    public async Task SyncOrphanedPrintingJobsAsync_WhenExternalJobPrinterIsOffline_DoesNotFailJob()
    {
        Printer printer = await CreatePrinterAsync();
        DateTime now = DateTime.UtcNow;
        PrintJob job = new()
        {
            Id = Guid.NewGuid(),
            Name = "offline-external.gcode",
            AssignedPrinterId = printer.Id,
            SourcePrinterId = printer.Id,
            Status = PrintJobStatus.Printing,
            ActualStartTime = now.AddMinutes(-30),
            CreatedAt = now.AddMinutes(-30),
            UpdatedAt = now.AddMinutes(-30),
            QueuedAt = now.AddMinutes(-30),
            IsExternalPrint = true,
            ActiveExternalPrinterId = printer.Id,
            ExternalJobId = $"ext-{printer.Id:N}-offline",
        };
        _db.PrintJobs.Add(job);
        await _db.SaveChangesAsync();
        PrintJobCompletionService service = CreateService();

        int synced = await service.SyncOrphanedPrintingJobsAsync(
            id => id == printer.Id ? FreshSnapshot(printer.Id, "offline", isOnline: false) : null,
            "system");

        synced.Should().Be(0);
        job.Status.Should().Be(PrintJobStatus.Printing);
        job.ActualEndTime.Should().BeNull();
        job.ActiveExternalPrinterId.Should().Be(printer.Id);
    }

    private PrintJobCompletionService CreateService() =>
        new(
            _db,
            Mock.Of<IHubContext<PrinterHub>>(),
            NullLogger<PrintJobCompletionService>.Instance);

    private static PrinterStatusCacheSnapshot FreshSnapshot(
        Guid printerId,
        string state,
        bool isOnline = true) =>
        new(
            new PrinterStatusDto(printerId, isOnline, state),
            DateTime.UtcNow,
            LastSeenAtUtc: DateTime.UtcNow);

    private static PrinterStatusCacheSnapshot StaleSnapshot(Guid printerId, string state) =>
        new(
            new PrinterStatusDto(printerId, true, state),
            DateTime.UtcNow.AddMinutes(-10),
            LastSeenAtUtc: DateTime.UtcNow.AddMinutes(-10));

    private async Task<Printer> CreatePrinterAsync()
    {
        Manufacturer manufacturer = new()
        {
            Id = Guid.NewGuid(),
            Name = "Test Manufacturer",
        };
        PrinterModel model = new()
        {
            Id = Guid.NewGuid(),
            Name = "Test Model",
            ManufacturerId = manufacturer.Id,
        };
        Printer printer = new()
        {
            Id = Guid.NewGuid(),
            Name = "Completion Occupancy Test Printer",
            ServerUrl = $"http://completion-occupancy-{Guid.NewGuid():N}.local",
            BackendPort = 7125,
            Backend = (int)PrinterBackend.Moonraker,
            ManufacturerId = manufacturer.Id,
            ModelId = model.Id,
            IsEnabled = true,
            IsAvailable = true,
        };

        _db.Manufacturers.Add(manufacturer);
        _db.PrinterModels.Add(model);
        _db.Printers.Add(printer);
        await _db.SaveChangesAsync();
        return printer;
    }
}
