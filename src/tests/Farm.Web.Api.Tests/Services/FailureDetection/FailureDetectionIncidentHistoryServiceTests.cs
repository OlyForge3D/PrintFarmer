using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.FailureDetection;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Tests.Services.FailureDetection;

public sealed class FailureDetectionIncidentHistoryServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _dbContextOptions;

    public FailureDetectionIncidentHistoryServiceTests()
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
    public async Task RecordFailureAsync_PersistsIncidentWithResolvedJobContext()
    {
        using AppDbContext dbContext = CreateDbContext();
        Printer printer = await SeedPrinterAsync(dbContext, "History Printer");
        FailureDetectionIncidentHistoryService service = new(dbContext);
        DateTime detectedAt = new(2026, 3, 27, 12, 34, 56, DateTimeKind.Utc);

        await service.RecordFailureAsync(
            printer.Id,
            Guid.NewGuid(),
            "folder/spaghetti-test.gcode",
            "spaghetti-test.gcode",
            0.9321m,
            detectedAt,
            "http://camera.local/snapshot.jpg",
            autoPaused: true,
            CancellationToken.None);

        FailureDetectionIncident persistedIncident = await dbContext.FailureDetectionIncidents.SingleAsync();
        persistedIncident.PrinterId.Should().Be(printer.Id);
        persistedIncident.JobName.Should().Be("folder/spaghetti-test.gcode");
        persistedIncident.FileName.Should().Be("spaghetti-test.gcode");
        persistedIncident.Confidence.Should().Be(0.9321m);
        persistedIncident.DetectedAt.Should().Be(detectedAt);
        persistedIncident.SnapshotUrl.Should().Be("http://camera.local/snapshot.jpg");
        persistedIncident.AutoPaused.Should().BeTrue();
    }

    [Fact]
    public async Task GetRecentAsync_WhenPrinterFilterProvided_ReturnsNewestMatchingIncidents()
    {
        using AppDbContext dbContext = CreateDbContext();
        Printer firstPrinter = await SeedPrinterAsync(dbContext, "Filtered Printer");
        Printer secondPrinter = await SeedPrinterAsync(dbContext, "Other Printer");
        dbContext.FailureDetectionIncidents.AddRange(
            new FailureDetectionIncident
            {
                Id = Guid.NewGuid(),
                PrinterId = firstPrinter.Id,
                JobName = "jobs/first.gcode",
                FileName = "first.gcode",
                Confidence = 0.81m,
                DetectedAt = new DateTime(2026, 3, 27, 10, 00, 00, DateTimeKind.Utc),
                AutoPaused = false,
            },
            new FailureDetectionIncident
            {
                Id = Guid.NewGuid(),
                PrinterId = secondPrinter.Id,
                JobName = "jobs/second.gcode",
                FileName = "second.gcode",
                Confidence = 0.88m,
                DetectedAt = new DateTime(2026, 3, 27, 11, 00, 00, DateTimeKind.Utc),
                AutoPaused = true,
            },
            new FailureDetectionIncident
            {
                Id = Guid.NewGuid(),
                PrinterId = firstPrinter.Id,
                JobName = "jobs/latest.gcode",
                FileName = "latest.gcode",
                Confidence = 0.95m,
                DetectedAt = new DateTime(2026, 3, 27, 12, 00, 00, DateTimeKind.Utc),
                AutoPaused = true,
            });
        await dbContext.SaveChangesAsync();

        FailureDetectionIncidentHistoryService service = new(dbContext);

        List<Farm.Infrastructure.FailureDetectionDto> incidents = await service.GetRecentAsync(firstPrinter.Id, take: 10, CancellationToken.None);

        incidents.Should().HaveCount(2);
        incidents[0].Id.Should().NotBeEmpty();
        incidents[0].PrinterId.Should().Be(firstPrinter.Id);
        incidents[0].PrinterName.Should().Be("Filtered Printer");
        incidents[0].JobName.Should().Be("jobs/latest.gcode");
        incidents[1].JobName.Should().Be("jobs/first.gcode");
    }

    [Fact]
    public async Task GetRecentAsync_WhenTakeExceedsBounds_ClampsToSupportedWindow()
    {
        using AppDbContext dbContext = CreateDbContext();
        Printer printer = await SeedPrinterAsync(dbContext, "Take Clamp Printer");
        dbContext.FailureDetectionIncidents.AddRange(
            new FailureDetectionIncident
            {
                Id = Guid.NewGuid(),
                PrinterId = printer.Id,
                Confidence = 0.91m,
                DetectedAt = new DateTime(2026, 3, 27, 12, 0, 0, DateTimeKind.Utc),
            },
            new FailureDetectionIncident
            {
                Id = Guid.NewGuid(),
                PrinterId = printer.Id,
                Confidence = 0.81m,
                DetectedAt = new DateTime(2026, 3, 27, 11, 0, 0, DateTimeKind.Utc),
            },
            new FailureDetectionIncident
            {
                Id = Guid.NewGuid(),
                PrinterId = printer.Id,
                Confidence = 0.71m,
                DetectedAt = new DateTime(2026, 3, 27, 10, 0, 0, DateTimeKind.Utc),
            });
        await dbContext.SaveChangesAsync();

        FailureDetectionIncidentHistoryService service = new(dbContext);

        List<FailureDetectionDto> defaultWindow = await service.GetRecentAsync(printer.Id, take: 0, CancellationToken.None);
        List<FailureDetectionDto> maxWindow = await service.GetRecentAsync(printer.Id, take: 999, CancellationToken.None);

        defaultWindow.Should().HaveCount(3);
        maxWindow.Should().HaveCount(3);
        maxWindow.Select(incident => incident.Confidence).Should().Equal(0.91m, 0.81m, 0.71m);
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    private AppDbContext CreateDbContext() => new(_dbContextOptions);

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
}
