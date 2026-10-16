using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Cameras;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Farm.Infrastructure.Tests.Repositories.Cameras;

/// <summary>
/// Relational SQLite coverage for <see cref="EfCameraRepository"/> printer-link queries through the
/// <see cref="CameraPrinter"/> join.
/// </summary>
public sealed class EfCameraRepositoryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly EfCameraRepository _repo;

    public EfCameraRepositoryTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=False");
        _connection.Open();
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;
        _db = new AppDbContext(options);
        _db.Database.EnsureCreated();
        _repo = new EfCameraRepository(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private Printer AddPrinter(string name)
    {
        var printer = new Printer { Id = Guid.NewGuid(), Name = name, ServerUrl = $"http://{name}.local" };
        _db.Printers.Add(printer);
        return printer;
    }

    private Camera AddCamera(string name, CameraSource source = CameraSource.Standalone, int sortOrder = 0)
    {
        var camera = new Camera { Id = Guid.NewGuid(), Name = name, Source = source, SortOrder = sortOrder };
        _db.Cameras.Add(camera);
        return camera;
    }

    [Fact]
    public async Task GetByPrinterIdAsync_ReturnsLinkedCameras_PrimaryFirstThenLinkSortOrder()
    {
        Printer printer = AddPrinter("p1");
        Printer other = AddPrinter("p2");
        Camera secondary = AddCamera("a-secondary");
        Camera primary = AddCamera("z-primary");
        Camera shared = AddCamera("shared");
        AddCamera("standalone");
        secondary.LinkToPrinter(printer.Id, sortOrder: 2);
        primary.LinkToPrinter(printer.Id, isPrimary: true, sortOrder: 9);
        shared.LinkToPrinter(printer.Id, sortOrder: 1);
        shared.LinkToPrinter(other.Id, isPrimary: true);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        List<Camera> cameras = await _repo.GetByPrinterIdAsync(printer.Id, CancellationToken.None);

        cameras.Select(c => c.Name).Should().Equal("z-primary", "shared", "a-secondary");
        cameras.Single(c => c.Name == "shared").PrinterLinks.Should().HaveCount(2);
        (await _repo.GetByPrinterIdAsync(other.Id, CancellationToken.None)).Select(c => c.Name).Should().Equal("shared");
    }

    [Fact]
    public async Task HasPrimaryCameraAsync_ReflectsPerPrinterPrimaryLink()
    {
        Printer withPrimary = AddPrinter("p1");
        Printer withoutPrimary = AddPrinter("p2");
        AddCamera("c1").LinkToPrinter(withPrimary.Id, isPrimary: true);
        AddCamera("c2").LinkToPrinter(withoutPrimary.Id);
        await _db.SaveChangesAsync();

        (await _repo.HasPrimaryCameraAsync(withPrimary.Id, CancellationToken.None)).Should().BeTrue();
        (await _repo.HasPrimaryCameraAsync(withoutPrimary.Id, CancellationToken.None)).Should().BeFalse();
        (await _repo.HasPrimaryCameraAsync(Guid.NewGuid(), CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task FindByPrinterIdAndSourceAsync_MatchesThroughJoin()
    {
        Printer printer = AddPrinter("p1");
        Printer other = AddPrinter("p2");
        Camera moonraker = AddCamera("mr", CameraSource.Moonraker);
        moonraker.LinkToPrinter(printer.Id, isPrimary: true);
        AddCamera("other-mr", CameraSource.Moonraker).LinkToPrinter(other.Id, isPrimary: true);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        Camera? found = await _repo.FindByPrinterIdAndSourceAsync(printer.Id, CameraSource.Moonraker, CancellationToken.None);

        found.Should().NotBeNull();
        found!.Id.Should().Be(moonraker.Id);
        found.GetPrimaryPrinter()!.Id.Should().Be(printer.Id);
        (await _repo.FindByPrinterIdAndSourceAsync(printer.Id, CameraSource.PrusaLink, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task DeletingPrinter_CascadesLinksButKeepsCamera()
    {
        Printer printer = AddPrinter("p1");
        AddCamera("c1").LinkToPrinter(printer.Id, isPrimary: true);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        await _db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON;");
        await _db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Printers\" WHERE \"Id\" = {printer.Id}");

        (await _db.Set<CameraPrinter>().CountAsync()).Should().Be(0);
        (await _db.Cameras.CountAsync()).Should().Be(1);
    }
}
