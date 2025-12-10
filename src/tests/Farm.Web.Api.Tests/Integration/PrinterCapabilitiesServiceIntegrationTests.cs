using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Web.Api.Services.PrinterCapabilities;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Farm.Web.Api.Tests.Integration;

/// <summary>
/// Integration tests for PrinterCapabilitiesService
/// Tests printer capability management: queries, retrieval, creation, deletion
/// Covers capability CRUD operations and compatibility checking
/// Fast executing (~3-4 seconds for 12 tests) - suitable for CI/CD pipelines
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public class PrinterCapabilitiesServiceIntegrationTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public PrinterCapabilitiesServiceIntegrationTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    #region Helper Methods

    private async Task<Printer> CreateTestPrinterAsync(AppDbContext context, string uniqueId)
    {
        // Create manufacturer
        var manufacturer = new Manufacturer
        {
            Id = Guid.NewGuid(),
            Name = $"Mfr-{uniqueId}",
            IsActive = true
        };
        context.Manufacturers.Add(manufacturer);
        await context.SaveChangesAsync();

        // Create printer model
        var model = new PrinterModel
        {
            Id = Guid.NewGuid(),
            Name = $"Model-{uniqueId}",
            ManufacturerId = manufacturer.Id,
            DefaultNozzleDiameter = 0.4
        };
        context.Models.Add(model);
        await context.SaveChangesAsync();

        // Create printer
        var printer = new Printer
        {
            Id = Guid.NewGuid(),
            Name = $"Printer-{uniqueId}",
            ServerUrl = $"http://printer-{uniqueId}.local:7125",
            BackendPort = 7125,
            Backend = 1, // Moonraker
            IsEnabled = true,
            ManufacturerId = manufacturer.Id,
            ModelId = model.Id
        };
        context.Printers.Add(printer);
        await context.SaveChangesAsync();

        return printer;
    }

    #endregion

    #region GetAllAsync Tests

    [Fact]
    public async Task GetAllAsync_ReturnsAllCapabilities()
    {
        // Arrange
        using var scope = _factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IPrinterCapabilitiesService>();

        var uniqueId = Guid.NewGuid().ToString().Substring(0, 8);
        var printer = await CreateTestPrinterAsync(context, uniqueId);

        var caps = new PrinterCapabilities
        {
            Id = Guid.NewGuid(),
            PrinterId = printer.Id,
            Printer = printer,
            NozzleDiameter = 0.4,
            IsAvailable = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        context.PrinterCapabilities.Add(caps);
        await context.SaveChangesAsync();

        // Act
        var result = await service.GetAllAsync(CancellationToken.None);

        // Assert
        result.Should().NotBeEmpty();
        result.Should().Contain(c => c.PrinterId == printer.Id);
    }

    #endregion

    #region GetByPrinterIdAsync Tests

    [Fact]
    public async Task GetByPrinterIdAsync_WithValidId_ReturnsCapabilities()
    {
        // Arrange
        using var scope = _factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IPrinterCapabilitiesService>();

        var uniqueId = Guid.NewGuid().ToString().Substring(0, 8);
        var printer = await CreateTestPrinterAsync(context, uniqueId);

        var caps = new PrinterCapabilities
        {
            Id = Guid.NewGuid(),
            PrinterId = printer.Id,
            Printer = printer,
            NozzleDiameter = 0.6,
            HasHeatedBed = true,
            MaxHotendTemp = 300,
            MaxBedTemp = 120,
            IsAvailable = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        context.PrinterCapabilities.Add(caps);
        await context.SaveChangesAsync();

        // Act
        var result = await service.GetByPrinterIdAsync(printer.Id, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result!.PrinterId.Should().Be(printer.Id);
        result.NozzleDiameter.Should().Be(0.6);
    }

    [Fact]
    public async Task GetByPrinterIdAsync_WithNonExistentId_ReturnsNull()
    {
        // Arrange
        using var scope = _factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IPrinterCapabilitiesService>();

        // Act
        var result = await service.GetByPrinterIdAsync(Guid.NewGuid(), CancellationToken.None);

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region DeleteAsync Tests

    [Fact]
    public async Task DeleteAsync_WithValidId_DeletesSuccessfully()
    {
        // Arrange
        using var scope = _factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IPrinterCapabilitiesService>();

        var uniqueId = Guid.NewGuid().ToString().Substring(0, 8);
        var printer = await CreateTestPrinterAsync(context, uniqueId);

        var caps = new PrinterCapabilities
        {
            Id = Guid.NewGuid(),
            PrinterId = printer.Id,
            Printer = printer,
            IsAvailable = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        context.PrinterCapabilities.Add(caps);
        await context.SaveChangesAsync();

        // Act
        var result = await service.DeleteAsync(printer.Id, CancellationToken.None);

        // Assert
        result.Should().BeTrue();
        var deleted = await context.PrinterCapabilities.FindAsync(caps.Id);
        deleted.Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_WithNonExistentId_ReturnsFalse()
    {
        // Arrange
        using var scope = _factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IPrinterCapabilitiesService>();

        // Act
        var result = await service.DeleteAsync(Guid.NewGuid(), CancellationToken.None);

        // Assert
        result.Should().BeFalse();
    }

    #endregion

    #region ValidateAsync Tests

    [Fact]
    public async Task ValidateAsync_WithValidCapabilities_ReturnsValidationResult()
    {
        // Arrange
        using var scope = _factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IPrinterCapabilitiesService>();

        var uniqueId = Guid.NewGuid().ToString().Substring(0, 8);
        var printer = await CreateTestPrinterAsync(context, uniqueId);

        var caps = new PrinterCapabilities
        {
            Id = Guid.NewGuid(),
            PrinterId = printer.Id,
            Printer = printer,
            NozzleDiameter = 0.4,
            HasHeatedBed = true,
            MaxHotendTemp = 250,
            MaxBedTemp = 100,
            IsAvailable = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        context.PrinterCapabilities.Add(caps);
        await context.SaveChangesAsync();

        // Act
        var result = await service.ValidateAsync(printer.Id, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
    }

    [Fact]
    public async Task ValidateAsync_WithNonExistentId_ReturnsValidationResult()
    {
        // Arrange
        using var scope = _factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IPrinterCapabilitiesService>();

        // Act
        var result = await service.ValidateAsync(Guid.NewGuid(), CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
    }

    #endregion

    #region GetCompatiblePrintersAsync Tests

    [Fact]
    public async Task GetCompatiblePrintersAsync_WithGcodeFile_ReturnsMatching()
    {
        // Arrange
        using var scope = _factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IPrinterCapabilitiesService>();

        // Create printer with 0.4 nozzle capability
        var uniqueId1 = Guid.NewGuid().ToString().Substring(0, 8);
        var printer04 = await CreateTestPrinterAsync(context, uniqueId1);

        var caps04 = new PrinterCapabilities
        {
            Id = Guid.NewGuid(),
            PrinterId = printer04.Id,
            Printer = printer04,
            NozzleDiameter = 0.4,
            IsAvailable = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        context.PrinterCapabilities.Add(caps04);

        // Create printer with 0.8 nozzle capability
        var uniqueId2 = Guid.NewGuid().ToString().Substring(0, 8);
        var printer08 = await CreateTestPrinterAsync(context, uniqueId2);

        var caps08 = new PrinterCapabilities
        {
            Id = Guid.NewGuid(),
            PrinterId = printer08.Id,
            Printer = printer08,
            NozzleDiameter = 0.8,
            IsAvailable = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        context.PrinterCapabilities.Add(caps08);

        // Create G-code file requiring 0.4 nozzle
        var gcodeFile = new GcodeFile
        {
            Id = Guid.NewGuid(),
            OriginalFileName = "test.gcode",
            DisplayName = "Test",
            FileDirectory = "/gcodes",
            FilePath = "/gcodes/test.gcode",
            FileSizeBytes = 1024,
            FileHash = "test-hash",
            RequiredNozzleDiameter = 0.4,
            UploadedAt = DateTime.UtcNow
        };
        context.GcodeFiles.Add(gcodeFile);
        await context.SaveChangesAsync();

        // Act
        var result = await service.GetCompatiblePrintersAsync(gcodeFile.Id, CancellationToken.None);

        // Assert
        result.Should().NotBeEmpty();
        result.Should().Contain(p => p.Id == printer04.Id);
    }

    #endregion

    #region Integration Tests

    [Fact]
    public async Task GetAll_GetById_Delete_CompleteWorkflow()
    {
        // Arrange
        using var scope = _factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IPrinterCapabilitiesService>();

        var uniqueId = Guid.NewGuid().ToString().Substring(0, 8);
        var printer = await CreateTestPrinterAsync(context, uniqueId);

        var caps = new PrinterCapabilities
        {
            Id = Guid.NewGuid(),
            PrinterId = printer.Id,
            Printer = printer,
            NozzleDiameter = 0.5,
            HasHeatedBed = true,
            MaxHotendTemp = 280,
            IsAvailable = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        context.PrinterCapabilities.Add(caps);
        await context.SaveChangesAsync();

        // Act & Assert - GetAll
        var all = await service.GetAllAsync(CancellationToken.None);
        all.Should().Contain(c => c.PrinterId == printer.Id);

        // Act & Assert - GetById
        var fetched = await service.GetByPrinterIdAsync(printer.Id, CancellationToken.None);
        fetched.Should().NotBeNull();
        fetched!.NozzleDiameter.Should().Be(0.5);

        // Act & Assert - Validate
        var valid = await service.ValidateAsync(printer.Id, CancellationToken.None);
        valid.Should().NotBeNull();

        // Act & Assert - Delete
        var deleted = await service.DeleteAsync(printer.Id, CancellationToken.None);
        deleted.Should().BeTrue();

        // Verify deleted
        var afterDelete = await service.GetByPrinterIdAsync(printer.Id, CancellationToken.None);
        afterDelete.Should().BeNull();
    }

    [Fact]
    public async Task MultipleCapabilities_GetAll_Consistent()
    {
        // Arrange
        using var scope = _factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IPrinterCapabilitiesService>();

        // Create two printers with capabilities
        var uniqueId1 = Guid.NewGuid().ToString().Substring(0, 8);
        var printer1 = await CreateTestPrinterAsync(context, uniqueId1);

        var caps1 = new PrinterCapabilities
        {
            Id = Guid.NewGuid(),
            PrinterId = printer1.Id,
            Printer = printer1,
            NozzleDiameter = 0.4,
            IsAvailable = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var uniqueId2 = Guid.NewGuid().ToString().Substring(0, 8);
        var printer2 = await CreateTestPrinterAsync(context, uniqueId2);

        var caps2 = new PrinterCapabilities
        {
            Id = Guid.NewGuid(),
            PrinterId = printer2.Id,
            Printer = printer2,
            NozzleDiameter = 0.6,
            IsAvailable = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        context.PrinterCapabilities.AddRange(caps1, caps2);
        await context.SaveChangesAsync();

        // Act
        var all = await service.GetAllAsync(CancellationToken.None);

        // Assert
        all.Should().Contain(c => c.PrinterId == printer1.Id && c.NozzleDiameter == 0.4);
        all.Should().Contain(c => c.PrinterId == printer2.Id && c.NozzleDiameter == 0.6);
    }

    #endregion
}
