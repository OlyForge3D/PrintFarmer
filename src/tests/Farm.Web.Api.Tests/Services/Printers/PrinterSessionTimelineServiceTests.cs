using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Tests.Services.Printers;

public sealed class PrinterSessionTimelineServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _dbContextOptions;

    public PrinterSessionTimelineServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _dbContextOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        using AppDbContext dbContext = CreateDbContext();
        _ = dbContext.Database.EnsureCreated();
    }

    [Fact]
    public async Task GetRecentAsync_WhenPrinterHasTimelineData_ComposesSessionEvents()
    {
        using AppDbContext dbContext = CreateDbContext();
        Printer printer = await SeedPrinterAsync(dbContext, "Timeline Printer");
        PrintJob job = await SeedJobAsync(
            dbContext,
            printer.Id,
            PrintJobStatus.Failed,
            queuedAt: new DateTime(2026, 3, 27, 8, 0, 0, DateTimeKind.Utc),
            dispatchedAt: new DateTime(2026, 3, 27, 8, 5, 0, DateTimeKind.Utc),
            startedAt: new DateTime(2026, 3, 27, 8, 6, 0, DateTimeKind.Utc),
            endedAt: new DateTime(2026, 3, 27, 8, 45, 0, DateTimeKind.Utc),
            failureReason: "Spaghetti detected",
            name: "jobs/demo-print.gcode");

        dbContext.JobStateHistories.Add(new JobStateHistory
        {
            Id = Guid.NewGuid(),
            JobId = job.Id,
            FromState = "Printing",
            ToState = "Paused",
            TransitionedAtUtc = new DateTime(2026, 3, 27, 8, 20, 0, DateTimeKind.Utc),
            DurationInState = TimeSpan.FromMinutes(14),
            Notes = "Operator paused for inspection",
            CreatedAt = new DateTime(2026, 3, 27, 8, 20, 0, DateTimeKind.Utc),
        });
        dbContext.FailureDetectionIncidents.Add(new FailureDetectionIncident
        {
            Id = Guid.NewGuid(),
            PrinterId = printer.Id,
            JobId = job.Id,
            JobName = "jobs/demo-print.gcode",
            FileName = "demo-print.gcode",
            Confidence = 0.973m,
            DetectedAt = new DateTime(2026, 3, 27, 8, 18, 0, DateTimeKind.Utc),
            SnapshotUrl = "http://camera.local/incident.jpg",
            AutoPaused = true,
        });
        await dbContext.SaveChangesAsync();

        PrinterSessionTimelineService service = new(dbContext);

        PrinterSessionTimelineDto timeline = await service.GetRecentAsync(printer.Id, take: 10, CancellationToken.None);

        timeline.PrinterId.Should().Be(printer.Id);
        timeline.PrinterName.Should().Be("Timeline Printer");
        timeline.Sessions.Should().ContainSingle();

        PrinterSessionTimelineSessionDto session = timeline.Sessions[0];
        session.JobId.Should().Be(job.Id);
        session.JobName.Should().Be("jobs/demo-print.gcode");
        session.FileName.Should().Be("demo-print.gcode");
        session.Status.Should().Be(PrintJobStatus.Failed);
        session.HasFailureIncident.Should().BeTrue();
        session.FailureIncidentCount.Should().Be(1);
        session.Events.Select(@event => @event.Type).Should().ContainInOrder(
            PrinterSessionTimelineEventType.Queued,
            PrinterSessionTimelineEventType.Dispatched,
            PrinterSessionTimelineEventType.SessionStarted,
            PrinterSessionTimelineEventType.FailureDetected,
            PrinterSessionTimelineEventType.StateTransition,
            PrinterSessionTimelineEventType.SessionEnded);
        session.Events.Single(@event => @event.Type == PrinterSessionTimelineEventType.FailureDetected).AutoPaused.Should().BeTrue();
        session.Events.Single(@event => @event.Type == PrinterSessionTimelineEventType.StateTransition).ToState.Should().Be("Paused");
        session.Events[^1].Notes.Should().Be("Spaghetti detected");
    }

    [Fact]
    public async Task GetRecentAsync_WhenIncidentLacksJobId_AttachesIncidentBySessionWindow()
    {
        using AppDbContext dbContext = CreateDbContext();
        Printer printer = await SeedPrinterAsync(dbContext, "Window Printer");
        PrintJob job = await SeedJobAsync(
            dbContext,
            printer.Id,
            PrintJobStatus.Completed,
            queuedAt: new DateTime(2026, 3, 27, 9, 0, 0, DateTimeKind.Utc),
            dispatchedAt: new DateTime(2026, 3, 27, 9, 2, 0, DateTimeKind.Utc),
            startedAt: new DateTime(2026, 3, 27, 9, 3, 0, DateTimeKind.Utc),
            endedAt: new DateTime(2026, 3, 27, 9, 40, 0, DateTimeKind.Utc),
            failureReason: null,
            name: "jobs/window-print.gcode");

        dbContext.FailureDetectionIncidents.Add(new FailureDetectionIncident
        {
            Id = Guid.NewGuid(),
            PrinterId = printer.Id,
            JobId = null,
            JobName = "jobs/window-print.gcode",
            FileName = "window-print.gcode",
            Confidence = 0.801m,
            DetectedAt = new DateTime(2026, 3, 27, 9, 10, 0, DateTimeKind.Utc),
            AutoPaused = false,
        });
        await dbContext.SaveChangesAsync();

        PrinterSessionTimelineService service = new(dbContext);

        PrinterSessionTimelineDto timeline = await service.GetRecentAsync(printer.Id, take: 10, CancellationToken.None);

        timeline.Sessions.Should().ContainSingle();
        timeline.Sessions[0].FailureIncidentCount.Should().Be(1);
        timeline.Sessions[0].Events.Should().Contain(@event =>
            @event.Type == PrinterSessionTimelineEventType.FailureDetected &&
            @event.Confidence == 0.801m);
        timeline.Sessions[0].FileName.Should().Be("window-print.gcode");
        job.Id.Should().NotBeEmpty();
    }

    [Fact]
    public async Task GetRecentAsync_WhenPrinterDoesNotExist_ThrowsKeyNotFoundException()
    {
        using AppDbContext dbContext = CreateDbContext();
        PrinterSessionTimelineService service = new(dbContext);

        Func<Task> action = async () => await service.GetRecentAsync(Guid.NewGuid(), take: 10, CancellationToken.None);

        await action.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task GetRecentAsync_WhenMultipleSessionsExist_RespectsTakeAndReturnsNewestSessionFirst()
    {
        using AppDbContext dbContext = CreateDbContext();
        Printer printer = await SeedPrinterAsync(dbContext, "Ordered Printer");

        PrintJob olderJob = await SeedJobAsync(
            dbContext,
            printer.Id,
            PrintJobStatus.Completed,
            queuedAt: new DateTime(2026, 3, 27, 6, 0, 0, DateTimeKind.Utc),
            dispatchedAt: new DateTime(2026, 3, 27, 6, 2, 0, DateTimeKind.Utc),
            startedAt: new DateTime(2026, 3, 27, 6, 5, 0, DateTimeKind.Utc),
            endedAt: new DateTime(2026, 3, 27, 6, 35, 0, DateTimeKind.Utc),
            failureReason: null,
            name: "jobs/older-session.gcode");

        PrintJob newerJob = await SeedJobAsync(
            dbContext,
            printer.Id,
            PrintJobStatus.Failed,
            queuedAt: new DateTime(2026, 3, 27, 8, 0, 0, DateTimeKind.Utc),
            dispatchedAt: new DateTime(2026, 3, 27, 8, 3, 0, DateTimeKind.Utc),
            startedAt: new DateTime(2026, 3, 27, 8, 5, 0, DateTimeKind.Utc),
            endedAt: new DateTime(2026, 3, 27, 8, 25, 0, DateTimeKind.Utc),
            failureReason: "Monitoring aborted the print",
            name: "jobs/newer-session.gcode");

        dbContext.FailureDetectionIncidents.Add(new FailureDetectionIncident
        {
            Id = Guid.NewGuid(),
            PrinterId = printer.Id,
            JobId = newerJob.Id,
            JobName = "jobs/newer-session.gcode",
            FileName = "newer-session.gcode",
            Confidence = 0.884m,
            DetectedAt = new DateTime(2026, 3, 27, 8, 20, 0, DateTimeKind.Utc),
            AutoPaused = true,
        });
        await dbContext.SaveChangesAsync();

        PrinterSessionTimelineService service = new(dbContext);

        PrinterSessionTimelineDto timeline = await service.GetRecentAsync(printer.Id, take: 1, CancellationToken.None);

        timeline.ReturnedSessionCount.Should().Be(1);
        timeline.Sessions.Should().ContainSingle();
        timeline.Sessions[0].JobId.Should().Be(newerJob.Id);
        timeline.Sessions[0].JobName.Should().Be("jobs/newer-session.gcode");
        timeline.Sessions[0].FailureIncidentCount.Should().Be(1);
        timeline.Sessions[0].Events[^1].OccurredAt.Should().Be(new DateTime(2026, 3, 27, 8, 25, 0, DateTimeKind.Utc));
        olderJob.Id.Should().NotBe(timeline.Sessions[0].JobId);
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    /// <summary>
    /// Creates a new database context for the in-memory SQLite connection.
    /// </summary>
    private AppDbContext CreateDbContext() => new(_dbContextOptions);

    /// <summary>
    /// Seeds the minimum printer graph required by the app model.
    /// </summary>
    private static async Task<Printer> SeedPrinterAsync(AppDbContext dbContext, string printerName)
    {
        Manufacturer manufacturer = new()
        {
            Id = Guid.NewGuid(),
            Name = $"{printerName} Manufacturer",
        };
        PrinterModel model = new()
        {
            Id = Guid.NewGuid(),
            ManufacturerId = manufacturer.Id,
            Name = $"{printerName} Model",
        };
        Printer printer = new()
        {
            Id = Guid.NewGuid(),
            Name = printerName,
            ServerUrl = $"http://{Guid.NewGuid():N}.local",
            BackendPort = 7125,
            ManufacturerId = manufacturer.Id,
            ModelId = model.Id,
        };

        _ = dbContext.Manufacturers.Add(manufacturer);
        _ = dbContext.PrinterModels.Add(model);
        _ = dbContext.Printers.Add(printer);
        await dbContext.SaveChangesAsync();

        return printer;
    }

    /// <summary>
    /// Seeds a print job assigned to the target printer.
    /// </summary>
    private static async Task<PrintJob> SeedJobAsync(
        AppDbContext dbContext,
        Guid printerId,
        PrintJobStatus status,
        DateTime queuedAt,
        DateTime? dispatchedAt,
        DateTime? startedAt,
        DateTime? endedAt,
        string? failureReason,
        string name)
    {
        PrintJob job = new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            AssignedPrinterId = printerId,
            Status = status,
            Priority = 0,
            QueuePosition = 0,
            CreatedAt = queuedAt,
            UpdatedAt = endedAt ?? startedAt ?? dispatchedAt ?? queuedAt,
            QueuedAt = queuedAt,
            DispatchedAt = dispatchedAt,
            ActualStartTime = startedAt,
            ActualEndTime = endedAt,
            ActualPrintTime = startedAt.HasValue && endedAt.HasValue ? endedAt.Value - startedAt.Value : null,
            FailureReason = failureReason,
        };

        _ = dbContext.PrintJobs.Add(job);
        await dbContext.SaveChangesAsync();
        return job;
    }
}
