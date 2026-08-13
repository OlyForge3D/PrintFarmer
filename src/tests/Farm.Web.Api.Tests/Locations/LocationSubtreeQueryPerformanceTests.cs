using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Repositories.Locations;
using Farm.Infrastructure.Repositories.UnitOfWork;
using Farm.Infrastructure.Services.Locations;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.Security;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Locations;

/// <summary>
/// Regression tests for issue #1514 (follow-up to spike #1500): the three nested N+1
/// traversals rooted in <see cref="EfLocationRepository.GetDescendantsAsync"/> must stay
/// O(1)/O(2) EF command round trips as the subtree grows, using the materialized
/// <see cref="Location.Path"/> prefix instead of a per-node BFS. Command counts are
/// measured with a <see cref="DbCommandInterceptor"/>, mirroring the throwaway benchmark
/// used in the #1500 spike and the pattern already used in <see cref="LocationHierarchyTests"/>.
/// </summary>
public class LocationSubtreeQueryPerformanceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly CommandCountingInterceptor _interceptor;
    private readonly AppDbContext _context;
    private readonly Guid _testManufacturerId;
    private readonly Guid _testModelId;
    private int _printerCounter;

    public LocationSubtreeQueryPerformanceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _interceptor = new CommandCountingInterceptor();
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(_interceptor)
            .Options;

        _context = new AppDbContext(options);
        _context.Database.EnsureCreated();

        _testManufacturerId = Guid.NewGuid();
        _testModelId = Guid.NewGuid();

        _context.Manufacturers.Add(new Manufacturer { Id = _testManufacturerId, Name = "Test Manufacturer" });
        _context.PrinterModels.Add(new PrinterModel { Id = _testModelId, Name = "Test Model", ManufacturerId = _testManufacturerId });
        _context.SaveChanges();
        _interceptor.Reset();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task<Location> CreateAndSaveLocationAsync(string name, Guid? parentId = null)
    {
        int depth = 0;
        string path = $"/{name}";

        if (parentId.HasValue)
        {
            Location? parent = await _context.Locations.FindAsync(parentId.Value);
            if (parent is not null)
            {
                depth = parent.Depth + 1;
                path = $"{parent.Path}/{name}";
            }
        }

        var location = new Location
        {
            Id = Guid.NewGuid(),
            Name = name,
            ParentId = parentId,
            Path = path,
            Depth = depth,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            ModifiedAt = DateTime.UtcNow
        };

        _context.Locations.Add(location);
        await _context.SaveChangesAsync();
        return location;
    }

    private async Task<Printer> CreatePrinterInLocationAsync(Guid? locationId)
    {
        int n = Interlocked.Increment(ref _printerCounter);
        var printer = new Printer
        {
            Id = Guid.NewGuid(),
            Name = $"Printer {n}",
            ServerUrl = $"http://192.168.2.{n}",
            BackendPort = 7125,
            Backend = (int)PrinterBackend.Moonraker,
            LocationId = locationId,
            ManufacturerId = _testManufacturerId,
            ModelId = _testModelId,
            IsEnabled = true,
            IsAvailable = true
        };

        _context.Printers.Add(printer);
        await _context.SaveChangesAsync();
        return printer;
    }

    /// <summary>
    /// Builds a 3-level-deep tree (root -> 3 children -> 3 grandchildren each = 13 locations)
    /// mirroring the "realistic tree" shape measured in the #1500 spike, with one printer
    /// assigned to each location (13 printers total).
    /// </summary>
    private async Task<(Location Root, List<Location> Descendants, List<Printer> Printers)> SeedTreeAsync()
    {
        Location root = await CreateAndSaveLocationAsync("Root");
        var descendants = new List<Location>();
        var printers = new List<Printer>();

        printers.Add(await CreatePrinterInLocationAsync(root.Id));

        for (int i = 0; i < 3; i++)
        {
            Location child = await CreateAndSaveLocationAsync($"Child-{i}", root.Id);
            descendants.Add(child);
            printers.Add(await CreatePrinterInLocationAsync(child.Id));

            for (int j = 0; j < 3; j++)
            {
                Location grandchild = await CreateAndSaveLocationAsync($"Grandchild-{i}-{j}", child.Id);
                descendants.Add(grandchild);
                printers.Add(await CreatePrinterInLocationAsync(grandchild.Id));
            }
        }

        return (root, descendants, printers);
    }

    // =========================================================================
    // EfLocationRepository.GetDescendantsAsync
    // =========================================================================

    [Fact]
    [Trait("Category", "Locations")]
    public async Task GetDescendantsAsync_ReturnsSameSetAsBfs_UsingConstantCommandCount()
    {
        (Location root, List<Location> expectedDescendants, _) = await SeedTreeAsync();
        var repository = new EfLocationRepository(_context);

        _interceptor.Reset();
        List<Location> descendants = await repository.GetDescendantsAsync(root.Id, CancellationToken.None);

        // Root lookup (Path, served from the identity map when already tracked) + one
        // prefix-range query = O(1), regardless of subtree size.
        _interceptor.CommandCount.Should().BeLessThanOrEqualTo(2, "GetDescendantsAsync should issue a fixed number of commands, not one per BFS node");

        descendants.Select(d => d.Id).Should().BeEquivalentTo(expectedDescendants.Select(d => d.Id),
            "the prefix-range query must return exactly the same descendant set the old BFS produced");
    }

    [Fact]
    [Trait("Category", "Locations")]
    public async Task GetDescendantsAsync_CommandCountDoesNotGrowWithSubtreeSize()
    {
        Location root = await CreateAndSaveLocationAsync("Root");
        for (int i = 0; i < 20; i++)
        {
            await CreateAndSaveLocationAsync($"Child-{i}", root.Id);
        }

        var repository = new EfLocationRepository(_context);
        _interceptor.Reset();

        List<Location> descendants = await repository.GetDescendantsAsync(root.Id, CancellationToken.None);

        descendants.Should().HaveCount(20);
        _interceptor.CommandCount.Should().BeLessThanOrEqualTo(2, "command count must stay O(1) even for a much larger subtree (was O(N) with the BFS)");
    }

    [Fact]
    [Trait("Category", "Locations")]
    public async Task GetDescendantsAsync_UnknownLocation_ReturnsEmptyWithoutSecondQuery()
    {
        var repository = new EfLocationRepository(_context);
        _interceptor.Reset();

        List<Location> descendants = await repository.GetDescendantsAsync(Guid.NewGuid(), CancellationToken.None);

        descendants.Should().BeEmpty();
    }

    // =========================================================================
    // EfLocationRepository.GetPrinterCountInSubtreeAsync (LocationService.UpdatePrinterCountAsync)
    // =========================================================================

    [Fact]
    [Trait("Category", "Locations")]
    public async Task UpdatePrinterCountAsync_ComputesCorrectTotalUsingConstantCommandCount()
    {
        (Location root, _, List<Printer> printers) = await SeedTreeAsync();
        LocationService service = CreateLocationService();

        _interceptor.Reset();
        await service.UpdatePrinterCountAsync(root.Id, CancellationToken.None);

        Location? updated = await _context.Locations.AsNoTracking().FirstOrDefaultAsync(l => l.Id == root.Id);
        updated.Should().NotBeNull();
        updated!.PrinterCount.Should().Be(1, "only one printer is assigned directly to the root");
        updated.TotalPrinterCount.Should().Be(printers.Count, "TotalPrinterCount must equal the root printer plus every descendant's printer");

        // Two O(1) aggregate queries (own count + subtree count) + FindByIdAsync x2 (service + subtree
        // lookup) + update/save — a small constant, not one COUNT per descendant (was 2N with the old loop).
        _interceptor.CommandCount.Should().BeLessThanOrEqualTo(6,
            "UpdatePrinterCountAsync must not scale with the number of descendants (was O(N) with the per-descendant COUNT loop)");
    }

    [Fact]
    [Trait("Category", "Locations")]
    public async Task UpdatePrinterCountAsync_CommandCountDoesNotGrowWithSubtreeSize()
    {
        Location root = await CreateAndSaveLocationAsync("Root");
        await CreatePrinterInLocationAsync(root.Id);

        for (int i = 0; i < 25; i++)
        {
            Location child = await CreateAndSaveLocationAsync($"Child-{i}", root.Id);
            await CreatePrinterInLocationAsync(child.Id);
        }

        LocationService service = CreateLocationService();
        _interceptor.Reset();

        await service.UpdatePrinterCountAsync(root.Id, CancellationToken.None);

        Location? updated = await _context.Locations.AsNoTracking().FirstOrDefaultAsync(l => l.Id == root.Id);
        updated!.TotalPrinterCount.Should().Be(26);
        _interceptor.CommandCount.Should().BeLessThanOrEqualTo(6,
            "command count must stay constant regardless of how many descendants/printers exist");
    }

    // =========================================================================
    // EfLocationRepository.GetPrintersInSubtreeAsync (LocationService.GetSubtreePrintersAsync)
    // =========================================================================

    [Fact]
    [Trait("Category", "Locations")]
    public async Task GetSubtreePrintersAsync_ReturnsSamePrintersAsPerLocationLoop_UsingConstantCommandCount()
    {
        (Location root, _, List<Printer> expectedPrinters) = await SeedTreeAsync();
        LocationService service = CreateLocationService();

        _interceptor.Reset();
        List<LocationSubtreePrinterDto> result = await service.GetSubtreePrintersAsync(root.Id, CancellationToken.None);

        result.Select(p => p.PrinterId).Should().BeEquivalentTo(expectedPrinters.Select(p => p.Id),
            "the set-based subtree query must return exactly the printers the old per-location loop produced");

        // FindByIdAsync (location) + FindByIdAsync (inside GetPrintersInSubtreeAsync) + one
        // set-based printers query = O(1), not one query per location in the subtree.
        _interceptor.CommandCount.Should().BeLessThanOrEqualTo(3,
            "GetSubtreePrintersAsync must not issue one GetPrintersInLocationAsync call per location in the subtree");
    }

    [Fact]
    [Trait("Category", "Locations")]
    public async Task GetSubtreePrintersAsync_CommandCountDoesNotGrowWithSubtreeSize()
    {
        Location root = await CreateAndSaveLocationAsync("Root");
        await CreatePrinterInLocationAsync(root.Id);

        for (int i = 0; i < 20; i++)
        {
            Location child = await CreateAndSaveLocationAsync($"Child-{i}", root.Id);
            await CreatePrinterInLocationAsync(child.Id);
        }

        LocationService service = CreateLocationService();
        _interceptor.Reset();

        List<LocationSubtreePrinterDto> result = await service.GetSubtreePrintersAsync(root.Id, CancellationToken.None);

        result.Should().HaveCount(21);
        _interceptor.CommandCount.Should().BeLessThanOrEqualTo(3,
            "command count must stay constant even for a much larger subtree (was 2N with GetDescendantsAsync + per-location loop)");
    }

    [Fact]
    [Trait("Category", "Locations")]
    public async Task GetSubtreePrintersAsync_ExcludesPrintersOutsideSubtree()
    {
        Location root = await CreateAndSaveLocationAsync("Root");
        Location child = await CreateAndSaveLocationAsync("Child", root.Id);
        Location sibling = await CreateAndSaveLocationAsync("Sibling");

        Printer rootPrinter = await CreatePrinterInLocationAsync(root.Id);
        Printer childPrinter = await CreatePrinterInLocationAsync(child.Id);
        await CreatePrinterInLocationAsync(sibling.Id);

        LocationService service = CreateLocationService();

        List<LocationSubtreePrinterDto> result = await service.GetSubtreePrintersAsync(root.Id, CancellationToken.None);

        result.Select(p => p.PrinterId).Should().BeEquivalentTo([rootPrinter.Id, childPrinter.Id],
            "printers under an unrelated sibling location must not be included");
    }

    private LocationService CreateLocationService()
    {
        var unitOfWork = new AppUnitOfWork(_context, Mock.Of<ISensitiveDataProtector>());
        var statusCache = new Mock<IPrinterStatusCacheReader>();
        statusCache.Setup(s => s.GetAllStatuses()).Returns(new Dictionary<Guid, PrinterStatusDto>());

        return new LocationService(unitOfWork, NullLogger<LocationService>.Instance, statusCache.Object);
    }

    private sealed class CommandCountingInterceptor : DbCommandInterceptor
    {
        public int CommandCount { get; private set; }

        public void Reset()
        {
            CommandCount = 0;
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            CommandCount++;
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            CommandCount++;
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<object> ScalarExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<object> result)
        {
            CommandCount++;
            return result;
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
        {
            CommandCount++;
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result)
        {
            CommandCount++;
            return result;
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            CommandCount++;
            return ValueTask.FromResult(result);
        }
    }
}
