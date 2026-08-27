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

namespace Farm.Infrastructure.Tests.Locations;

/// <summary>
/// Regression tests for issue #1514 (follow-up to spike #1500): the three nested N+1
/// traversals rooted in <see cref="EfLocationRepository.GetDescendantsAsync"/> must stay
/// O(1)/O(2) EF command round trips as the subtree grows, using the materialized
/// <see cref="Location.Path"/> prefix instead of a per-node BFS. Command counts are
/// measured with a <see cref="DbCommandInterceptor"/>, mirroring the throwaway benchmark
/// used in the #1500 spike and the pattern already used in <c>LocationHierarchyTests</c>.
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
        _interceptor.CommandCount.Should().BeLessThanOrEqualTo(1,
            "an unknown location should only issue the root lookup, with no follow-up prefix-range query");
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

    [Fact]
    [Trait("Category", "Locations")]
    public async Task GetSubtreePrintersAsync_AttributesEachPrinterToItsOwnLocation()
    {
        // Regression: LocationName is now derived from printer.Location?.Name ?? location.Name,
        // replacing a guaranteed-correct dictionary lookup. Verify each printer's DTO carries the
        // *correct* LocationId/LocationName, not just that the right set of printer IDs came back.
        Location root = await CreateAndSaveLocationAsync("Root");
        Location child = await CreateAndSaveLocationAsync("Child", root.Id);

        Printer rootPrinter = await CreatePrinterInLocationAsync(root.Id);
        Printer childPrinter = await CreatePrinterInLocationAsync(child.Id);

        LocationService service = CreateLocationService();
        List<LocationSubtreePrinterDto> result = await service.GetSubtreePrintersAsync(root.Id, CancellationToken.None);

        LocationSubtreePrinterDto rootDto = result.Single(p => p.PrinterId == rootPrinter.Id);
        rootDto.LocationId.Should().Be(root.Id);
        rootDto.LocationName.Should().Be("Root");

        LocationSubtreePrinterDto childDto = result.Single(p => p.PrinterId == childPrinter.Id);
        childDto.LocationId.Should().Be(child.Id);
        childDto.LocationName.Should().Be("Child", "a descendant printer must be attributed to its own location, not silently fall back to the root's name");
    }

    // =========================================================================
    // Materialized-path prefix correctness edge cases
    // =========================================================================

    [Fact]
    [Trait("Category", "Locations")]
    public async Task GetDescendantsAsync_DoesNotMatchSiblingWithNamePrefixCollision()
    {
        // Classic materialized-path bug: without a trailing-slash boundary, "/Room1" would also
        // match "/Room10" via a naive StartsWith("/Room1").
        Location parent = await CreateAndSaveLocationAsync("Building");
        Location room1 = await CreateAndSaveLocationAsync("Room1", parent.Id);
        Location room10 = await CreateAndSaveLocationAsync("Room10", parent.Id);
        Location room1Child = await CreateAndSaveLocationAsync("Shelf", room1.Id);

        var repository = new EfLocationRepository(_context);
        List<Location> descendants = await repository.GetDescendantsAsync(room1.Id, CancellationToken.None);

        descendants.Select(d => d.Id).Should().BeEquivalentTo([room1Child.Id],
            "Room10 is a sibling, not a descendant of Room1, and must not be matched by prefix");
        descendants.Select(d => d.Id).Should().NotContain(room10.Id);
    }

    [Fact]
    [Trait("Category", "Locations")]
    public async Task GetDescendantsAsync_ExcludesInactiveDescendants()
    {
        Location root = await CreateAndSaveLocationAsync("Root");
        Location activeChild = await CreateAndSaveLocationAsync("ActiveChild", root.Id);
        Location inactiveChild = await CreateAndSaveLocationAsync("InactiveChild", root.Id);
        Location inactiveGrandchild = await CreateAndSaveLocationAsync("Grandchild", inactiveChild.Id);

        inactiveChild.IsActive = false;
        inactiveGrandchild.IsActive = false;
        await _context.SaveChangesAsync();

        var repository = new EfLocationRepository(_context);
        List<Location> descendants = await repository.GetDescendantsAsync(root.Id, CancellationToken.None);

        descendants.Select(d => d.Id).Should().BeEquivalentTo([activeChild.Id],
            "soft-deleted (IsActive = false) descendants must be excluded, matching the old BFS's filter");
    }

    [Fact]
    [Trait("Category", "Locations")]
    public async Task GetPrinterCountInSubtreeAsync_ExcludesPrintersUnderInactiveDescendants()
    {
        Location root = await CreateAndSaveLocationAsync("Root");
        Location inactiveChild = await CreateAndSaveLocationAsync("InactiveChild", root.Id);
        inactiveChild.IsActive = false;
        await _context.SaveChangesAsync();

        await CreatePrinterInLocationAsync(root.Id);
        await CreatePrinterInLocationAsync(inactiveChild.Id);

        var repository = new EfLocationRepository(_context);
        int count = await repository.GetPrinterCountInSubtreeAsync(root.Id, CancellationToken.None);

        count.Should().Be(1, "printers under an inactive descendant location must not be counted");
    }

    [Fact]
    [Trait("Category", "Locations")]
    public async Task GetDescendantsAsync_LocationNameWithLikeWildcardCharacters_MatchesOnlyRealDescendants()
    {
        // StartsWith translates to a SQL LIKE; '%' and '_' are wildcards in LIKE patterns unless
        // escaped. EF Core escapes the argument automatically, but this is the first place in the
        // codebase Path.StartsWith() is used for subtree filtering, so assert it directly.
        Location parent = await CreateAndSaveLocationAsync("100%_Reliable");
        Location child = await CreateAndSaveLocationAsync("Shelf", parent.Id);
        Location unrelated = await CreateAndSaveLocationAsync("100X_ReliableX");

        var repository = new EfLocationRepository(_context);
        List<Location> descendants = await repository.GetDescendantsAsync(parent.Id, CancellationToken.None);

        descendants.Select(d => d.Id).Should().BeEquivalentTo([child.Id],
            "'%' and '_' in a location name must be treated literally, not as SQL LIKE wildcards");
        descendants.Select(d => d.Id).Should().NotContain(unrelated.Id);
    }

    [Fact]
    [Trait("Category", "Locations")]
    public async Task GetDescendantsAsync_UnrootedPath_FallsBackToParentIdTraversal_InsteadOfMatchingEveryLocation()
    {
        // Simulates legacy/imported data whose Path was never materialized (Location.Path
        // defaults to "/"). A naive Path.StartsWith("/") would match every other active
        // location in the table; the repository must detect this and fall back to a
        // ParentId-based traversal instead.
        var unrooted = new Location
        {
            Id = Guid.NewGuid(),
            Name = "Imported",
            Path = "/",
            Depth = 0,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            ModifiedAt = DateTime.UtcNow
        };
        _context.Locations.Add(unrooted);
        await _context.SaveChangesAsync();

        // A real, properly-rooted, unrelated location tree that must NOT be treated as a
        // descendant of the unrooted location.
        Location unrelatedRoot = await CreateAndSaveLocationAsync("UnrelatedRoot");
        await CreateAndSaveLocationAsync("UnrelatedChild", unrelatedRoot.Id);

        var repository = new EfLocationRepository(_context);
        List<Location> descendants = await repository.GetDescendantsAsync(unrooted.Id, CancellationToken.None);

        descendants.Should().BeEmpty("the unrooted location has no real ParentId-linked children, and must not match unrelated locations by a blanket '/' prefix");

        int printerCount = await repository.GetPrinterCountInSubtreeAsync(unrooted.Id, CancellationToken.None);
        printerCount.Should().Be(0);

        List<Printer> printers = await repository.GetPrintersInSubtreeAsync(unrooted.Id, CancellationToken.None);
        printers.Should().BeEmpty();
    }

    // =========================================================================
    // Location name validation (tightly coupled: Path.StartsWith would otherwise
    // treat '/' inside a name as a path separator, breaking subtree isolation)
    // =========================================================================

    [Fact]
    [Trait("Category", "Locations")]
    public async Task CreateLocationAsync_NameContainingSlash_Throws()
    {
        LocationService service = CreateLocationService();

        Func<Task> act = () => service.CreateLocationAsync(new CreateLocationDto { Name = "Foo/Bar" }, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>(
            "a '/' inside a location name would otherwise be indistinguishable from a path separator in the materialized Path");
    }

    [Fact]
    [Trait("Category", "Locations")]
    public async Task UpdateLocationAsync_NameContainingSlash_Throws()
    {
        Location existing = await CreateAndSaveLocationAsync("Foo");
        LocationService service = CreateLocationService();

        Func<Task> act = () => service.UpdateLocationAsync(existing.Id, new UpdateLocationDto { Name = "Foo/Bar" }, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    // =========================================================================
    // Move/rename regression (#1516): RebuildPathAsync must leave Path materialized
    // correctly for the single-round-trip subtree queries above, both within the same
    // DbContext instance used for the move/rename and from a fresh DbContext after
    // SaveChangesAsync (ruling out identity-map artifacts masking a persistence bug).
    // =========================================================================

    [Fact]
    [Trait("Category", "Locations")]
    public async Task MoveAsync_ThenGetDescendantsOfNewParent_IncludesMovedSubtreeWithUpdatedPaths()
    {
        Location oldRoot = await CreateAndSaveLocationAsync("OldRoot");
        Location movedNode = await CreateAndSaveLocationAsync("Moved", oldRoot.Id);
        Location movedChild = await CreateAndSaveLocationAsync("MovedChild", movedNode.Id);
        Location newRoot = await CreateAndSaveLocationAsync("NewRoot");

        LocationService service = CreateLocationService();
        var repository = new EfLocationRepository(_context);

        LocationDto? result = await service.MoveAsync(movedNode.Id, newRoot.Id, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Path.Should().Be("/NewRoot/Moved", "RebuildPathAsync must recompute Path against the new parent before returning");

        // Same DbContext instance used for the move: the subtree query must see the
        // post-move Path immediately, without requiring a fresh context/round trip.
        List<Location> newRootDescendants = await repository.GetDescendantsAsync(newRoot.Id, CancellationToken.None);
        newRootDescendants.Select(d => d.Id).Should().BeEquivalentTo([movedNode.Id, movedChild.Id],
            "the moved subtree must appear under its new parent immediately, in the same DbContext instance used for the move");

        Location movedNodeAfter = newRootDescendants.Single(d => d.Id == movedNode.Id);
        Location movedChildAfter = newRootDescendants.Single(d => d.Id == movedChild.Id);
        movedNodeAfter.Path.Should().Be("/NewRoot/Moved");
        movedNodeAfter.Depth.Should().Be(1);
        movedChildAfter.Path.Should().Be("/NewRoot/Moved/MovedChild");
        movedChildAfter.Depth.Should().Be(2);

        // The old parent's subtree must no longer contain the moved node/its descendant.
        List<Location> oldRootDescendants = await repository.GetDescendantsAsync(oldRoot.Id, CancellationToken.None);
        oldRootDescendants.Should().BeEmpty("the moved subtree must be removed from the old parent's descendants once ParentId/Path change");
    }

    [Fact]
    [Trait("Category", "Locations")]
    public async Task MoveAsync_ThenQueryFromFreshDbContext_SeesPersistedPathAfterSaveChanges()
    {
        Location oldRoot = await CreateAndSaveLocationAsync("OldRoot");
        Location movedNode = await CreateAndSaveLocationAsync("Moved", oldRoot.Id);
        Printer movedPrinter = await CreatePrinterInLocationAsync(movedNode.Id);
        Location newRoot = await CreateAndSaveLocationAsync("NewRoot");

        LocationService service = CreateLocationService();
        await service.MoveAsync(movedNode.Id, newRoot.Id, CancellationToken.None);

        // Open a brand-new DbContext against the same underlying (in-memory) SQLite connection
        // to rule out identity-map/tracked-entity artifacts masking a real persistence bug: the
        // subtree query must reflect the post-move Path purely from what SaveChangesAsync wrote.
        DbContextOptions<AppDbContext> freshOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;
        await using var freshContext = new AppDbContext(freshOptions);
        var freshRepository = new EfLocationRepository(freshContext);

        List<Location> descendants = await freshRepository.GetDescendantsAsync(newRoot.Id, CancellationToken.None);
        descendants.Select(d => d.Id).Should().BeEquivalentTo([movedNode.Id]);
        descendants.Single().Path.Should().Be("/NewRoot/Moved");

        List<Printer> printers = await freshRepository.GetPrintersInSubtreeAsync(newRoot.Id, CancellationToken.None);
        printers.Select(p => p.Id).Should().BeEquivalentTo([movedPrinter.Id],
            "the printer assigned to the moved location must be found via the new parent's subtree query from a fresh DbContext, proving Path was durably persisted");
    }

    [Fact]
    [Trait("Category", "Locations")]
    public async Task UpdateLocationAsync_RenameMidTreeNode_PropagatesPathToDescendantsAndIsVisibleFromAncestorSubtreeQuery()
    {
        Location root = await CreateAndSaveLocationAsync("Root");
        Location middle = await CreateAndSaveLocationAsync("OldName", root.Id);
        Location leaf = await CreateAndSaveLocationAsync("Leaf", middle.Id);
        await CreatePrinterInLocationAsync(leaf.Id);

        LocationService service = CreateLocationService();
        var repository = new EfLocationRepository(_context);

        LocationDto? renamed = await service.UpdateLocationAsync(middle.Id, new UpdateLocationDto { Name = "NewName" }, CancellationToken.None);

        renamed.Should().NotBeNull();
        renamed!.Path.Should().Be("/Root/NewName");

        // The ancestor's subtree query, in the same DbContext instance right after
        // SaveChangesAsync, must reflect the renamed node's and its descendant's rebuilt Path.
        List<Location> rootDescendants = await repository.GetDescendantsAsync(root.Id, CancellationToken.None);
        Location middleAfter = rootDescendants.Single(d => d.Id == middle.Id);
        Location leafAfter = rootDescendants.Single(d => d.Id == leaf.Id);

        middleAfter.Path.Should().Be("/Root/NewName");
        leafAfter.Path.Should().Be("/Root/NewName/Leaf", "RebuildPathAsync must recurse into descendants of the renamed node");
        leafAfter.Depth.Should().Be(2);

        int printerCount = await repository.GetPrinterCountInSubtreeAsync(root.Id, CancellationToken.None);
        printerCount.Should().Be(1, "the leaf printer must still be counted under the root after the rename cascades through Path");

        // And directly against the renamed node's own (new) subtree.
        List<Location> renamedNodeDescendants = await repository.GetDescendantsAsync(middle.Id, CancellationToken.None);
        renamedNodeDescendants.Select(d => d.Id).Should().BeEquivalentTo([leaf.Id]);
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
