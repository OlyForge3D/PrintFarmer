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
        _client = await _factory.CreateAuthenticatedClientAsync();
    }

    public Task DisposeAsync()
    {
        _client?.Dispose();
        _factory?.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task GetModelFile_WithoutAuthentication_Returns401()
    {
        using HttpClient anonymousClient = _factory.CreateClient();

        HttpResponseMessage response = await anonymousClient.GetAsync(
            $"/api/3d-models/file/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task DownloadForViewer_WithoutAuthentication_Returns401()
    {
        using HttpClient anonymousClient = _factory.CreateClient();

        HttpResponseMessage response = await anonymousClient.GetAsync(
            "/api/3d-models/download-for-viewer?path=missing.stl");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
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
        string modelsPath = config["STORAGE_PATHS:UPLOADS"] ?? Path.Join(Path.GetTempPath(), "slicer_models_fallback");
        Directory.CreateDirectory(modelsPath);
        string filePath = Path.Join(modelsPath, fileName);

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
            response.Content.Headers.ContentType?.MediaType.Should().Be("model/stl");

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
    public async Task DownloadForViewer_WithAuthenticatedRequest_Returns200AndFileContent()
    {
        string modelsPath = GetModelStoragePath();
        string fileName = $"viewer-{Guid.NewGuid():N}.stl";
        string filePath = Path.Join(modelsPath, fileName);
        const string fileContent = "authenticated viewer STL content";
        await File.WriteAllTextAsync(filePath, fileContent);

        try
        {
            HttpResponseMessage response = await _client!.GetAsync(BuildViewerDownloadUrl(fileName));

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Content.Headers.ContentType?.MediaType.Should().Be("model/stl");
            (await response.Content.ReadAsStringAsync()).Should().Be(fileContent);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task DownloadForViewer_WithAuthenticatedMissingFile_Returns404()
    {
        HttpResponseMessage response = await _client!.GetAsync(
            BuildViewerDownloadUrl($"missing-{Guid.NewGuid():N}.stl"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DownloadForViewer_WithTraversalPath_Returns400()
    {
        string modelsPath = GetModelStoragePath();
        string outsideFileName = $"outside-{Guid.NewGuid():N}.stl";
        string outsidePath = Path.Join(Path.GetDirectoryName(modelsPath)!, outsideFileName);
        await File.WriteAllTextAsync(outsidePath, "outside");

        try
        {
            HttpResponseMessage response = await _client!.GetAsync(
                BuildViewerDownloadUrl($"..{Path.DirectorySeparatorChar}{outsideFileName}"));

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
        finally
        {
            File.Delete(outsidePath);
        }
    }

    [Fact]
    public async Task DownloadForViewer_WithAbsolutePath_Returns400()
    {
        string modelsPath = GetModelStoragePath();
        string outsidePath = Path.Join(
            Path.GetDirectoryName(modelsPath)!,
            $"absolute-{Guid.NewGuid():N}.stl");
        await File.WriteAllTextAsync(outsidePath, "outside");

        try
        {
            HttpResponseMessage response = await _client!.GetAsync(
                BuildViewerDownloadUrl(outsidePath));

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
        finally
        {
            File.Delete(outsidePath);
        }
    }

    [Fact]
    public async Task DownloadForViewer_WithSymlinkOutsideStorageRoot_Returns403()
    {
        string modelsPath = GetModelStoragePath();
        string outsidePath = Path.Join(
            Path.GetDirectoryName(modelsPath)!,
            $"symlink-target-{Guid.NewGuid():N}.stl");
        string linkPath = Path.Join(modelsPath, $"symlink-{Guid.NewGuid():N}.stl");
        await File.WriteAllTextAsync(outsidePath, "outside");
        _ = File.CreateSymbolicLink(linkPath, outsidePath);

        try
        {
            HttpResponseMessage response = await _client!.GetAsync(
                BuildViewerDownloadUrl(Path.GetFileName(linkPath)));

            response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }
        finally
        {
            File.Delete(linkPath);
            File.Delete(outsidePath);
        }
    }

    [Fact]
    public async Task DownloadForViewer_WithMultiHopRelativeDirectorySymlinks_Returns403()
    {
        string modelsPath = GetModelStoragePath();
        string outsideDirectory = Path.Join(
            Path.GetDirectoryName(modelsPath)!,
            $"symlink-directory-{Guid.NewGuid():N}");
        string outsideFileName = $"outside-{Guid.NewGuid():N}.stl";
        string outsidePath = Path.Join(outsideDirectory, outsideFileName);
        string secondLinkPath = Path.Join(modelsPath, $"second-link-{Guid.NewGuid():N}");
        string firstLinkPath = Path.Join(modelsPath, $"first-link-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outsideDirectory);
        await File.WriteAllTextAsync(outsidePath, "outside");
        _ = Directory.CreateSymbolicLink(
            secondLinkPath,
            Path.GetRelativePath(modelsPath, outsideDirectory));
        _ = Directory.CreateSymbolicLink(firstLinkPath, Path.GetFileName(secondLinkPath));

        try
        {
            string requestedPath = Path.Join(Path.GetFileName(firstLinkPath), outsideFileName);
            HttpResponseMessage response = await _client!.GetAsync(
                BuildViewerDownloadUrl(requestedPath));

            response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }
        finally
        {
            Directory.Delete(firstLinkPath);
            Directory.Delete(secondLinkPath);
            Directory.Delete(outsideDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadForViewer_WithLinkTargetContainingIntermediateSymlink_Returns403()
    {
        string modelsPath = GetModelStoragePath();
        string outsideDirectory = Path.Join(
            Path.GetDirectoryName(modelsPath)!,
            $"intermediate-target-{Guid.NewGuid():N}");
        string outsideInnerDirectory = Path.Join(outsideDirectory, "inner");
        string outsideFileName = $"outside-{Guid.NewGuid():N}.stl";
        string outsidePath = Path.Join(outsideInnerDirectory, outsideFileName);
        string nestedLinkPath = Path.Join(modelsPath, $"nested-{Guid.NewGuid():N}");
        string bridgeLinkPath = Path.Join(modelsPath, $"bridge-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outsideInnerDirectory);
        await File.WriteAllTextAsync(outsidePath, "outside");
        _ = Directory.CreateSymbolicLink(
            nestedLinkPath,
            Path.GetRelativePath(modelsPath, outsideDirectory));
        _ = Directory.CreateSymbolicLink(
            bridgeLinkPath,
            Path.Join(Path.GetFileName(nestedLinkPath), "inner"));

        try
        {
            string requestedPath = Path.Join(Path.GetFileName(bridgeLinkPath), outsideFileName);
            HttpResponseMessage response = await _client!.GetAsync(
                BuildViewerDownloadUrl(requestedPath));

            response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }
        finally
        {
            Directory.Delete(bridgeLinkPath);
            Directory.Delete(nestedLinkPath);
            Directory.Delete(outsideDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadForViewer_WithSymlinkCycle_Returns403()
    {
        string modelsPath = GetModelStoragePath();
        string firstLinkPath = Path.Join(modelsPath, $"cycle-a-{Guid.NewGuid():N}.stl");
        string secondLinkPath = Path.Join(modelsPath, $"cycle-b-{Guid.NewGuid():N}.stl");
        bool firstLinkCreated = false;
        bool secondLinkCreated = false;

        try
        {
            _ = File.CreateSymbolicLink(firstLinkPath, Path.GetFileName(secondLinkPath));
            firstLinkCreated = true;
            _ = File.CreateSymbolicLink(secondLinkPath, Path.GetFileName(firstLinkPath));
            secondLinkCreated = true;

            HttpResponseMessage response = await _client!.GetAsync(
                BuildViewerDownloadUrl(Path.GetFileName(firstLinkPath)));

            response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }
        finally
        {
            if (secondLinkCreated)
            {
                File.Delete(secondLinkPath);
            }
            if (firstLinkCreated)
            {
                File.Delete(firstLinkPath);
            }
        }
    }

    [Fact]
    public async Task DownloadForViewer_WithExcessiveSymlinkDepth_Returns403()
    {
        const int linkCount = 65;
        string modelsPath = GetModelStoragePath();
        string targetPath = Path.Join(modelsPath, $"depth-target-{Guid.NewGuid():N}.stl");
        string[] linkPaths = new string[linkCount];
        for (int index = 0; index < linkPaths.Length; index++)
        {
            linkPaths[index] = Path.Join(
                modelsPath,
                $"depth-{index:D2}-{Guid.NewGuid():N}.stl");
        }

        List<string> createdLinks = [];
        await File.WriteAllTextAsync(targetPath, "inside");

        try
        {
            string nextTarget = Path.GetFileName(targetPath);
            for (int index = linkPaths.Length - 1; index >= 0; index--)
            {
                _ = File.CreateSymbolicLink(linkPaths[index], nextTarget);
                createdLinks.Add(linkPaths[index]);
                nextTarget = Path.GetFileName(linkPaths[index]);
            }

            HttpResponseMessage response = await _client!.GetAsync(
                BuildViewerDownloadUrl(Path.GetFileName(linkPaths[0])));

            response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }
        finally
        {
            foreach (string linkPath in createdLinks)
            {
                File.Delete(linkPath);
            }
            File.Delete(targetPath);
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
        string modelsPath = config["STORAGE_PATHS:UPLOADS"] ?? Path.Join(Path.GetTempPath(), "slicer_models_fallback");
        Directory.CreateDirectory(modelsPath);
        string filePath = Path.Join(modelsPath, fileName);

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

            response.Content.Headers.ContentType?.MediaType.Should().Be("model/stl");

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
        string modelsPath = config["STORAGE_PATHS:UPLOADS"] ?? Path.Join(Path.GetTempPath(), "slicer_models_fallback");

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
        string modelsPath = config["STORAGE_PATHS:UPLOADS"] ?? Path.Join(Path.GetTempPath(), "slicer_models_fallback");
        Directory.CreateDirectory(modelsPath);

        // Create model file
        string modelPath = Path.Join(modelsPath, modelFileName);
        await File.WriteAllTextAsync(modelPath, "invalid model");

        // Create thumbnail file
        string thumbnailPath = Path.Join(modelsPath, thumbnailFileName);
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

    private string GetModelStoragePath()
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        IConfiguration config = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        return config["STORAGE_PATHS:UPLOADS"]
            ?? throw new InvalidOperationException("Model storage path is not configured for the test host.");
    }

    private static string BuildViewerDownloadUrl(string path)
    {
        return $"/api/3d-models/download-for-viewer?path={Uri.EscapeDataString(path)}";
    }
}
