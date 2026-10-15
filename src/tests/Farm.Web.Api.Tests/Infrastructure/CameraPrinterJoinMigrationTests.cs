using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Farm.Web.Api.Tests.Infrastructure;

/// <summary>
/// Verifies the SQLite data mapping in <c>CameraPrinterJoinAndLocation</c> (#3335): legacy
/// <c>Cameras.PrinterId</c> becomes a <see cref="CameraPrinter"/> link (first camera per printer
/// by SortOrder then Id is primary) and free-text <c>Location</c> maps to a <see cref="Location"/> id.
/// </summary>
public sealed class CameraPrinterJoinMigrationTests
{
    private const string PreviousMigration = "20261012013745_AddPrinterShowExternalSpool";

    [Fact]
    public async Task Migrate_LegacyPrinterId_CreatesJoinRowsWithSinglePrimaryPerPrinter()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using AppDbContext context = CreateContext(connection);
        IMigrator migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(PreviousMigration);

        Printer printerA = AddPrinter(context, "Printer A");
        Printer printerB = AddPrinter(context, "Printer B");
        await context.SaveChangesAsync();

        // Printer A: SortOrder decides primary even though the primary was inserted last; Id breaks the tie.
        Guid aSecond = Guid.Parse("00000000-0000-0000-0000-00000000000a");
        Guid aFirstTie = Guid.Parse("00000000-0000-0000-0000-000000000001");
        Guid aSecondTie = Guid.Parse("00000000-0000-0000-0000-000000000002");
        await InsertLegacyCameraAsync(context, aSecond, "A late", printerA.Id, sortOrder: 5, location: null);
        await InsertLegacyCameraAsync(context, aSecondTie, "A tie 2", printerA.Id, sortOrder: 1, location: null);
        await InsertLegacyCameraAsync(context, aFirstTie, "A tie 1", printerA.Id, sortOrder: 1, location: null);
        Guid bOnly = Guid.NewGuid();
        await InsertLegacyCameraAsync(context, bOnly, "B only", printerB.Id, sortOrder: 3, location: null);
        Guid standalone = Guid.NewGuid();
        await InsertLegacyCameraAsync(context, standalone, "Standalone", printerId: null, sortOrder: 0, location: null);

        await migrator.MigrateAsync();
        context.ChangeTracker.Clear();

        List<CameraPrinter> links = await context.Set<CameraPrinter>().AsNoTracking().ToListAsync();
        links.Should().HaveCount(4);
        links.Should().NotContain(l => l.CameraId == standalone);
        links.Where(l => l.PrinterId == printerA.Id).Should().BeEquivalentTo(
        [
            new { CameraId = aFirstTie, IsPrimary = true, SortOrder = 1 },
            new { CameraId = aSecondTie, IsPrimary = false, SortOrder = 1 },
            new { CameraId = aSecond, IsPrimary = false, SortOrder = 5 },
        ]);
        links.Should().ContainSingle(l => l.PrinterId == printerB.Id)
            .Which.Should().BeEquivalentTo(new { CameraId = bOnly, IsPrimary = true, SortOrder = 3 });
        (await context.Cameras.CountAsync()).Should().Be(5, "the migration must not drop camera rows");
    }

    [Fact]
    public async Task Migrate_LegacyLocationText_MapsToLocationIdIgnoringCaseAndWhitespace()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using AppDbContext context = CreateContext(connection);
        IMigrator migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(PreviousMigration);

        var shallow = new Location { Id = Guid.NewGuid(), Name = "Print Room", Path = "/print-room", Depth = 0 };
        var deep = new Location { Id = Guid.NewGuid(), Name = "print room", Path = "/lab/print-room", Depth = 1, ParentId = null };
        context.Locations.AddRange(deep, shallow);
        await context.SaveChangesAsync();

        Guid matched = Guid.NewGuid();
        Guid unmatched = Guid.NewGuid();
        Guid blank = Guid.NewGuid();
        Guid none = Guid.NewGuid();
        await InsertLegacyCameraAsync(context, matched, "Matched", printerId: null, sortOrder: 0, location: "  PRINT room ");
        await InsertLegacyCameraAsync(context, unmatched, "Unmatched", printerId: null, sortOrder: 0, location: "Garage");
        await InsertLegacyCameraAsync(context, blank, "Blank", printerId: null, sortOrder: 0, location: "   ");
        await InsertLegacyCameraAsync(context, none, "None", printerId: null, sortOrder: 0, location: null);

        await migrator.MigrateAsync();
        context.ChangeTracker.Clear();

        Dictionary<Guid, Guid?> locations = await context.Cameras.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.LocationId);
        locations[matched].Should().Be(shallow.Id, "the shallowest matching location wins");
        locations[unmatched].Should().BeNull();
        locations[blank].Should().BeNull();
        locations[none].Should().BeNull();
    }

    private static AppDbContext CreateContext(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection, options => options.MigrationsAssembly("Farm.Migrations.Sqlite"))
            .Options);

    private static Printer AddPrinter(AppDbContext context, string name)
    {
        Manufacturer manufacturer = context.Manufacturers.Local.FirstOrDefault()
            ?? new Manufacturer { Id = Guid.NewGuid(), Name = "Camera migration fixture" };
        PrinterModel model = context.PrinterModels.Local.FirstOrDefault()
            ?? new PrinterModel { Id = Guid.NewGuid(), Name = "Camera migration model", ManufacturerId = manufacturer.Id, Manufacturer = manufacturer };
        var printer = new Printer
        {
            Id = Guid.NewGuid(),
            Name = name,
            ServerUrl = $"http://{Guid.NewGuid():N}.example.test",
            ManufacturerId = manufacturer.Id,
            Manufacturer = manufacturer,
            ModelId = model.Id,
            Model = model,
        };
        context.Printers.Add(printer);
        return printer;
    }

    private static Task<int> InsertLegacyCameraAsync(
        AppDbContext context,
        Guid id,
        string name,
        Guid? printerId,
        int sortOrder,
        string? location) =>
        context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Cameras"
                ("Id", "Name", "CameraType", "HealthStatus", "Source", "ConsecutiveFailures", "CreatedAt",
                 "IsEnabled", "SortOrder", "PrinterId", "Location", "StreamUrl")
            VALUES ({id}, {name}, 'General', 'Healthy', 'Moonraker', 0, {DateTime.UtcNow},
                    1, {sortOrder}, {printerId}, {location}, 'http://camera.example.test/stream');
            """);
}
