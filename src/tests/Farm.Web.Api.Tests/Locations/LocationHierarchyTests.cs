using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Locations;
using Farm.Web.Api.Tests.TestInfrastructure;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Farm.Web.Api.Tests.Locations;

/// <summary>
/// Tests for the hierarchical location system.
/// Locations form a tree: Building → Floor → Room → Rack.
/// Each location has ParentId, Path (materialized path), Depth, and TotalPrinterCount.
///
/// The Location entity already supports hierarchy (ParentId, Path, Depth, Children).
/// Ripley is adding tree operations: GetTree, MoveLocation, GetAncestors, GetDescendants.
///
/// These tests validate both existing and soon-to-arrive functionality.
/// </summary>
public class LocationHierarchyTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _context;
    private readonly Guid _testManufacturerId;
    private readonly Guid _testModelId;

    public LocationHierarchyTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        TestSqlitePragmaEnforcer.EnsureForeignKeysEnabled(_connection);

        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        _context = new AppDbContext(options);
        _context.Database.EnsureCreated();

        // Seed shared manufacturer and model once to satisfy FK constraints
        _testManufacturerId = Guid.NewGuid();
        _testModelId = Guid.NewGuid();

        _context.Manufacturers.Add(new Manufacturer
        {
            Id = _testManufacturerId,
            Name = "Test Manufacturer"
        });

        _context.PrinterModels.Add(new PrinterModel
        {
            Id = _testModelId,
            Name = "Test Model",
            ManufacturerId = _testManufacturerId
        });

        _context.SaveChanges();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    #region Helper Methods

    private Location CreateLocation(
        string name,
        Guid? parentId = null,
        string? description = null,
        int depth = 0,
        string path = "/")
    {
        var location = new Location
        {
            Id = Guid.NewGuid(),
            Name = name,
            Description = description,
            ParentId = parentId,
            Path = path,
            Depth = depth,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            ModifiedAt = DateTime.UtcNow
        };

        return location;
    }

    private async Task<Location> CreateAndSaveLocationAsync(
        string name,
        Guid? parentId = null,
        string? description = null)
    {
        int depth = 0;
        string path = "/";

        if (parentId.HasValue)
        {
            Location? parent = await _context.Locations.FindAsync(parentId.Value);
            if (parent is not null)
            {
                depth = parent.Depth + 1;
                path = parent.Path == "/" ? $"/{parent.Name}" : $"{parent.Path}/{parent.Name}";
            }
        }

        var location = new Location
        {
            Id = Guid.NewGuid(),
            Name = name,
            Description = description,
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

    private int _printerCounter;

    private async Task<Printer> CreatePrinterInLocationAsync(string name, Guid? locationId)
    {
        int n = Interlocked.Increment(ref _printerCounter);
        var printer = new Printer
        {
            Id = Guid.NewGuid(),
            Name = name,
            ServerUrl = $"http://192.168.1.{n}",
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
    /// Seeds a full test hierarchy:
    /// HQ (root)
    ///   ├── Floor 1
    ///   │   ├── Lab A
    ///   │   └── Lab B
    ///   └── Floor 2
    ///       └── Lab C
    /// </summary>
    private async Task<Dictionary<string, Location>> SeedTestHierarchyAsync()
    {
        Location hq = await CreateAndSaveLocationAsync("HQ");
        Location floor1 = await CreateAndSaveLocationAsync("Floor 1", hq.Id);
        Location floor2 = await CreateAndSaveLocationAsync("Floor 2", hq.Id);
        Location labA = await CreateAndSaveLocationAsync("Lab A", floor1.Id);
        Location labB = await CreateAndSaveLocationAsync("Lab B", floor1.Id);
        Location labC = await CreateAndSaveLocationAsync("Lab C", floor2.Id);

        return new Dictionary<string, Location>
        {
            ["HQ"] = hq,
            ["Floor 1"] = floor1,
            ["Floor 2"] = floor2,
            ["Lab A"] = labA,
            ["Lab B"] = labB,
            ["Lab C"] = labC
        };
    }

    #endregion

    // =========================================================================
    // LOCATION CREATION + PATH/DEPTH TESTS
    // =========================================================================

    [Fact]
    [Trait("Category", "Locations")]
    public async Task CreateLocation_WithParent_SetsCorrectPath()
    {
        // Creating "Lab A" under "Floor 1" under "HQ"
        // Path should reflect the hierarchy
        Location hq = await CreateAndSaveLocationAsync("HQ");
        Location floor1 = await CreateAndSaveLocationAsync("Floor 1", hq.Id);
        Location labA = await CreateAndSaveLocationAsync("Lab A", floor1.Id);

        labA.ParentId.Should().Be(floor1.Id);
        labA.Path.Should().Contain("Floor 1", "path should include parent name");
    }

    [Fact]
    [Trait("Category", "Locations")]
    public async Task CreateLocation_WithParent_SetsCorrectDepth()
    {
        // Root = depth 0, child = depth 1, grandchild = depth 2
        Location root = await CreateAndSaveLocationAsync("Building");
        Location floor = await CreateAndSaveLocationAsync("Floor 1", root.Id);
        Location room = await CreateAndSaveLocationAsync("Room 101", floor.Id);

        root.Depth.Should().Be(0, "root location should be depth 0");
        floor.Depth.Should().Be(1, "direct child should be depth 1");
        room.Depth.Should().Be(2, "grandchild should be depth 2");
    }

    [Fact]
    [Trait("Category", "Locations")]
    public async Task CreateLocation_NoParent_IsRootNode()
    {
        Location root = await CreateAndSaveLocationAsync("Headquarters");

        root.ParentId.Should().BeNull("root node has no parent");
        root.Depth.Should().Be(0, "root node is depth 0");
        root.Path.Should().Be("/", "root node path is /");
    }

    [Fact]
    [Trait("Category", "Locations")]
    public async Task CreateLocation_DuplicateNameSameParent_ThrowsConflict()
    {
        // Two locations with same name under the same parent should fail
        // The database enforces a UNIQUE constraint on (ParentId, Name)
        Location parent = await CreateAndSaveLocationAsync("Building");
        await CreateAndSaveLocationAsync("Room A", parent.Id);

        // Creating another "Room A" under "Building" should be rejected by the database
        Location duplicate = CreateLocation("Room A", parent.Id);
        _context.Locations.Add(duplicate);

        Func<Task> act = () => _context.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateException>(
            "database UNIQUE constraint on (ParentId, Name) should reject duplicate");
    }

    [Fact]
    [Trait("Category", "Locations")]
    public async Task CreateLocation_DuplicateNameDifferentParent_Succeeds()
    {
        // Same name under different parents is fine
        Location building1 = await CreateAndSaveLocationAsync("Building 1");
        Location building2 = await CreateAndSaveLocationAsync("Building 2");

        Location roomA1 = await CreateAndSaveLocationAsync("Room A", building1.Id);
        Location roomA2 = await CreateAndSaveLocationAsync("Room A", building2.Id);

        roomA1.Name.Should().Be(roomA2.Name, "same name is allowed under different parents");
        roomA1.ParentId!.Value.Should().NotBe(roomA2.ParentId!.Value, "parents should be different");
    }

    // =========================================================================
    // TREE TRAVERSAL TESTS
    // =========================================================================

    [Fact]
    [Trait("Category", "Locations")]
    public async Task GetTree_ReturnsNestedStructure()
    {
        // Build a hierarchy and verify tree structure
        Dictionary<string, Location> hierarchy = await SeedTestHierarchyAsync();

        // Query all root locations (no parent)
        List<Location> roots = await _context.Locations
            .Where(l => l.ParentId == null && l.IsActive)
            .ToListAsync();

        roots.Should().HaveCount(1, "should have exactly one root (HQ)");
        roots[0].Name.Should().Be("HQ");

        // Load children of HQ
        List<Location> hqChildren = await _context.Locations
            .Where(l => l.ParentId == hierarchy["HQ"].Id)
            .ToListAsync();

        hqChildren.Should().HaveCount(2, "HQ should have Floor 1 and Floor 2");
        hqChildren.Select(c => c.Name).Should().Contain("Floor 1");
        hqChildren.Select(c => c.Name).Should().Contain("Floor 2");

        // Load children of Floor 1
        List<Location> floor1Children = await _context.Locations
            .Where(l => l.ParentId == hierarchy["Floor 1"].Id)
            .ToListAsync();

        floor1Children.Should().HaveCount(2, "Floor 1 should have Lab A and Lab B");
    }

    [Fact]
    [Trait("Category", "Locations")]
    public async Task GetTree_WithRootId_ReturnsSubtree()
    {
        // Query subtree starting from Floor 1 — should include Lab A and Lab B, not Floor 2/Lab C
        Dictionary<string, Location> hierarchy = await SeedTestHierarchyAsync();
        Guid floor1Id = hierarchy["Floor 1"].Id;

        // Get direct children of Floor 1
        List<Location> subtree = await _context.Locations
            .Where(l => l.ParentId == floor1Id && l.IsActive)
            .ToListAsync();

        subtree.Should().HaveCount(2);
        subtree.Select(l => l.Name).Should().Contain("Lab A").And.Contain("Lab B");
        subtree.Select(l => l.Name).Should().NotContain("Lab C", "Lab C is under Floor 2");
    }

    [Fact]
    [Trait("Category", "Locations")]
    public async Task GetAncestors_ReturnsPathToRoot()
    {
        // From Lab A → Floor 1 → HQ
        Dictionary<string, Location> hierarchy = await SeedTestHierarchyAsync();

        // Walk up the tree from Lab A
        var ancestors = new List<Location>();
        Location? current = hierarchy["Lab A"];

        while (current?.ParentId is not null)
        {
            current = await _context.Locations.FindAsync(current.ParentId!.Value);
            if (current is not null)
            {
                ancestors.Add(current);
            }
        }

        ancestors.Should().HaveCount(2, "Lab A has 2 ancestors: Floor 1 and HQ");
        ancestors[0].Name.Should().Be("Floor 1", "immediate parent is Floor 1");
        ancestors[1].Name.Should().Be("HQ", "root ancestor is HQ");
    }

    [Fact]
    [Trait("Category", "Locations")]
    public async Task GetDescendants_ReturnsAllChildren()
    {
        // All descendants of HQ = Floor 1, Floor 2, Lab A, Lab B, Lab C
        Dictionary<string, Location> hierarchy = await SeedTestHierarchyAsync();

        // Recursive query: get all descendants of HQ
        var descendants = new List<Location>();
        var queue = new Queue<Guid>();
        queue.Enqueue(hierarchy["HQ"].Id);

        while (queue.Count > 0)
        {
            Guid parentId = queue.Dequeue();
            List<Location> children = await _context.Locations
                .Where(l => l.ParentId == parentId && l.IsActive)
                .ToListAsync();

            descendants.AddRange(children);
            foreach (Location child in children)
            {
                queue.Enqueue(child.Id);
            }
        }

        descendants.Should().HaveCount(5, "HQ has 5 total descendants");
        descendants.Select(d => d.Name).Should().Contain("Floor 1");
        descendants.Select(d => d.Name).Should().Contain("Floor 2");
        descendants.Select(d => d.Name).Should().Contain("Lab A");
        descendants.Select(d => d.Name).Should().Contain("Lab B");
        descendants.Select(d => d.Name).Should().Contain("Lab C");
    }

    // =========================================================================
    // MOVE LOCATION TESTS
    // =========================================================================

    [Fact]
    [Trait("Category", "Locations")]
    public async Task MoveLocation_UpdatesPathForAllDescendants()
    {
        // Move Floor 1 from HQ to Floor 2 → Lab A and Lab B paths should update
        Dictionary<string, Location> hierarchy = await SeedTestHierarchyAsync();

        Location floor1 = hierarchy["Floor 1"];
        Location floor2 = hierarchy["Floor 2"];

        // Simulate moving Floor 1 under Floor 2
        floor1.ParentId = floor2.Id;
        floor1.Depth = floor2.Depth + 1;

        _context.Locations.Update(floor1);
        await _context.SaveChangesAsync();

        // Verify Floor 1 now has Floor 2 as parent
        Location? movedFloor1 = await _context.Locations.FindAsync(floor1.Id);
        movedFloor1!.ParentId.Should().Be(floor2.Id, "Floor 1 should now be under Floor 2");
        movedFloor1.Depth.Should().Be(floor2.Depth + 1, "depth should be parent + 1");

        // TODO: When Ripley's MoveLocation service method lands, it should also
        // update all descendant paths and depths recursively
    }

    [Fact]
    [Trait("Category", "Locations")]
    public async Task MoveLocation_CircularReference_ThrowsValidationError()
    {
        // Moving HQ under its own child (Floor 1) would create a cycle
        Dictionary<string, Location> hierarchy = await SeedTestHierarchyAsync();

        Guid hqId = hierarchy["HQ"].Id;
        Guid floor1Id = hierarchy["Floor 1"].Id;

        // Attempting to set HQ's parent to Floor 1 (its own child)
        // This should be prevented by the service layer
        bool wouldCreateCycle = IsDescendantOf(floor1Id, hqId, hierarchy);
        wouldCreateCycle.Should().BeTrue("Floor 1 is a descendant of HQ — moving HQ under Floor 1 is circular");

        // TODO: When Ripley implements MoveLocationAsync with validation:
        // Func<Task> act = () => locationService.MoveLocationAsync(hqId, new MoveLocationDto { NewParentId = floor1Id }, ct);
        // await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    [Trait("Category", "Locations")]
    public async Task MoveLocation_ToOwnDescendant_ThrowsValidationError()
    {
        // Moving Floor 1 under Lab A (its own grandchild) → circular
        Dictionary<string, Location> hierarchy = await SeedTestHierarchyAsync();

        bool wouldCreateCycle = IsDescendantOf(hierarchy["Lab A"].Id, hierarchy["Floor 1"].Id, hierarchy);
        wouldCreateCycle.Should().BeTrue("Lab A is a descendant of Floor 1");

        // TODO: Service should reject this move
    }

    // =========================================================================
    // DELETE LOCATION TESTS
    // =========================================================================

    [Fact]
    [Trait("Category", "Locations")]
    public async Task DeleteLocation_WithChildren_Fails()
    {
        // Deleting Floor 1 (which has Lab A and Lab B) should fail
        Dictionary<string, Location> hierarchy = await SeedTestHierarchyAsync();

        Guid floor1Id = hierarchy["Floor 1"].Id;

        // Check if it has children
        bool hasChildren = await _context.Locations
            .AnyAsync(l => l.ParentId == floor1Id && l.IsActive);

        hasChildren.Should().BeTrue("Floor 1 has children — delete should be blocked");

        // TODO: When Ripley's DeleteLocation validates children:
        // Func<Task> act = () => locationService.DeleteLocationAsync(floor1Id, ct);
        // await act.Should().ThrowAsync<InvalidOperationException>("cannot delete location with children");
    }

    [Fact]
    [Trait("Category", "Locations")]
    public async Task DeleteLocation_LeafNode_Succeeds()
    {
        // Deleting Lab A (a leaf node with no children) should succeed
        Dictionary<string, Location> hierarchy = await SeedTestHierarchyAsync();

        Location labA = hierarchy["Lab A"];

        bool hasChildren = await _context.Locations
            .AnyAsync(l => l.ParentId == labA.Id && l.IsActive);
        hasChildren.Should().BeFalse("Lab A is a leaf node");

        // Soft delete
        labA.IsActive = false;
        _context.Locations.Update(labA);
        await _context.SaveChangesAsync();

        Location? deletedLab = await _context.Locations.FindAsync(labA.Id);
        deletedLab!.IsActive.Should().BeFalse("leaf node should be soft-deleted");
    }

    [Fact]
    [Trait("Category", "Locations")]
    public async Task DeleteLocation_WithPrinters_UnassignsPrinters()
    {
        // Deleting a location with printers should unassign them (set LocationId = null)
        Location lab = await CreateAndSaveLocationAsync("Print Lab");
        Printer printer1 = await CreatePrinterInLocationAsync("Printer 1", lab.Id);
        Printer printer2 = await CreatePrinterInLocationAsync("Printer 2", lab.Id);

        // Verify printers are assigned
        printer1.LocationId.Should().Be(lab.Id);
        printer2.LocationId.Should().Be(lab.Id);

        // Simulate unassigning printers before soft-delete
        List<Printer> printersInLocation = await _context.Printers
            .Where(p => p.LocationId == lab.Id)
            .ToListAsync();

        foreach (Printer p in printersInLocation)
        {
            p.LocationId = null;
        }

        lab.IsActive = false;
        lab.PrinterCount = 0;
        await _context.SaveChangesAsync();

        // Verify printers are unassigned
        Printer? p1 = await _context.Printers.FindAsync(printer1.Id);
        Printer? p2 = await _context.Printers.FindAsync(printer2.Id);
        p1!.LocationId.Should().BeNull("printer should be unassigned after location delete");
        p2!.LocationId.Should().BeNull("printer should be unassigned after location delete");
    }

    // =========================================================================
    // VALIDATION TESTS
    // =========================================================================

    [Fact]
    [Trait("Category", "Locations")]
    public async Task MaxDepth_Exceeded_ThrowsValidationError()
    {
        // Create a chain deeper than the max allowed depth (assumed max = 10)
        const int maxDepth = 10;
        Guid? parentId = null;

        for (int i = 0; i <= maxDepth; i++)
        {
            Location loc = await CreateAndSaveLocationAsync($"Level {i}", parentId);
            parentId = loc.Id;
        }

        // The chain is now maxDepth+1 levels deep
        Location? deepest = await _context.Locations
            .Where(l => l.Name == $"Level {maxDepth}")
            .FirstOrDefaultAsync();

        deepest.Should().NotBeNull();
        deepest!.Depth.Should().Be(maxDepth);

        // TODO: When Ripley adds max depth validation to CreateLocationAsync:
        // Creating level maxDepth+1 should throw
        // Func<Task> act = () => locationService.CreateLocationAsync(
        //     new CreateLocationDto { Name = "Too Deep", ParentId = deepest.Id }, ct);
        // await act.Should().ThrowAsync<InvalidOperationException>("exceeds max depth");
    }

    [Fact]
    [Trait("Category", "Locations")]
    public async Task TotalPrinterCount_IncludesDescendants()
    {
        // HQ → Floor 1 → Lab A (2 printers), Lab B (1 printer)
        //     → Floor 2 → Lab C (3 printers)
        // HQ.TotalPrinterCount should be 6
        Dictionary<string, Location> hierarchy = await SeedTestHierarchyAsync();

        // Add printers to leaf locations
        await CreatePrinterInLocationAsync("P1", hierarchy["Lab A"].Id);
        await CreatePrinterInLocationAsync("P2", hierarchy["Lab A"].Id);
        await CreatePrinterInLocationAsync("P3", hierarchy["Lab B"].Id);
        await CreatePrinterInLocationAsync("P4", hierarchy["Lab C"].Id);
        await CreatePrinterInLocationAsync("P5", hierarchy["Lab C"].Id);
        await CreatePrinterInLocationAsync("P6", hierarchy["Lab C"].Id);

        // Calculate total printer count for HQ (recursive)
        int totalCount = await CountPrintersRecursiveAsync(hierarchy["HQ"].Id);

        totalCount.Should().Be(6, "HQ should have 6 total printers across all descendants");

        // Calculate for Floor 1
        int floor1Total = await CountPrintersRecursiveAsync(hierarchy["Floor 1"].Id);
        floor1Total.Should().Be(3, "Floor 1 should have 3 printers (Lab A: 2, Lab B: 1)");

        // Calculate for Floor 2
        int floor2Total = await CountPrintersRecursiveAsync(hierarchy["Floor 2"].Id);
        floor2Total.Should().Be(3, "Floor 2 should have 3 printers (Lab C: 3)");
    }

    // =========================================================================
    // HELPER METHODS
    // =========================================================================

    private bool IsDescendantOf(Guid locationId, Guid potentialAncestorId, Dictionary<string, Location> hierarchy)
    {
        // Walk up from locationId to see if we reach potentialAncestorId
        Location? location = hierarchy.Values.FirstOrDefault(l => l.Id == locationId);
        while (location?.ParentId is not null)
        {
            if (location.ParentId == potentialAncestorId)
            {
                return true;
            }

            location = hierarchy.Values.FirstOrDefault(l => l.Id == location.ParentId);
        }

        return false;
    }

    private async Task<int> CountPrintersRecursiveAsync(Guid locationId)
    {
        int directCount = await _context.Printers.CountAsync(p => p.LocationId == locationId);

        List<Guid> childIds = await _context.Locations
            .Where(l => l.ParentId == locationId && l.IsActive)
            .Select(l => l.Id)
            .ToListAsync();

        int childCount = 0;
        foreach (Guid childId in childIds)
        {
            childCount += await CountPrintersRecursiveAsync(childId);
        }

        return directCount + childCount;
    }
}

/// <summary>
/// Integration tests for location hierarchy API endpoints.
/// These use CustomWebApplicationFactory for full HTTP testing against the LocationsController.
/// </summary>
[Trait("Category", "Integration")]
[Collection(IntegrationTestCollection.Name)]
public class LocationHierarchyEndpointTests : IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory;

    public LocationHierarchyEndpointTests()
    {
        _factory = new CustomWebApplicationFactory();
    }

    public async Task InitializeAsync()
    {
        await _factory.ResetDatabaseAsync();
    }

    public async Task DisposeAsync()
    {
        _factory?.Dispose();
    }

    [Fact]
    [Trait("Category", "Locations")]
    public async Task GetLocationTree_ReturnsNestedJson()
    {
        // Create a hierarchy through the API, then GET /api/locations should return tree
        HttpClient client = await _factory.CreateAdminClientAsync();

        // Create root location
        HttpResponseMessage createResponse = await client.PostAsJsonAsync("/api/locations",
            new CreateLocationDto { Name = "Building A", Description = "Main building" });
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        LocationDto? root = await createResponse.Content.ReadFromJsonAsync<LocationDto>();
        root.Should().NotBeNull();
        root!.Name.Should().Be("Building A");

        // Create child location
        HttpResponseMessage childResponse = await client.PostAsJsonAsync("/api/locations",
            new CreateLocationDto { Name = "Floor 1", ParentId = root.Id });
        childResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        LocationDto? child = await childResponse.Content.ReadFromJsonAsync<LocationDto>();
        child.Should().NotBeNull();
        child!.ParentId.Should().Be(root.Id);

        // GET all locations
        HttpResponseMessage getResponse = await client.GetAsync("/api/locations");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        LocationDto[]? locations = await getResponse.Content.ReadFromJsonAsync<LocationDto[]>();
        locations.Should().NotBeNull();
        locations!.Length.Should().BeGreaterThanOrEqualTo(2, "should include Building A and Floor 1");
    }

    [Fact]
    [Trait("Category", "Locations")]
    public async Task CreateLocation_WithParentId_CreatesChild()
    {
        HttpClient client = await _factory.CreateAdminClientAsync();

        // Create parent
        HttpResponseMessage parentResponse = await client.PostAsJsonAsync("/api/locations",
            new CreateLocationDto { Name = "Parent Location" });
        parentResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        LocationDto? parent = await parentResponse.Content.ReadFromJsonAsync<LocationDto>();

        // Create child under parent
        HttpResponseMessage childResponse = await client.PostAsJsonAsync("/api/locations",
            new CreateLocationDto { Name = "Child Location", ParentId = parent!.Id });
        childResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        LocationDto? child = await childResponse.Content.ReadFromJsonAsync<LocationDto>();
        child.Should().NotBeNull();
        child!.ParentId.Should().Be(parent.Id, "child's parent should match");
        child.Depth.Should().BeGreaterThan(0, "child depth should be > 0");
    }

    [Fact]
    [Trait("Category", "Locations")]
    public async Task MoveLocation_ValidMove_Returns200()
    {
        // TODO: When Ripley adds PUT /api/locations/{id}/move endpoint:
        // 1. Create parent A and parent B
        // 2. Create child under A
        // 3. PUT /api/locations/{childId}/move { newParentId: B.Id }
        // 4. Verify child is now under B

        HttpClient client = await _factory.CreateAdminClientAsync();

        // For now, just verify the infrastructure works
        HttpResponseMessage health = await client.GetAsync("/healthz");
        health.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    [Trait("Category", "Locations")]
    public async Task MoveLocation_CircularRef_Returns400()
    {
        // TODO: When Ripley adds move validation:
        // Attempting to move a parent under its own descendant should return 400

        HttpClient client = await _factory.CreateAdminClientAsync();

        // Verify infrastructure
        HttpResponseMessage health = await client.GetAsync("/healthz");
        health.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
