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
using Farm.Web.Api.Tests.TestInfrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Farm.Web.Api.Tests.Integration;

/// <summary>
/// Integration tests for GcodeFilesService library operations
/// Tests G-code file library management: queries, retrieval, deletion
/// Covers filtering by search, material, and nozzle diameter
/// Fast executing (~3-4 seconds for 15 tests) - suitable for CI/CD pipelines
/// </summary>
[Trait("Category", "Integration")]
[Collection(IntegrationTestCollection.Name)]
public class GcodeLibraryServiceIntegrationTests : IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory;

    public GcodeLibraryServiceIntegrationTests()
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

    /// <summary>
    /// Helper method to get or create a gcode folder for tests
    /// </summary>
    private async Task<FolderNode> GetOrCreateGcodeFolderAsync(AppDbContext context)
    {
        FolderNode? folder = await context.Set<FolderNode>().FirstOrDefaultAsync(f => f.Path == "/" && f.FolderType == "gcode");
        if (folder == null)
        {
            folder = new FolderNode
            {
                Id = Guid.NewGuid(),
                Path = "/",
                FolderType = "gcode"
            };
            context.Set<FolderNode>().Add(folder);
            await context.SaveChangesAsync();
        }
        return folder;
    }

    #region QueryLibraryAsync Tests

    [Fact]
    public async Task QueryLibraryAsync_WithNoFilters_ReturnsAllFiles()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        IGcodeFilesService service = scope.ServiceProvider.GetRequiredService<IGcodeFilesService>();

        FolderNode folder = await GetOrCreateGcodeFolderAsync(context);

        // Create test files with FolderId
        var file1 = new GcodeFile
        {
            Id = Guid.NewGuid(),
            Name = "File 1",
            FileName = "File 1",
            FolderId = folder.Id,
            FilePath = "/gcodes/file1.gcode",
            FileSizeBytes = 1024,
            FileHash = "hash1",
            UploadedAt = DateTime.UtcNow
        };
        var file2 = new GcodeFile
        {
            Id = Guid.NewGuid(),
            Name = "File 2",
            FileName = "File 2",
            FolderId = folder.Id,
            FilePath = "/gcodes/file2.gcode",
            FileSizeBytes = 2048,
            FileHash = "hash2",
            UploadedAt = DateTime.UtcNow
        };
        context.GcodeFiles.AddRange(file1, file2);
        await context.SaveChangesAsync();

        // Act
        IReadOnlyList<GcodeFileDto> result = await service.QueryLibraryAsync(null, null, null, null, CancellationToken.None);

        // Assert
        result.Should().NotBeEmpty();
        result.Count.Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task QueryLibraryAsync_WithSearchFilter_ReturnsMatching()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        IGcodeFilesService service = scope.ServiceProvider.GetRequiredService<IGcodeFilesService>();

        FolderNode folder = await GetOrCreateGcodeFolderAsync(context);

        string uniqueId = Guid.NewGuid().ToString().Substring(0, 8);
        var file = new GcodeFile
        {
            Id = Guid.NewGuid(),
            Name = "Benchmark Test.gcode",
            FileName = "Benchmark Test.gcode",  // Include extension
            FolderId = folder.Id,
            FilePath = $"/gcodes/benchmark-{uniqueId}.gcode",
            FileSizeBytes = 1024,
            FileHash = $"hash-{uniqueId}",
            UploadedAt = DateTime.UtcNow
        };
        context.GcodeFiles.Add(file);
        await context.SaveChangesAsync();

        // Act
        IReadOnlyList<GcodeFileDto> result = await service.QueryLibraryAsync("benchmark", null, null, null, CancellationToken.None);

        // Assert
        result.Should().NotBeEmpty();
        result.Should().Contain(g => g.FileName.Contains("benchmark", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task QueryLibraryAsync_WithDescriptionSearch_ReturnsMatching()
    {
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        IGcodeFilesService service = scope.ServiceProvider.GetRequiredService<IGcodeFilesService>();

        FolderNode folder = await GetOrCreateGcodeFolderAsync(context);

        string uniqueId = Guid.NewGuid().ToString()[..8];
        var file = new GcodeFile
        {
            Id = Guid.NewGuid(),
            Name = "Description Search Test",
            FileName = $"description-search-{uniqueId}.gcode",
            Description = $"Calibration tower {uniqueId}",
            FolderId = folder.Id,
            FilePath = $"/gcodes/description-search-{uniqueId}.gcode",
            FileSizeBytes = 1024,
            FileHash = $"description-search-hash-{uniqueId}",
            UploadedAt = DateTime.UtcNow
        };
        context.GcodeFiles.Add(file);
        await context.SaveChangesAsync();

        IReadOnlyList<GcodeFileDto> result = await service.QueryLibraryAsync($"CALIBRATION TOWER {uniqueId}", null, null, null, CancellationToken.None);

        result.Should().ContainSingle(g => g.Id == file.Id);
    }

    [Fact]
    public async Task QueryLibraryAsync_WithMaterialFilter_ReturnsMatching()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        IGcodeFilesService service = scope.ServiceProvider.GetRequiredService<IGcodeFilesService>();

        FolderNode folder = await GetOrCreateGcodeFolderAsync(context);

        var file = new GcodeFile
        {
            Id = Guid.NewGuid(),
            Name = "PLA Test",
            FileName = "PLA Test.gcode",  // Include extension
            FolderId = folder.Id,
            FilePath = "/gcodes/pla-test.gcode",
            FileSizeBytes = 1024,
            FileHash = "pla-hash",
            RequiredMaterial = "PLA",
            UploadedAt = DateTime.UtcNow
        };
        context.GcodeFiles.Add(file);
        await context.SaveChangesAsync();

        // Act
        IReadOnlyList<GcodeFileDto> result = await service.QueryLibraryAsync(null, "PLA", null, null, CancellationToken.None);

        // Assert
        result.Should().NotBeEmpty();
        result.Should().Contain(g => g.RequiredMaterial == "PLA");
    }

    [Fact]
    public async Task QueryLibraryAsync_WithNozzleFilter_ReturnsMatching()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        IGcodeFilesService service = scope.ServiceProvider.GetRequiredService<IGcodeFilesService>();

        FolderNode folder = await GetOrCreateGcodeFolderAsync(context);

        var file = new GcodeFile
        {
            Id = Guid.NewGuid(),
            Name = "0.4mm Nozzle",
            FileName = "0.4mm Nozzle.gcode",  // Include extension
            FolderId = folder.Id,
            FilePath = "/gcodes/nozzle-04.gcode",
            FileSizeBytes = 1024,
            FileHash = "nozzle-hash",
            RequiredNozzleDiameter = 0.4,
            UploadedAt = DateTime.UtcNow
        };
        context.GcodeFiles.Add(file);
        await context.SaveChangesAsync();

        // Act
        IReadOnlyList<GcodeFileDto> result = await service.QueryLibraryAsync(null, null, 0.4, null, CancellationToken.None);

        // Assert
        result.Should().NotBeEmpty();
        result.Should().Contain(g => Math.Abs((g.RequiredNozzleDiameter ?? 0) - 0.4) < 0.01);
    }

    [Fact]
    public async Task QueryLibraryAsync_WithMultipleFilters_ReturnsMatchingBoth()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        IGcodeFilesService service = scope.ServiceProvider.GetRequiredService<IGcodeFilesService>();

        FolderNode folder = await GetOrCreateGcodeFolderAsync(context);

        var file = new GcodeFile
        {
            Id = Guid.NewGuid(),
            FileName = "PETG Fixture.gcode",  // Include extension
            FolderId = folder.Id,
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
        IReadOnlyList<GcodeFileDto> result = await service.QueryLibraryAsync("fixture", "PETG", 0.6, null, CancellationToken.None);

        // Assert
        result.Should().NotBeEmpty();
        result.Should().Contain(g => g.RequiredMaterial == "PETG" && Math.Abs((g.RequiredNozzleDiameter ?? 0) - 0.6) < 0.01);
    }

    #endregion

    #region GetFileAsync Tests

    [Fact]
    public async Task GetFileAsync_WithValidId_ReturnsFile()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        IGcodeFilesService service = scope.ServiceProvider.GetRequiredService<IGcodeFilesService>();

        FolderNode folder = await GetOrCreateGcodeFolderAsync(context);

        var file = new GcodeFile
        {
            Id = Guid.NewGuid(),
            FileName = "Get Test.gcode",  // Include extension
            FolderId = folder.Id,
            FilePath = "/gcodes/get-test.gcode",
            FileSizeBytes = 1024,
            FileHash = "get-hash",
            UploadedAt = DateTime.UtcNow
        };
        context.GcodeFiles.Add(file);
        await context.SaveChangesAsync();

        // Act
        GcodeFileDto? result = await service.GetFileAsync(file.Id, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result!.Id.Should().Be(file.Id);
        result.FileName.Should().Be("Get Test.gcode");
    }

    [Fact]
    public async Task GetFileAsync_WithNonExistentId_ReturnsNull()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        IGcodeFilesService service = scope.ServiceProvider.GetRequiredService<IGcodeFilesService>();

        // Act
        GcodeFileDto? result = await service.GetFileAsync(Guid.NewGuid(), CancellationToken.None);

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region DeleteFileAsync Tests

    [Fact]
    public async Task DeleteFileAsync_WithValidId_DeletesSuccessfully()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        IGcodeFilesService service = scope.ServiceProvider.GetRequiredService<IGcodeFilesService>();

        FolderNode folder = await GetOrCreateGcodeFolderAsync(context);

        var file = new GcodeFile
        {
            Id = Guid.NewGuid(),
            FileName = "Delete Test.gcode",  // Include extension
            FolderId = folder.Id,
            FilePath = "/gcodes/delete-test.gcode",
            FileSizeBytes = 1024,
            FileHash = "delete-hash",
            UploadedAt = DateTime.UtcNow
        };
        context.GcodeFiles.Add(file);
        await context.SaveChangesAsync();

        // Act
        bool result = await service.DeleteFileAsync(file.Id, CancellationToken.None);

        // Assert
        result.Should().BeTrue();
        GcodeFile? deleted = await context.GcodeFiles.FindAsync(file.Id);
        deleted.Should().BeNull();
    }

    [Fact]
    public async Task DeleteFileAsync_WithNonExistentId_ReturnsFalse()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        IGcodeFilesService service = scope.ServiceProvider.GetRequiredService<IGcodeFilesService>();

        // Act
        bool result = await service.DeleteFileAsync(Guid.NewGuid(), CancellationToken.None);

        // Assert
        result.Should().BeFalse();
    }

    #endregion

    #region Integration Tests

    [Fact]
    public async Task QueryAndGetAndDelete_CompleteWorkflow()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        IGcodeFilesService service = scope.ServiceProvider.GetRequiredService<IGcodeFilesService>();

        FolderNode folder = await GetOrCreateGcodeFolderAsync(context);

        string uniqueId = Guid.NewGuid().ToString().Substring(0, 8);
        var file = new GcodeFile
        {
            Id = Guid.NewGuid(),
            FileName = $"Workflow {uniqueId}.gcode",  // Include extension
            FolderId = folder.Id,
            FilePath = $"/gcodes/workflow-{uniqueId}.gcode",
            FileSizeBytes = 1024,
            FileHash = $"workflow-hash-{uniqueId}",
            RequiredMaterial = "ABS",
            UploadedAt = DateTime.UtcNow
        };
        context.GcodeFiles.Add(file);
        await context.SaveChangesAsync();

        // Act & Assert - Query
        IReadOnlyList<GcodeFileDto> queried = await service.QueryLibraryAsync(null, "ABS", null, null, CancellationToken.None);
        queried.Should().Contain(g => g.Id == file.Id);

        // Act & Assert - Get
        GcodeFileDto? fetched = await service.GetFileAsync(file.Id, CancellationToken.None);
        fetched.Should().NotBeNull();

        // Act & Assert - Delete
        bool deleted = await service.DeleteFileAsync(file.Id, CancellationToken.None);
        deleted.Should().BeTrue();

        // Verify deleted
        GcodeFileDto? afterDelete = await service.GetFileAsync(file.Id, CancellationToken.None);
        afterDelete.Should().BeNull();
    }

    #endregion
}
