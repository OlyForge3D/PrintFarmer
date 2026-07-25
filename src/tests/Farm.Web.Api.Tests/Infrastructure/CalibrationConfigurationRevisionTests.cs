using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Tests.Infrastructure;

public sealed class CalibrationConfigurationRevisionTests
{
    [Fact]
    public async Task SaveChangesAsync_WithCalibrationRelevantPrinterChange_IncrementsPersistedRevision()
    {
        await using AppDbContext db = CreateContext();
        Printer printer = CreatePrinter();
        _ = db.Printers.Add(printer);
        _ = await db.SaveChangesAsync();

        printer.FirmwareFamily = PrinterFirmwareFamily.Klipper;
        _ = await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        Printer persisted = await db.Printers.AsNoTracking().SingleAsync();
        _ = persisted.ConfigurationRevision.Should().Be(2);
        _ = persisted.CalibrationConfigurationUpdatedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task SaveChangesAsync_WithPrinterModelChange_IncrementsPersistedRevision()
    {
        await using AppDbContext db = CreateContext();
        Printer printer = CreatePrinter();
        _ = db.Printers.Add(printer);
        _ = await db.SaveChangesAsync();

        printer.ModelId = Guid.NewGuid();
        _ = await db.SaveChangesAsync();

        _ = printer.ConfigurationRevision.Should().Be(2);
    }

    [Fact]
    public async Task SaveChangesAsync_WithNonCalibrationPrinterAndSpoolChanges_PreservesRevision()
    {
        await using AppDbContext db = CreateContext();
        Printer printer = CreatePrinter();
        _ = db.Printers.Add(printer);
        _ = await db.SaveChangesAsync();

        printer.Notes = "Operator-only note";
        _ = db.Spools.Add(new Spool
        {
            Id = Guid.NewGuid(),
            Material = "PLA",
            ColorHex = "#000000",
            WeightGrams = 1000,
            InUse = false,
            AssignedPrinterId = printer.Id,
        });
        _ = await db.SaveChangesAsync();

        _ = printer.ConfigurationRevision.Should().Be(1);
    }

    [Fact]
    public async Task SaveChangesAsync_WithCalibrationRelevantToolheadChange_IncrementsPrinterRevision()
    {
        await using AppDbContext db = CreateContext();
        Printer printer = CreatePrinter();
        Toolhead toolhead = printer.Toolheads.Single();
        _ = db.Printers.Add(printer);
        _ = await db.SaveChangesAsync();

        toolhead.NozzleDiameter = 0.6;
        _ = await db.SaveChangesAsync();

        _ = printer.ConfigurationRevision.Should().Be(2);
    }

    private static AppDbContext CreateContext()
    {
        DbContextOptions<AppDbContext> options =
            new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase($"calibration-revision-{Guid.NewGuid()}")
                .Options;
        return new AppDbContext(options);
    }

    private static Printer CreatePrinter()
    {
        Guid printerId = Guid.NewGuid();
        return new Printer
        {
            Id = printerId,
            Name = "Revision test printer",
            ServerUrl = "http://printer.invalid",
            BackendPort = 7125,
            Backend = (int)PrinterBackend.Moonraker,
            ManufacturerId = Guid.NewGuid(),
            ModelId = Guid.NewGuid(),
            Toolheads =
            [
                new Toolhead
                {
                    Id = Guid.NewGuid(),
                    PrinterId = printerId,
                    Name = "T0",
                    Index = 0,
                    IsPrimary = true,
                },
            ],
        };
    }
}
