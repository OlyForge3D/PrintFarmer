using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Web.Api.Services.Gcode;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Farm.Web.Api.Tests.Integration;

/// <summary>
/// Integration tests for GcodeLibraryService
/// Tests G-code file library management: queries, retrieval, deletion
/// Covers filtering by search, material, and nozzle diameter
/// Fast executing (~3-4 seconds for 15 tests) - suitable for CI/CD pipelines
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public class GcodeLibraryServiceIntegrationTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public GcodeLibraryServiceIntegrationTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    #region QueryLibraryAsync Tests

    [Fact]
    public async Task QueryLibraryAsync_WithNoFilters_ReturnsAllFiles()
    {
        // Arrange
        using var scope = _factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IGcodeLibraryService>();

        // Create test files
        var file1 = new GcodeFile
        {
            Id = Guid.NewGuid(),
            OriginalFileName = "file1.gcode",
            DisplayName = "File 1",
            FileDirectory = "/gcodes",
            FilePath = "/gcodes/file1.gcode",
            FileSizeBytes = 1024,
            FileHash = "hash1",
            UploadedAt = DateTime.UtcNow
        };
        var file2 = new GcodeFile
        {
            Id = Guid.NewGuid(),
            OriginalFileName = "file2.gcode",
            DisplayName = "File 2",
            FileDirectory = "/gcodes",
            FilePath = "/gcodes/file2.gcode",
            FileSizeBytes = 2048,
            FileHash = "hash2",
            UploadedAt = DateTime.UtcNow
        };
        context.GcodeFiles.AddRange(file1, file2);
        await context.SaveChangesAsync();

        // Act
        var result = await service.QueryLibraryAsync(null, null, null, null, CancellationToken.None);

        // Assert
        result.Should().NotBeEmpty();
        result.Count.Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task QueryLibraryAsync_WithSearchFilter_ReturnsMatching()
    {
        // Arrange
        using var scope = _factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IGcodeLibraryService>();

        var uniqueId = Guid.NewGuid().ToString().Substring(0, 8);
        var file = new GcodeFile
        {
            Id = Guid.NewGuid(),
            OriginalFileName = $"benchmark-{uniqueId}.gcode",
            DisplayName = "Benchmark Test",
            FileDirectory = "/gcodes",
            FilePath = $"/gcodes/benchmark-{uniqueId}.gcode",
            FileSizeBytes = 1024,
            FileHash = $"hash-{uniqueId}",
            UploadedAt = DateTime.UtcNow
        };
        context.GcodeFiles.Add(file);
        await context.SaveChangesAsync();

        // Act
        var result = await service.QueryLibraryAsync("benchmark", null, null, null, CancellationToken.None);

        // Assert
        result.Should().NotBeEmpty();
        result.Should().Contain(g => g.OriginalFileName.Contains("benchmark"));
    }

    [Fact]
    public async Task QueryLibraryAsync_WithMaterialFilter_ReturnsMatching()
    {
        // Arrange
        using var scope = _factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IGcodeLibraryService>();

        var file = new GcodeFile
        {
            Id = Guid.NewGuid(),
            OriginalFileName = "pla-test.gcode",
            DisplayName = "PLA Test",
            FileDirectory = "/gcodes",
            FilePath = "/gcodes/pla-test.gcode",
            FileSizeBytes = 1024,
            FileHash = "pla-hash",
            RequiredMaterial = "PLA",
            UploadedAt = DateTime.UtcNow
        };
        context.GcodeFiles.Add(file);
        await context.SaveChangesAsync();

        // Act
        var result = await service.QueryLibraryAsync(null, "PLA", null, null, CancellationToken.None);

        // Assert
        result.Should().NotBeEmpty();
        result.Should().Contain(g => g.RequiredMaterial == "PLA");
    }

    [Fact]
    public async Task QueryLibraryAsync_WithNozzleFilter_ReturnsMatching()
    {
        // Arrange
        using var scope = _factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IGcodeLibraryService>();

        var file = new GcodeFile
        {
            Id = Guid.NewGuid(),
            OriginalFileName = "nozzle-04.gcode",
            DisplayName = "0.4mm Nozzle",
            FileDirectory = "/gcodes",
            FilePath = "/gcodes/nozzle-04.gcode",
            FileSizeBytes = 1024,
            FileHash = "nozzle-hash",
            RequiredNozzleDiameter = 0.4,
            UploadedAt = DateTime.UtcNow
        };
        context.GcodeFiles.Add(file);
        await context.SaveChangesAsync();

        // Act
        var result = await service.QueryLibraryAsync(null, null, 0.4, null, CancellationToken.None);

        // Assert
        result.Should().NotBeEmpty();
        result.Should().Contain(g => Math.Abs(g.RequiredNozzleDiameter.Value - 0.4) < 0.01);
    }

    [Fact]
    public async Task QueryLibraryAsync_WithMultipleFilters_ReturnsMatchingBoth()
    {
        // Arrange
        using var scope = _factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IGcodeLibraryService>();

        var file = new GcodeFile
        {
            Id = Guid.NewGuid(),
            OriginalFileName = "petg-fixture.gcode",
            DisplayName = "PETG Fixture",
            FileDirectory = "/gcodes",
            FilePath = "/gcodes/petg-fixture.gcode",
            FileSizeBytes = 1024,
            FileHash = "petg-hash",
            RequiredMaterial = "PETG",
            RequiredNozzleDiameter = 0.6,
            UploadedAt = DateTime.UtcNow
        };
        context.GcodeFiles.Add(file);
        await context.SaveChangesAsync();

        // Act
        var result = await service.QueryLibraryAsync("fixture", "PETG", 0.6, null, CancellationToken.None);

        // Assert
        result.Should().NotBeEmpty();
        result.Should().Contain(g => g.RequiredMaterial == "PETG" && Math.Abs(g.RequiredNozzleDiameter.Value - 0.6) < 0.01);
    }

    #endregion

    #region GetFileAsync Tests

    [Fact]
    public async Task GetFileAsync_WithValidId_ReturnsFile()
    {
        // Arrange
        using var scope = _factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IGcodeLibraryService>();

        var file = new GcodeFile
        {
            Id = Guid.NewGuid(),
            OriginalFileName = "get-test.gcode",
            DisplayName = "Get Test",
            FileDirectory = "/gcodes",
            FilePath = "/gcodes/get-test.gcode",
            FileSizeBytes = 1024,
            FileHash = "get-hash",
            UploadedAt = DateTime.UtcNow
        };
        context.GcodeFiles.Add(file);
        await context.SaveChangesAsync();

        // Act
        var result = await service.GetFileAsync(file.Id, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result!.Id.Should().Be(file.Id);
        result.DisplayName.Should().Be("Get Test");
    }

    [Fact]
    public async Task GetFileAsync_WithNonExistentId_ReturnsNull()
    {
        // Arrange
        using var scope = _factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IGcodeLibraryService>();

        // Act
        var result = await service.GetFileAsync(Guid.NewGuid(), CancellationToken.None);

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region DeleteFileAsync Tests

    [Fact]
    public async Task DeleteFileAsync_WithValidId_DeletesSuccessfully()
    {
        // Arrange
        using var scope = _factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IGcodeLibraryService>();

        var file = new GcodeFile
        {
            Id = Guid.NewGuid(),
            OriginalFileName = "delete-test.gcode",
            DisplayName = "Delete Test",
            FileDirectory = "/gcodes",
            FilePath = "/gcodes/delete-test.gcode",
            FileSizeBytes = 1024,
            FileHash = "delete-hash",
            UploadedAt = DateTime.UtcNow
        };
        context.GcodeFiles.Add(file);
        await context.SaveChangesAsync();

        // Act
        var result = await service.DeleteFileAsync(file.Id, CancellationToken.None);

        // Assert
        result.Should().BeTrue();
        var deleted = await context.GcodeFiles.FindAsync(file.Id);
        deleted.Should().BeNull();
    }

    [Fact]
    public async Task DeleteFileAsync_WithNonExistentId_ReturnsFalse()
    {
        // Arrange
        using var scope = _factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IGcodeLibraryService>();

        // Act
        var result = await service.DeleteFileAsync(Guid.NewGuid(), CancellationToken.None);

        // Assert
        result.Should().BeFalse();
    }

    #endregion

    #region Integration Tests

    [Fact]
    public async Task QueryAndGetAndDelete_CompleteWorkflow()
    {
        // Arrange
        using var scope = _factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IGcodeLibraryService>();

        var uniqueId = Guid.NewGuid().ToString().Substring(0, 8);
        var file = new GcodeFile
        {
            Id = Guid.NewGuid(),
            OriginalFileName = $"workflow-{uniqueId}.gcode",
            DisplayName = $"Workflow {uniqueId}",
            FileDirectory = "/gcodes",
            FilePath = $"/gcodes/workflow-{uniqueId}.gcode",
            FileSizeBytes = 1024,
            FileHash = $"workflow-hash-{uniqueId}",
            RequiredMaterial = "ABS",
            UploadedAt = DateTime.UtcNow
        };
        context.GcodeFiles.Add(file);
        await context.SaveChangesAsync();

        // Act & Assert - Query
        var queried = await service.QueryLibraryAsync(null, "ABS", null, null, CancellationToken.None);
        queried.Should().Contain(g => g.Id == file.Id);

        // Act & Assert - Get
        var fetched = await service.GetFileAsync(file.Id, CancellationToken.None);
        fetched.Should().NotBeNull();

        // Act & Assert - Delete
        var deleted = await service.DeleteFileAsync(file.Id, CancellationToken.None);
        deleted.Should().BeTrue();

        // Verify deleted
        var afterDelete = await service.GetFileAsync(file.Id, CancellationToken.None);
        afterDelete.Should().BeNull();
    }

    #endregion
}
