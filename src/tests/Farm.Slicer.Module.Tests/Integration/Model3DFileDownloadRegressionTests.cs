using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Farm.Slicer.Module.Data;
using Farm.Slicer.Module.Domain;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Farm.Slicer.Module.Tests.Integration;

/// <summary>
/// Regression tests for GET /api/3d-models/file/{id} endpoint.
/// Validates the fix for file downloads returning 404 for invalid models.
/// </summary>
/// <remarks>
/// Context: The endpoint was incorrectly returning 404 for models with IsValid=false.
/// Root cause: GetModelFilePathAsync used GetByIdAsync (filters by IsValid=true).
/// Fix: Changed to GetByIdUnfilteredAsync for file operations.
/// Pattern: Use filtered query for list/metadata, unfiltered for physical file access.
/// </remarks>
[Trait("Category", "Integration")]
[Trait("Category", "Regression")]
[Collection(IntegrationTestCollection.Name)]
public class Model3DFileDownloadRegressionTests : IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory;
    private HttpClient? _client;

    public Model3DFileDownloadRegressionTests()
    {
        _factory = new CustomWebApplicationFactory();
    }

    public async Task InitializeAsync()
    {
        await _factory.ResetDatabaseAsync();
        _client = _factory.CreateClient();
    }

    public Task DisposeAsync()
    {
        _client?.Dispose();
        _factory?.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task GetModelFile_WithValidModel_Returns200AndFileContent()
    {
        // Arrange - Create valid model with physical file
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        SlicerDbContext dbContext = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();
        IConfiguration config = scope.ServiceProvider.GetRequiredService<IConfiguration>();

        var modelId = Guid.NewGuid();
        string fileName = $"{modelId}.stl";
        string modelsPath = config["Slicer:ModelsPath"] ?? "/app/uploads/models";
        Directory.CreateDirectory(modelsPath);
        string filePath = Path.Combine(modelsPath, fileName);

        string fileContent = "valid STL file content";
        await File.WriteAllTextAsync(filePath, fileContent);

        var model = new Model3D
        {
            Id = modelId,
            Name = "valid-model.stl",
            FileName = fileName,
            FilePath = modelsPath,
            FileSizeBytes = fileContent.Length,
            FileHash = $"hash-{modelId}",
            FileFormat = ModelFileFormat.STL,
            IsValid = true,
            FolderId = Guid.NewGuid(),
            UploadedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _ = await dbContext.Models3D.AddAsync(model);
        _ = await dbContext.SaveChangesAsync();

        try
        {
            // Act
            HttpResponseMessage response = await _client!.GetAsync($"/api/3d-models/file/{modelId}");

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.OK, "valid model file should be downloadable");
            response.Content.Headers.ContentType?.MediaType.Should().Be("application/octet-stream");

            string downloadedContent = await response.Content.ReadAsStringAsync();
            downloadedContent.Should().Be(fileContent, "downloaded content should match uploaded file");
        }
        finally
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
    }

    [Fact]
    public async Task GetModelFile_WithInvalidModel_Returns200AndFileContent()
    {
        // Arrange - Create INVALID model with physical file
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        SlicerDbContext dbContext = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();
        IConfiguration config = scope.ServiceProvider.GetRequiredService<IConfiguration>();

        var modelId = Guid.NewGuid();
        string fileName = $"{modelId}.stl";
        string modelsPath = config["Slicer:ModelsPath"] ?? "/app/uploads/models";
        Directory.CreateDirectory(modelsPath);
        string filePath = Path.Combine(modelsPath, fileName);

        string fileContent = "invalid STL file content (corrupted)";
        await File.WriteAllTextAsync(filePath, fileContent);

        var invalidModel = new Model3D
        {
            Id = modelId,
            Name = "invalid-model.stl",
            FileName = fileName,
            FilePath = modelsPath,
            FileSizeBytes = fileContent.Length,
            FileHash = $"hash-{modelId}",
            FileFormat = ModelFileFormat.STL,
            IsValid = false, // CRITICAL: Model marked as invalid/corrupted
            FolderId = Guid.NewGuid(),
            UploadedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _ = await dbContext.Models3D.AddAsync(invalidModel);
        _ = await dbContext.SaveChangesAsync();

        try
        {
            // Act - REGRESSION TEST: This should NOT return 404
            HttpResponseMessage response = await _client!.GetAsync($"/api/3d-models/file/{modelId}");

            // Assert - CRITICAL: File should be downloadable even when IsValid = false
            response.StatusCode.Should().Be(HttpStatusCode.OK,
                "REGRESSION: File endpoint must serve physical files regardless of IsValid status. " +
                "This prevents 404 errors when downloading files for models that failed validation.");

            response.Content.Headers.ContentType?.MediaType.Should().Be("application/octet-stream");

            string downloadedContent = await response.Content.ReadAsStringAsync();
            downloadedContent.Should().Be(fileContent,
                "downloaded content should match the physical file, regardless of validation status");
        }
        finally
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
    }

    [Fact]
    public async Task GetModelFile_WithNonExistentModel_Returns404()
    {
        // Arrange
        var nonExistentId = Guid.NewGuid();

        // Act
        HttpResponseMessage response = await _client!.GetAsync($"/api/3d-models/file/{nonExistentId}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "endpoint should return 404 when model does not exist in database");
    }

    [Fact]
    public async Task GetModelFile_WithValidModelButMissingPhysicalFile_Returns404()
    {
        // Arrange - Create model record without physical file
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        SlicerDbContext dbContext = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();
        IConfiguration config = scope.ServiceProvider.GetRequiredService<IConfiguration>();

        var modelId = Guid.NewGuid();
        string fileName = $"{modelId}.stl";
        string modelsPath = config["Slicer:ModelsPath"] ?? "/app/uploads/models";

        var model = new Model3D
        {
            Id = modelId,
            Name = "orphaned-model.stl",
            FileName = fileName,
            FilePath = modelsPath,
            FileSizeBytes = 1024,
            FileHash = $"hash-{modelId}",
            FileFormat = ModelFileFormat.STL,
            IsValid = true,
            FolderId = Guid.NewGuid(),
            UploadedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _ = await dbContext.Models3D.AddAsync(model);
        _ = await dbContext.SaveChangesAsync();

        // Act - Physical file does not exist
        HttpResponseMessage response = await _client!.GetAsync($"/api/3d-models/file/{modelId}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "endpoint should return 404 when physical file is missing (orphaned database record)");
    }

    [Fact]
    public async Task GetModelThumbnail_WithInvalidModel_Returns200AndThumbnailContent()
    {
        // Arrange - Create INVALID model with thumbnail
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        SlicerDbContext dbContext = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();
        IConfiguration config = scope.ServiceProvider.GetRequiredService<IConfiguration>();

        var modelId = Guid.NewGuid();
        string modelFileName = $"{modelId}.stl";
        string thumbnailFileName = $"{modelId}.png";
        string modelsPath = config["Slicer:ModelsPath"] ?? "/app/uploads/models";
        Directory.CreateDirectory(modelsPath);

        // Create model file
        string modelPath = Path.Combine(modelsPath, modelFileName);
        await File.WriteAllTextAsync(modelPath, "invalid model");

        // Create thumbnail file
        string thumbnailPath = Path.Combine(modelsPath, thumbnailFileName);
        byte[] thumbnailContent = new byte[] { 0x89, 0x50, 0x4E, 0x47 }; // PNG header
        await File.WriteAllBytesAsync(thumbnailPath, thumbnailContent);

        var invalidModel = new Model3D
        {
            Id = modelId,
            Name = "invalid-with-thumb.stl",
            FileName = modelFileName,
            ThumbnailFileName = thumbnailFileName,
            FilePath = modelsPath,
            FileSizeBytes = 123,
            FileHash = $"hash-{modelId}",
            FileFormat = ModelFileFormat.STL,
            IsValid = false, // CRITICAL: Invalid model
            FolderId = Guid.NewGuid(),
            UploadedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _ = await dbContext.Models3D.AddAsync(invalidModel);
        _ = await dbContext.SaveChangesAsync();

        try
        {
            // Act - REGRESSION TEST: Thumbnail should be accessible even for invalid models
            HttpResponseMessage response = await _client!.GetAsync($"/api/3d-models/thumbnail/{modelId}");

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.OK,
                "REGRESSION: Thumbnail endpoint must serve files regardless of IsValid status");

            response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");

            byte[] downloadedContent = await response.Content.ReadAsByteArrayAsync();
            downloadedContent.Should().BeEquivalentTo(thumbnailContent);
        }
        finally
        {
            if (File.Exists(modelPath))
            {
                File.Delete(modelPath);
            }
            if (File.Exists(thumbnailPath))
            {
                File.Delete(thumbnailPath);
            }
        }
    }
}
