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
