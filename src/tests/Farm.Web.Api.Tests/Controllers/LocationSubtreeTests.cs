using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Locations;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Farm.Web.Api.Tests.Controllers;

/// <summary>
/// Integration tests for the Location subtree endpoint: GET /api/locations/{id}/printers/subtree
/// Verifies that the endpoint returns printers from a location and all its descendant locations.
/// Uses CustomWebApplicationFactory with in-memory SQLite for isolated test data.
/// </summary>
public class LocationSubtreeTests : IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory;
    private HttpClient _client = null!;

    public LocationSubtreeTests()
    {
        _factory = new CustomWebApplicationFactory();
    }

    public async Task InitializeAsync()
    {
        _client = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        await _factory.DisposeAsync();
    }

    private static string UniquePrinterServerUrl()
    {
        return $"http://printer-{Guid.NewGuid():N}.test";
    }

    // =========================================================================
    // GET /api/locations/{id}/printers/subtree
    // =========================================================================

    [Fact]
    [Trait("Category", "Location")]
    public async Task GetSubtreePrinters_WithLocationAndDescendants_ReturnsAllPrintersInSubtree()
    {
        // Arrange: Create location hierarchy
        //   Warehouse (root)
        //     ├─ Room A (child)
        //     └─ Room B (child)
        // Assign printers to all three locations
        Guid warehouseId, roomAId, roomBId;
        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // Seed manufacturer and model
            Manufacturer manufacturer = new()
            {
                Id = Guid.NewGuid(),
                Name = $"TestManufacturer_{Guid.NewGuid():N}",
                Url = "https://test.com"
            };
            db.Manufacturers.Add(manufacturer);

            PrinterModel model = new()
            {
                Id = Guid.NewGuid(),
                ManufacturerId = manufacturer.Id,
                Name = "Test Model",
            };
            db.PrinterModels.Add(model);

            // Create location hierarchy
            Location warehouse = new()
            {
                Id = Guid.NewGuid(),
                Name = "Warehouse",
                ParentId = null,
                Depth = 0,
                Path = "/Warehouse"
            };
            db.Locations.Add(warehouse);
            warehouseId = warehouse.Id;

            Location roomA = new()
            {
                Id = Guid.NewGuid(),
                Name = "Room A",
                ParentId = warehouseId,
                Depth = 1,
                Path = "/Warehouse/Room A"
            };
            db.Locations.Add(roomA);
            roomAId = roomA.Id;

            Location roomB = new()
            {
                Id = Guid.NewGuid(),
                Name = "Room B",
                ParentId = warehouseId,
                Depth = 1,
                Path = "/Warehouse/Room B"
            };
            db.Locations.Add(roomB);
            roomBId = roomB.Id;

            // Create printers in each location
            Printer printer1 = new()
            {
                Id = Guid.NewGuid(),
                Name = "Printer-Warehouse",
                ServerUrl = UniquePrinterServerUrl(),
                Backend = (int)PrinterBackend.Moonraker,
                ModelId = model.Id,
                ManufacturerId = manufacturer.Id,
                LocationId = warehouseId,
                IsEnabled = true,
                IsAvailable = true
            };

            Printer printer2 = new()
            {
                Id = Guid.NewGuid(),
                Name = "Printer-RoomA",
                ServerUrl = UniquePrinterServerUrl(),
                Backend = (int)PrinterBackend.PrusaLink,
                ModelId = model.Id,
                ManufacturerId = manufacturer.Id,
                LocationId = roomAId,
                IsEnabled = true,
                IsAvailable = true
            };

            Printer printer3 = new()
            {
                Id = Guid.NewGuid(),
                Name = "Printer-RoomB",
                ServerUrl = UniquePrinterServerUrl(),
                Backend = (int)PrinterBackend.OctoPrint,
                ModelId = model.Id,
                ManufacturerId = manufacturer.Id,
                LocationId = roomBId,
                IsEnabled = true,
                IsAvailable = true
            };

            db.Printers.AddRange(printer1, printer2, printer3);
            await db.SaveChangesAsync();
        }

        // Act: Get subtree printers for warehouse (should include all 3 printers)
        HttpResponseMessage response = await _client.GetAsync($"/api/locations/{warehouseId}/printers/subtree");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        List<LocationSubtreePrinterDto>? printers = await response.Content.ReadFromJsonAsync<List<LocationSubtreePrinterDto>>();
        printers.Should().NotBeNull().And.HaveCount(3, "warehouse subtree includes all 3 printers");
        printers!.Should().Contain(p => p.PrinterName == "Printer-Warehouse");
        printers.Should().Contain(p => p.PrinterName == "Printer-RoomA");
        printers.Should().Contain(p => p.PrinterName == "Printer-RoomB");
    }

    [Fact]
    [Trait("Category", "Location")]
    public async Task GetSubtreePrinters_WithLeafLocation_ReturnsOnlyPrintersInThatLocation()
    {
        // Arrange: Create hierarchy where leaf location has printers
        //   Warehouse
        //     └─ Room A (leaf with printers)
        Guid warehouseId, roomAId;
        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            Manufacturer manufacturer = new()
            {
                Id = Guid.NewGuid(),
                Name = $"TestManufacturer_{Guid.NewGuid():N}",
                Url = "https://test.com"
            };
            db.Manufacturers.Add(manufacturer);

            PrinterModel model = new()
            {
                Id = Guid.NewGuid(),
                ManufacturerId = manufacturer.Id,
                Name = "Test Model",
            };
            db.PrinterModels.Add(model);

            Location warehouse = new()
            {
                Id = Guid.NewGuid(),
                Name = "Warehouse",
                ParentId = null,
                Depth = 0,
                Path = "/Warehouse"
            };
            db.Locations.Add(warehouse);
            warehouseId = warehouse.Id;

            Location roomA = new()
            {
                Id = Guid.NewGuid(),
                Name = "Room A",
                ParentId = warehouseId,
                Depth = 1,
                Path = "/Warehouse/Room A"
            };
            db.Locations.Add(roomA);
            roomAId = roomA.Id;

            // Only Room A has printers
            Printer printer1 = new()
            {
                Id = Guid.NewGuid(),
                Name = "Printer-RoomA-1",
                ServerUrl = UniquePrinterServerUrl(),
                Backend = (int)PrinterBackend.Moonraker,
                ModelId = model.Id,
                ManufacturerId = manufacturer.Id,
                LocationId = roomAId,
                IsEnabled = true,
                IsAvailable = true
            };

            Printer printer2 = new()
            {
                Id = Guid.NewGuid(),
                Name = "Printer-RoomA-2",
                ServerUrl = UniquePrinterServerUrl(),
                Backend = (int)PrinterBackend.PrusaLink,
                ModelId = model.Id,
                ManufacturerId = manufacturer.Id,
                LocationId = roomAId,
                IsEnabled = true,
                IsAvailable = true
            };

            db.Printers.AddRange(printer1, printer2);
            await db.SaveChangesAsync();
        }

        // Act: Get subtree printers for Room A (leaf location)
        HttpResponseMessage response = await _client.GetAsync($"/api/locations/{roomAId}/printers/subtree");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        List<LocationSubtreePrinterDto>? printers = await response.Content.ReadFromJsonAsync<List<LocationSubtreePrinterDto>>();
        printers.Should().NotBeNull().And.HaveCount(2, "leaf location has 2 printers");
        printers!.Should().Contain(p => p.PrinterName == "Printer-RoomA-1");
        printers.Should().Contain(p => p.PrinterName == "Printer-RoomA-2");
    }

    [Fact]
    [Trait("Category", "Location")]
    public async Task GetSubtreePrinters_WithLocationWithNoPrinters_ReturnsEmptyArray()
    {
        // Arrange: Create location with no printers
        Guid locationId;
        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            Location location = new()
            {
                Id = Guid.NewGuid(),
                Name = "Empty Location",
                ParentId = null,
                Depth = 0,
                Path = "/Empty Location"
            };
            db.Locations.Add(location);
            await db.SaveChangesAsync();
            locationId = location.Id;
        }

        // Act
        HttpResponseMessage response = await _client.GetAsync($"/api/locations/{locationId}/printers/subtree");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        List<LocationSubtreePrinterDto>? printers = await response.Content.ReadFromJsonAsync<List<LocationSubtreePrinterDto>>();
        printers.Should().NotBeNull().And.BeEmpty("location has no printers");
    }

    [Fact]
    [Trait("Category", "Location")]
    public async Task GetSubtreePrinters_WithNonexistentLocation_ReturnsEmptyArray()
    {
        // Act: Query nonexistent location
        Guid nonexistentId = Guid.NewGuid();
        HttpResponseMessage response = await _client.GetAsync($"/api/locations/{nonexistentId}/printers/subtree");

        // Assert: Service returns empty array for nonexistent location (graceful degradation)
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        List<LocationSubtreePrinterDto>? printers = await response.Content.ReadFromJsonAsync<List<LocationSubtreePrinterDto>>();
        printers.Should().NotBeNull().And.BeEmpty("nonexistent location has no printers");
    }

    [Fact]
    [Trait("Category", "Location")]
    public async Task GetSubtreePrinters_ExcludesPrintersFromSiblingLocations()
    {
        // Arrange: Create hierarchy with sibling locations
        //   Warehouse
        //     ├─ Room A (has printers)
        //     └─ Room B (has printers)
        // Query Room A subtree should NOT include Room B printers
        Guid warehouseId, roomAId, roomBId;
        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            Manufacturer manufacturer = new()
            {
                Id = Guid.NewGuid(),
                Name = $"TestManufacturer_{Guid.NewGuid():N}",
                Url = "https://test.com"
            };
            db.Manufacturers.Add(manufacturer);

            PrinterModel model = new()
            {
                Id = Guid.NewGuid(),
                ManufacturerId = manufacturer.Id,
                Name = "Test Model",
            };
            db.PrinterModels.Add(model);

            Location warehouse = new()
            {
                Id = Guid.NewGuid(),
                Name = "Warehouse",
                ParentId = null,
                Depth = 0,
                Path = "/Warehouse"
            };
            db.Locations.Add(warehouse);
            warehouseId = warehouse.Id;

            Location roomA = new()
            {
                Id = Guid.NewGuid(),
                Name = "Room A",
                ParentId = warehouseId,
                Depth = 1,
                Path = "/Warehouse/Room A"
            };
            db.Locations.Add(roomA);
            roomAId = roomA.Id;

            Location roomB = new()
            {
                Id = Guid.NewGuid(),
                Name = "Room B",
                ParentId = warehouseId,
                Depth = 1,
                Path = "/Warehouse/Room B"
            };
            db.Locations.Add(roomB);
            roomBId = roomB.Id;

            // Room A printer
            Printer printerA = new()
            {
                Id = Guid.NewGuid(),
                Name = "Printer-RoomA",
                ServerUrl = UniquePrinterServerUrl(),
                Backend = (int)PrinterBackend.Moonraker,
                ModelId = model.Id,
                ManufacturerId = manufacturer.Id,
                LocationId = roomAId,
                IsEnabled = true,
                IsAvailable = true
            };

            // Room B printer
            Printer printerB = new()
            {
                Id = Guid.NewGuid(),
                Name = "Printer-RoomB",
                ServerUrl = UniquePrinterServerUrl(),
                Backend = (int)PrinterBackend.PrusaLink,
                ModelId = model.Id,
                ManufacturerId = manufacturer.Id,
                LocationId = roomBId,
                IsEnabled = true,
                IsAvailable = true
            };

            db.Printers.AddRange(printerA, printerB);
            await db.SaveChangesAsync();
        }

        // Act: Get subtree printers for Room A only
        HttpResponseMessage response = await _client.GetAsync($"/api/locations/{roomAId}/printers/subtree");

        // Assert: Should only include Room A printer, not Room B
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        List<LocationSubtreePrinterDto>? printers = await response.Content.ReadFromJsonAsync<List<LocationSubtreePrinterDto>>();
        printers.Should().NotBeNull().And.HaveCount(1, "Room A subtree has only 1 printer");
        printers!.Should().Contain(p => p.PrinterName == "Printer-RoomA");
        printers.Should().NotContain(p => p.PrinterName == "Printer-RoomB", "sibling printers should be excluded");
    }

    [Fact]
    [Trait("Category", "Location")]
    public async Task GetSubtreePrinters_WithDeepHierarchy_ReturnsAllDescendantPrinters()
    {
        // Arrange: Create 3-level hierarchy
        //   Warehouse
        //     └─ Room A
        //         └─ Rack 1
        // All three levels have printers
        Guid warehouseId, roomAId, rack1Id;
        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            Manufacturer manufacturer = new()
            {
                Id = Guid.NewGuid(),
                Name = $"TestManufacturer_{Guid.NewGuid():N}",
                Url = "https://test.com"
            };
            db.Manufacturers.Add(manufacturer);

            PrinterModel model = new()
            {
                Id = Guid.NewGuid(),
                ManufacturerId = manufacturer.Id,
                Name = "Test Model",
            };
            db.PrinterModels.Add(model);

            Location warehouse = new()
            {
                Id = Guid.NewGuid(),
                Name = "Warehouse",
                ParentId = null,
                Depth = 0,
                Path = "/Warehouse"
            };
            db.Locations.Add(warehouse);
            warehouseId = warehouse.Id;

            Location roomA = new()
            {
                Id = Guid.NewGuid(),
                Name = "Room A",
                ParentId = warehouseId,
                Depth = 1,
                Path = "/Warehouse/Room A"
            };
            db.Locations.Add(roomA);
            roomAId = roomA.Id;

            Location rack1 = new()
            {
                Id = Guid.NewGuid(),
                Name = "Rack 1",
                ParentId = roomAId,
                Depth = 2,
                Path = "/Warehouse/Room A/Rack 1"
            };
            db.Locations.Add(rack1);
            rack1Id = rack1.Id;

            // Printers at each level
            Printer printerWarehouse = new()
            {
                Id = Guid.NewGuid(),
                Name = "Printer-Warehouse",
                ServerUrl = UniquePrinterServerUrl(),
                Backend = (int)PrinterBackend.Moonraker,
                ModelId = model.Id,
                ManufacturerId = manufacturer.Id,
                LocationId = warehouseId,
                IsEnabled = true,
                IsAvailable = true
            };

            Printer printerRoomA = new()
            {
                Id = Guid.NewGuid(),
                Name = "Printer-RoomA",
                ServerUrl = UniquePrinterServerUrl(),
                Backend = (int)PrinterBackend.PrusaLink,
                ModelId = model.Id,
                ManufacturerId = manufacturer.Id,
                LocationId = roomAId,
                IsEnabled = true,
                IsAvailable = true
            };

            Printer printerRack1 = new()
            {
                Id = Guid.NewGuid(),
                Name = "Printer-Rack1",
                ServerUrl = UniquePrinterServerUrl(),
                Backend = (int)PrinterBackend.OctoPrint,
                ModelId = model.Id,
                ManufacturerId = manufacturer.Id,
                LocationId = rack1Id,
                IsEnabled = true,
                IsAvailable = true
            };

            db.Printers.AddRange(printerWarehouse, printerRoomA, printerRack1);
            await db.SaveChangesAsync();
        }

        // Act: Get subtree printers for warehouse (should include all descendants)
        HttpResponseMessage response = await _client.GetAsync($"/api/locations/{warehouseId}/printers/subtree");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        List<LocationSubtreePrinterDto>? printers = await response.Content.ReadFromJsonAsync<List<LocationSubtreePrinterDto>>();
        printers.Should().NotBeNull().And.HaveCount(3, "warehouse subtree includes all 3 levels");
        printers!.Should().Contain(p => p.PrinterName == "Printer-Warehouse");
        printers.Should().Contain(p => p.PrinterName == "Printer-RoomA");
        printers.Should().Contain(p => p.PrinterName == "Printer-Rack1");
    }
}
