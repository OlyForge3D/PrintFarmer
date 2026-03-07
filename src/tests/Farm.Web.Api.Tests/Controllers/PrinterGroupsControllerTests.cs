#pragma warning disable CA5394 // Random is adequate for test data generation
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.PrinterGroups;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Farm.Web.Api.Tests.Controllers;

/// <summary>
/// Integration tests for PrinterGroupsController endpoints.
/// Tests CRUD operations, printer assignment/removal, authorization, and validation.
/// Uses CustomWebApplicationFactory with in-memory SQLite for isolated test data.
/// </summary>
public class PrinterGroupsControllerTests : IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory;
    private HttpClient _authenticatedClient = null!;
    private HttpClient _unauthenticatedClient = null!;

    public PrinterGroupsControllerTests()
    {
        _factory = new CustomWebApplicationFactory();
    }

    public async Task InitializeAsync()
    {
        _authenticatedClient = await _factory.CreateAdminClientAsync();
        _unauthenticatedClient = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        _authenticatedClient?.Dispose();
        _unauthenticatedClient?.Dispose();
        await _factory.DisposeAsync();
    }

    // =========================================================================
    // GET /api/printer-groups — List all groups
    // =========================================================================

    [Fact]
    [Trait("Category", "PrinterGroups")]
    public async Task ListGroups_WithNoGroups_ReturnsEmptyArray()
    {
        HttpResponseMessage response = await _authenticatedClient.GetAsync("/api/printer-groups");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        List<PrinterGroupDto>? groups = await response.Content.ReadFromJsonAsync<List<PrinterGroupDto>>();
        groups.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    [Trait("Category", "PrinterGroups")]
    public async Task ListGroups_WithExistingGroups_ReturnsAllGroups()
    {
        // Arrange: Seed two printer groups
        using IServiceScope scope = _factory.Services.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        PrinterGroup group1 = new()
        {
            Id = Guid.NewGuid(),
            Name = "Prusa MK4 Fleet",
            Description = "All MK4 printers",
            CreatedDate = DateTimeOffset.UtcNow,
            UpdatedDate = DateTimeOffset.UtcNow
        };
        PrinterGroup group2 = new()
        {
            Id = Guid.NewGuid(),
            Name = "Bambu X1C Fleet",
            Description = "Carbon fiber printers",
            CreatedDate = DateTimeOffset.UtcNow,
            UpdatedDate = DateTimeOffset.UtcNow
        };

        db.PrinterGroups.AddRange(group1, group2);
        await db.SaveChangesAsync();

        // Act
        HttpResponseMessage response = await _authenticatedClient.GetAsync("/api/printer-groups");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        List<PrinterGroupDto>? groups = await response.Content.ReadFromJsonAsync<List<PrinterGroupDto>>();
        groups.Should().NotBeNull().And.HaveCount(2);
        groups!.Should().Contain(g => g.Name == "Prusa MK4 Fleet");
        groups.Should().Contain(g => g.Name == "Bambu X1C Fleet");
    }

    // =========================================================================
    // GET /api/printer-groups/{id} — Get group by ID
    // =========================================================================

    [Fact]
    [Trait("Category", "PrinterGroups")]
    public async Task GetGroup_WithValidId_ReturnsDetailWithPrinters()
    {
        // Arrange: Seed group + printers
        Guid groupId;
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

            PrinterGroup group = new()
            {
                Id = Guid.NewGuid(),
                Name = "Test Group",
                Description = "Test Description",
                CreatedDate = DateTimeOffset.UtcNow,
                UpdatedDate = DateTimeOffset.UtcNow
            };
            db.PrinterGroups.Add(group);
            groupId = group.Id;

            Printer printer1 = new()
            {
                Id = Guid.NewGuid(),
                Name = "Printer1",
                ServerUrl = $"http://192.168.1.{new Random().Next(1, 254)}",
                Backend = (int)PrinterBackend.Moonraker,
                ModelId = model.Id,
                ManufacturerId = manufacturer.Id,
                PrinterGroupId = groupId,
                IsEnabled = true,
                IsAvailable = true,
                InMaintenance = false
            };
            Printer printer2 = new()
            {
                Id = Guid.NewGuid(),
                Name = "Printer2",
                ServerUrl = $"http://192.168.1.{new Random().Next(1, 254)}",
                Backend = (int)PrinterBackend.PrusaLink,
                ModelId = model.Id,
                ManufacturerId = manufacturer.Id,
                PrinterGroupId = groupId,
                IsEnabled = true,
                IsAvailable = true,
                InMaintenance = false
            };
            db.Printers.AddRange(printer1, printer2);

            await db.SaveChangesAsync();
        }

        // Act
        HttpResponseMessage response = await _authenticatedClient.GetAsync($"/api/printer-groups/{groupId}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        PrinterGroupDetailDto? detail = await response.Content.ReadFromJsonAsync<PrinterGroupDetailDto>();
        detail.Should().NotBeNull();
        detail!.Name.Should().Be("Test Group");
        detail.Printers.Should().HaveCount(2);
        detail.Printers.Should().Contain(p => p.Name == "Printer1");
        detail.Printers.Should().Contain(p => p.Name == "Printer2");
    }

    [Fact]
    [Trait("Category", "PrinterGroups")]
    public async Task GetGroup_WithNonexistentId_Returns404()
    {
        Guid nonexistentId = Guid.NewGuid();

        HttpResponseMessage response = await _authenticatedClient.GetAsync($"/api/printer-groups/{nonexistentId}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // =========================================================================
    // POST /api/printer-groups — Create group
    // =========================================================================

    [Fact]
    [Trait("Category", "PrinterGroups")]
    public async Task CreateGroup_WithValidName_Returns201AndCreatedGroup()
    {
        CreatePrinterGroupDto dto = new() { Name = "New Fleet", Description = "Test fleet" };

        HttpResponseMessage response = await _authenticatedClient.PostAsJsonAsync("/api/printer-groups", dto);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        PrinterGroupDto? created = await response.Content.ReadFromJsonAsync<PrinterGroupDto>();
        created.Should().NotBeNull();
        created!.Name.Should().Be("New Fleet");
        created.Description.Should().Be("Test fleet");
        created.Id.Should().NotBeEmpty();
        response.Headers.Location.Should().NotBeNull();
    }

    [Fact]
    [Trait("Category", "PrinterGroups")]
    public async Task CreateGroup_WithEmptyName_Returns400()
    {
        CreatePrinterGroupDto dto = new() { Name = "", Description = "Invalid" };

        HttpResponseMessage response = await _authenticatedClient.PostAsJsonAsync("/api/printer-groups", dto);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    [Trait("Category", "PrinterGroups")]
    public async Task CreateGroup_WithWhitespaceName_Returns400()
    {
        CreatePrinterGroupDto dto = new() { Name = "   ", Description = "Invalid" };

        HttpResponseMessage response = await _authenticatedClient.PostAsJsonAsync("/api/printer-groups", dto);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    [Trait("Category", "PrinterGroups")]
    public async Task CreateGroup_WithDuplicateName_Returns409()
    {
        // Arrange: Create first group
        CreatePrinterGroupDto dto = new() { Name = "Duplicate Name", Description = "First" };
        await _authenticatedClient.PostAsJsonAsync("/api/printer-groups", dto);

        // Act: Try to create second group with same name
        CreatePrinterGroupDto duplicate = new() { Name = "Duplicate Name", Description = "Second" };
        HttpResponseMessage response = await _authenticatedClient.PostAsJsonAsync("/api/printer-groups", duplicate);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    [Trait("Category", "PrinterGroups")]
    public async Task CreateGroup_WithoutAuthentication_Returns401()
    {
        CreatePrinterGroupDto dto = new() { Name = "Test Group", Description = "Test" };

        HttpResponseMessage response = await _unauthenticatedClient.PostAsJsonAsync("/api/printer-groups", dto);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // =========================================================================
    // PUT /api/printer-groups/{id} — Update group
    // =========================================================================

    [Fact]
    [Trait("Category", "PrinterGroups")]
    public async Task UpdateGroup_WithValidData_Returns200AndUpdatedGroup()
    {
        // Arrange: Create a group
        Guid groupId;
        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            PrinterGroup group = new()
            {
                Id = Guid.NewGuid(),
                Name = "Original Name",
                Description = "Original Description",
                CreatedDate = DateTimeOffset.UtcNow,
                UpdatedDate = DateTimeOffset.UtcNow
            };
            db.PrinterGroups.Add(group);
            await db.SaveChangesAsync();
            groupId = group.Id;
        }

        // Act: Update the group
        UpdatePrinterGroupDto update = new() { Name = "Updated Name", Description = "Updated Description" };
        HttpResponseMessage response = await _authenticatedClient.PutAsJsonAsync($"/api/printer-groups/{groupId}", update);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        PrinterGroupDto? updated = await response.Content.ReadFromJsonAsync<PrinterGroupDto>();
        updated.Should().NotBeNull();
        updated!.Name.Should().Be("Updated Name");
        updated.Description.Should().Be("Updated Description");
    }

    [Fact]
    [Trait("Category", "PrinterGroups")]
    public async Task UpdateGroup_WithEmptyName_Returns400()
    {
        // Arrange: Create a group
        Guid groupId;
        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            PrinterGroup group = new()
            {
                Id = Guid.NewGuid(),
                Name = "Test Group",
                Description = "Test",
                CreatedDate = DateTimeOffset.UtcNow,
                UpdatedDate = DateTimeOffset.UtcNow
            };
            db.PrinterGroups.Add(group);
            await db.SaveChangesAsync();
            groupId = group.Id;
        }

        // Act: Try to update with empty name
        UpdatePrinterGroupDto update = new() { Name = "", Description = "Invalid" };
        HttpResponseMessage response = await _authenticatedClient.PutAsJsonAsync($"/api/printer-groups/{groupId}", update);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    [Trait("Category", "PrinterGroups")]
    public async Task UpdateGroup_WithNonexistentId_Returns404()
    {
        Guid nonexistentId = Guid.NewGuid();
        UpdatePrinterGroupDto update = new() { Name = "New Name", Description = "Test" };

        HttpResponseMessage response = await _authenticatedClient.PutAsJsonAsync($"/api/printer-groups/{nonexistentId}", update);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    [Trait("Category", "PrinterGroups")]
    public async Task UpdateGroup_WithDuplicateName_Returns409()
    {
        // Arrange: Create two groups
        Guid groupToUpdateId;
        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            PrinterGroup group1 = new()
            {
                Id = Guid.NewGuid(),
                Name = "Existing Name",
                CreatedDate = DateTimeOffset.UtcNow,
                UpdatedDate = DateTimeOffset.UtcNow
            };
            PrinterGroup group2 = new()
            {
                Id = Guid.NewGuid(),
                Name = "Other Group",
                CreatedDate = DateTimeOffset.UtcNow,
                UpdatedDate = DateTimeOffset.UtcNow
            };
            db.PrinterGroups.AddRange(group1, group2);
            await db.SaveChangesAsync();
            groupToUpdateId = group2.Id;
        }

        // Act: Try to rename group2 to match group1's name
        UpdatePrinterGroupDto update = new() { Name = "Existing Name", Description = "Conflict" };
        HttpResponseMessage response = await _authenticatedClient.PutAsJsonAsync($"/api/printer-groups/{groupToUpdateId}", update);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    [Trait("Category", "PrinterGroups")]
    public async Task UpdateGroup_WithoutAuthentication_Returns401()
    {
        Guid groupId = Guid.NewGuid();
        UpdatePrinterGroupDto update = new() { Name = "Test", Description = "Test" };

        HttpResponseMessage response = await _unauthenticatedClient.PutAsJsonAsync($"/api/printer-groups/{groupId}", update);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // =========================================================================
    // DELETE /api/printer-groups/{id} — Delete group
    // =========================================================================

    [Fact]
    [Trait("Category", "PrinterGroups")]
    public async Task DeleteGroup_WithValidId_Returns204()
    {
        // Arrange: Create a group
        Guid groupId;
        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            PrinterGroup group = new()
            {
                Id = Guid.NewGuid(),
                Name = "Group to Delete",
                CreatedDate = DateTimeOffset.UtcNow,
                UpdatedDate = DateTimeOffset.UtcNow
            };
            db.PrinterGroups.Add(group);
            await db.SaveChangesAsync();
            groupId = group.Id;
        }

        // Act
        HttpResponseMessage response = await _authenticatedClient.DeleteAsync($"/api/printer-groups/{groupId}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Verify deletion
        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            PrinterGroup? deleted = await db.PrinterGroups.FindAsync(groupId);
            deleted.Should().BeNull("group should be deleted");
        }
    }

    [Fact]
    [Trait("Category", "PrinterGroups")]
    public async Task DeleteGroup_WithNonexistentId_Returns404()
    {
        Guid nonexistentId = Guid.NewGuid();

        HttpResponseMessage response = await _authenticatedClient.DeleteAsync($"/api/printer-groups/{nonexistentId}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    [Trait("Category", "PrinterGroups")]
    public async Task DeleteGroup_WithoutAuthentication_Returns401()
    {
        Guid groupId = Guid.NewGuid();

        HttpResponseMessage response = await _unauthenticatedClient.DeleteAsync($"/api/printer-groups/{groupId}");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // =========================================================================
    // PUT /api/printer-groups/{id}/printers/{printerId} — Add printer to group
    // =========================================================================

    [Fact]
    [Trait("Category", "PrinterGroups")]
    public async Task AddPrinterToGroup_WithValidIds_Returns204()
    {
        // Arrange: Create group and printer
        Guid groupId, printerId;
        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            Manufacturer manufacturer = new()
            {
                Id = Guid.NewGuid(),
                Name = $"TestMfg_{Guid.NewGuid():N}",
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

            PrinterGroup group = new()
            {
                Id = Guid.NewGuid(),
                Name = "Test Group",
                CreatedDate = DateTimeOffset.UtcNow,
                UpdatedDate = DateTimeOffset.UtcNow
            };
            db.PrinterGroups.Add(group);
            groupId = group.Id;

            Printer printer = new()
            {
                Id = Guid.NewGuid(),
                Name = "Test Printer",
                ServerUrl = $"http://192.168.1.{new Random().Next(1, 254)}",
                Backend = (int)PrinterBackend.Moonraker,
                ModelId = model.Id,
                ManufacturerId = manufacturer.Id,
                IsEnabled = true
            };
            db.Printers.Add(printer);
            printerId = printer.Id;

            await db.SaveChangesAsync();
        }

        // Act
        HttpResponseMessage response = await _authenticatedClient.PutAsync(
            $"/api/printer-groups/{groupId}/printers/{printerId}", null);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Verify assignment
        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Printer? updated = await db.Printers.FindAsync(printerId);
            updated!.PrinterGroupId.Should().Be(groupId);
        }
    }

    [Fact]
    [Trait("Category", "PrinterGroups")]
    public async Task AddPrinterToGroup_MovesFromPreviousGroup()
    {
        // Arrange: Create two groups and one printer
        Guid group1Id, group2Id, printerId;
        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            Manufacturer manufacturer = new()
            {
                Id = Guid.NewGuid(),
                Name = $"TestMfg_{Guid.NewGuid():N}",
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

            PrinterGroup group1 = new()
            {
                Id = Guid.NewGuid(),
                Name = "Group 1",
                CreatedDate = DateTimeOffset.UtcNow,
                UpdatedDate = DateTimeOffset.UtcNow
            };
            PrinterGroup group2 = new()
            {
                Id = Guid.NewGuid(),
                Name = "Group 2",
                CreatedDate = DateTimeOffset.UtcNow,
                UpdatedDate = DateTimeOffset.UtcNow
            };
            db.PrinterGroups.AddRange(group1, group2);
            group1Id = group1.Id;
            group2Id = group2.Id;

            Printer printer = new()
            {
                Id = Guid.NewGuid(),
                Name = "Test Printer",
                ServerUrl = $"http://192.168.1.{new Random().Next(1, 254)}",
                Backend = (int)PrinterBackend.Moonraker,
                ModelId = model.Id,
                ManufacturerId = manufacturer.Id,
                PrinterGroupId = group1Id,  // Initially in group1
                IsEnabled = true
            };
            db.Printers.Add(printer);
            printerId = printer.Id;

            await db.SaveChangesAsync();
        }

        // Act: Move printer to group2
        HttpResponseMessage response = await _authenticatedClient.PutAsync(
            $"/api/printer-groups/{group2Id}/printers/{printerId}", null);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Verify printer moved to group2
        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Printer? updated = await db.Printers.FindAsync(printerId);
            updated!.PrinterGroupId.Should().Be(group2Id, "printer should move to new group");
        }
    }

    [Fact]
    [Trait("Category", "PrinterGroups")]
    public async Task AddPrinterToGroup_WithNonexistentGroup_Returns404()
    {
        // Arrange: Create printer only
        Guid printerId;
        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            Manufacturer manufacturer = new()
            {
                Id = Guid.NewGuid(),
                Name = $"TestMfg_{Guid.NewGuid():N}",
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

            Printer printer = new()
            {
                Id = Guid.NewGuid(),
                Name = "Test Printer",
                ServerUrl = $"http://192.168.1.{new Random().Next(1, 254)}",
                Backend = (int)PrinterBackend.Moonraker,
                ModelId = model.Id,
                ManufacturerId = manufacturer.Id,
                IsEnabled = true
            };
            db.Printers.Add(printer);
            printerId = printer.Id;

            await db.SaveChangesAsync();
        }

        // Act
        Guid nonexistentGroupId = Guid.NewGuid();
        HttpResponseMessage response = await _authenticatedClient.PutAsync(
            $"/api/printer-groups/{nonexistentGroupId}/printers/{printerId}", null);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    [Trait("Category", "PrinterGroups")]
    public async Task AddPrinterToGroup_WithNonexistentPrinter_Returns404()
    {
        // Arrange: Create group only
        Guid groupId;
        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            PrinterGroup group = new()
            {
                Id = Guid.NewGuid(),
                Name = "Test Group",
                CreatedDate = DateTimeOffset.UtcNow,
                UpdatedDate = DateTimeOffset.UtcNow
            };
            db.PrinterGroups.Add(group);
            await db.SaveChangesAsync();
            groupId = group.Id;
        }

        // Act
        Guid nonexistentPrinterId = Guid.NewGuid();
        HttpResponseMessage response = await _authenticatedClient.PutAsync(
            $"/api/printer-groups/{groupId}/printers/{nonexistentPrinterId}", null);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // =========================================================================
    // DELETE /api/printer-groups/{id}/printers/{printerId} — Remove printer
    // =========================================================================

    [Fact]
    [Trait("Category", "PrinterGroups")]
    public async Task RemovePrinterFromGroup_WithValidIds_Returns204AndClearsPrinterGroupId()
    {
        // Arrange: Create group and printer in group
        Guid groupId, printerId;
        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            Manufacturer manufacturer = new()
            {
                Id = Guid.NewGuid(),
                Name = $"TestMfg_{Guid.NewGuid():N}",
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

            PrinterGroup group = new()
            {
                Id = Guid.NewGuid(),
                Name = "Test Group",
                CreatedDate = DateTimeOffset.UtcNow,
                UpdatedDate = DateTimeOffset.UtcNow
            };
            db.PrinterGroups.Add(group);
            groupId = group.Id;

            Printer printer = new()
            {
                Id = Guid.NewGuid(),
                Name = "Test Printer",
                ServerUrl = $"http://192.168.1.{new Random().Next(1, 254)}",
                Backend = (int)PrinterBackend.Moonraker,
                ModelId = model.Id,
                ManufacturerId = manufacturer.Id,
                PrinterGroupId = groupId,
                IsEnabled = true
            };
            db.Printers.Add(printer);
            printerId = printer.Id;

            await db.SaveChangesAsync();
        }

        // Act
        HttpResponseMessage response = await _authenticatedClient.DeleteAsync(
            $"/api/printer-groups/{groupId}/printers/{printerId}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Verify PrinterGroupId is null
        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Printer? updated = await db.Printers.FindAsync(printerId);
            updated!.PrinterGroupId.Should().BeNull("printer should be removed from group");
        }
    }

    [Fact]
    [Trait("Category", "PrinterGroups")]
    public async Task RemovePrinterFromGroup_WithNonexistentGroup_Returns404()
    {
        Guid nonexistentGroupId = Guid.NewGuid();
        Guid printerId = Guid.NewGuid();

        HttpResponseMessage response = await _authenticatedClient.DeleteAsync(
            $"/api/printer-groups/{nonexistentGroupId}/printers/{printerId}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    [Trait("Category", "PrinterGroups")]
    public async Task RemovePrinterFromGroup_WithNonexistentPrinter_Returns404()
    {
        // Arrange: Create group only
        Guid groupId;
        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            PrinterGroup group = new()
            {
                Id = Guid.NewGuid(),
                Name = "Test Group",
                CreatedDate = DateTimeOffset.UtcNow,
                UpdatedDate = DateTimeOffset.UtcNow
            };
            db.PrinterGroups.Add(group);
            await db.SaveChangesAsync();
            groupId = group.Id;
        }

        // Act
        Guid nonexistentPrinterId = Guid.NewGuid();
        HttpResponseMessage response = await _authenticatedClient.DeleteAsync(
            $"/api/printer-groups/{groupId}/printers/{nonexistentPrinterId}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
